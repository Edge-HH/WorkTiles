using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WorkTiles.Services;
using Windows.Foundation;

namespace WorkTiles.Controls;

/// <summary>
/// 选中时系统会把文字改成白色，并拿选区底色换掉荧光笔。任何 SelectionHighlightColor 都是这样，量过像素。
/// 选中期间在输入框同一格上盖一份没有选区的原文，再用几何裁剪只露出选中范围，上面铺一层浅灰。
/// 罩层不接收点击，所以拖选、松手和之后的点击都落在原来的输入框上。
/// 系统光标按行盒来画，比即将输入的字号高。只裁掉多出来的那段，不改它自己的位置变换，否则点击挪不动光标。
/// </summary>
internal static class TransparentInputMarkers
{
    private static readonly object MirrorTag = new();
    private static readonly SolidColorBrush VeilBrush = new(Windows.UI.Color.FromArgb(104, 186, 186, 186));
    private static readonly ConditionalWeakTable<Control, Marker> Markers = new();

    public static void Attach(Control editor, Func<bool>? keepSelectionVisible = null)
    {
        if (editor is not (TextBox or RichEditBox)) return;
        editor.Loaded -= OnLoaded;
        editor.Loaded += OnLoaded;
        editor.Unloaded -= OnUnloaded;
        editor.Unloaded += OnUnloaded;
        if (!Markers.TryGetValue(editor, out Marker? marker))
        {
            marker = new Marker(editor, keepSelectionVisible);
            Markers.Add(editor, marker);
        }
        else
        {
            marker.KeepSelectionVisible = keepSelectionVisible;
        }
        if (editor.IsLoaded) marker.HandleLoaded();
    }

    public static void Refresh(Control editor)
    {
        if (Markers.TryGetValue(editor, out Marker? marker)) marker.Update();
    }

    /// <summary>字号改完后立刻重算光标。改字号不一定会触发选区变化。</summary>
    public static void FitCaret(Control editor)
    {
        if (Markers.TryGetValue(editor, out Marker? marker)) marker.QueueFitCaret();
    }

    internal static bool IsOverlayMirror(Control editor) => ReferenceEquals(editor.Tag, MirrorTag);

    internal static FrameworkElement? OverlayHost(Control editor) =>
        Markers.TryGetValue(editor, out Marker? marker) ? marker.Host : null;

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Control editor && Markers.TryGetValue(editor, out Marker? marker))
            marker.HandleLoaded();
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Control editor && Markers.TryGetValue(editor, out Marker? marker))
            marker.HandleUnloaded();
    }

    private sealed class Marker
    {
        private readonly WeakReference<Control> _live;
        private readonly Grid _slot = new();
        // 画布不把子元素的高度算进父级。镜子若是格子里的输入框，内容一变条目就会被它撑高或缩短。
        private readonly Canvas _host = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, Name = "SelectionOverlay" };
        private Point _mirrorOrigin;
        private readonly Canvas _marks = new() { IsHitTestVisible = false };
        private readonly List<Border> _bands = [];
        private readonly List<Rect> _rects = [];
        private Control? _mirror;
        private ScrollViewer? _liveScroll;
        private ScrollViewer? _mirrorScroll;
        private bool _dirty = true;
        private bool _contentReady;
        private bool _shown;
        private bool _updating;
        private bool _pending;
        private bool _finishQueued;
        private bool _rewrapping;
        private bool _scrollHooked;
        private bool _highlightHidden;
        private bool _fittingCaret;
        private bool _caretQueued;
        private bool _caretClipBroken;
        private int _contentVersion;

        public Marker(Control live, Func<bool>? keepSelectionVisible)
        {
            _live = new WeakReference<Control>(live);
            KeepSelectionVisible = keepSelectionVisible;
            _host.Children.Add(_marks);
            if (live is TextBox text)
            {
                text.SelectionChanged += (_, _) => Update();
                text.TextChanged += (_, _) => MarkDirty();
            }
            else if (live is RichEditBox rich)
            {
                rich.SelectionChanged += (_, _) => Update();
                rich.TextChanged += (_, _) => MarkDirty();
            }
            live.GotFocus += (_, _) => Update();
            live.LostFocus += (_, _) => RequestUpdate();
            live.SizeChanged += (_, _) =>
            {
                if (Live() is Control current) MatchHost(current);
                RequestUpdate();
            };
        }

        public Func<bool>? KeepSelectionVisible { get; set; }

        public FrameworkElement? Host => _slot.Parent is not null ? _host : null;

        public void HandleLoaded()
        {
            // 重新加载后允许再尝试裁切光标；一次偶发 COM 异常不应永久禁用该编辑器。
            _caretClipBroken = false;
            if (_rewrapping) return;
            EnsureSlot();
            Update();
        }

        public void HandleUnloaded()
        {
            if (_rewrapping) return;
            Hide();
        }

        private void MarkDirty()
        {
            _contentVersion++;
            _dirty = true;
            RequestUpdate();
        }

        private void RequestUpdate()
        {
            Control? live = Live();
            if (live is null) return;
            if (_updating || _finishQueued)
            {
                _pending = true;
                return;
            }
            _finishQueued = true;
            live.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _finishQueued = false;
                Update();
            });
        }

        private Control? Live() => _live.TryGetTarget(out Control? live) ? live : null;

        /// <summary>等这一轮布局完成再量光标。布局过程中坐标会是 0，缩放过的光标就会整段错位。</summary>
        internal void QueueFitCaret()
        {
            if (_caretQueued) return;
            Control? live = Live();
            if (live is null) return;
            _caretQueued = true;
            live.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _caretQueued = false;
                FitCaret();
            });
        }

        /// <summary>
        /// 系统光标的高度是行盒，汉字只占其中的字号那么高。
        /// 位置变换是文本控件自己的，改了它点击就落不回插入点，还会在输入时把控件打成灾难性故障。
        /// 这里只按字号裁切光标条：正文贴基线，单行标题在行盒里居中。
        /// </summary>
        private void FitCaret()
        {
            if (_caretClipBroken || _fittingCaret) return;
            Control? live = Live();
            if (live is null) return;
            Rectangle? caret = FindCaret(live);
            if (caret is null) return;
            _fittingCaret = true;
            try
            {
                if (!IsCollapsedCaret(live) || !TryEmHeight(live, out double em))
                {
                    if (caret.Clip is not null) caret.Clip = null;
                    return;
                }
                double slot = caret.ActualHeight;
                if (slot < 1 || live.XamlRoot is null || em >= slot - 0.5)
                {
                    if (caret.Clip is not null) caret.Clip = null;
                    return;
                }
                double top = (slot - em) / 2;
                if (live is RichEditBox rich && TryBaseline(rich, out double baseline))
                {
                    try
                    {
                        Point origin = caret.TransformToVisual(live).TransformPoint(new Point(0, 0));
                        top = baseline - origin.Y - em * EmAboveBaseline;
                    }
                    catch (Exception)
                    {
                        top = (slot - em) / 2;
                    }
                }
                double raster = live.XamlRoot.RasterizationScale;
                if (raster < 0.5) raster = 1;
                top = Snap(Math.Clamp(top, 0, slot - em), raster);
                double height = Snap(em, raster);
                if (caret.Clip is RectangleGeometry clip
                    && Math.Abs(clip.Rect.Y - top) < 0.4
                    && Math.Abs(clip.Rect.Height - height) < 0.4)
                    return;
                caret.Clip = new RectangleGeometry
                {
                    Rect = new Rect(0, top, Math.Max(1, caret.ActualWidth), height)
                };
            }
            catch (Exception)
            {
                // 光标条正被文本控件占用时再改会变成未处理异常。这个输入框此后不再裁。
                _caretClipBroken = true;
                try { if (caret.Clip is not null) caret.Clip = null; } catch (Exception) { }
            }
            finally { _fittingCaret = false; }
        }

        /// <summary>汉字黑位大约从基线往上 0.85em。光标用整段 em，顶边对齐字头。</summary>
        private const double EmAboveBaseline = 0.85;

        private static bool IsCollapsedCaret(Control live)
        {
            try
            {
                return live switch
                {
                    TextBox text => text.SelectionLength == 0,
                    RichEditBox rich => rich.Document.Selection.StartPosition == rich.Document.Selection.EndPosition,
                    _ => false
                };
            }
            catch (Exception) { return false; }
        }

        /// <summary>RichEdit 的字号是磅。em 高度换成和画面一致的 DIP，标题框本身就是 DIP。</summary>
        private static bool TryEmHeight(Control live, out double em)
        {
            em = live.FontSize;
            if (live is RichEditBox rich)
            {
                float points = rich.Document.Selection.CharacterFormat.Size;
                if (points is < 1 or > 200) return false;
                em = points * 96.0 / 72.0;
            }
            if (double.IsNaN(em) || em < 1) return false;
            em = Math.Clamp(em, 8, 160);
            return true;
        }

        private static bool TryBaseline(RichEditBox editor, out double baseline)
        {
            baseline = 0;
            try
            {
                ITextSelection selection = editor.Document.Selection;
                ITextRange range = editor.Document.GetRange(selection.StartPosition, selection.StartPosition);
                range.GetPoint(HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Baseline, PointOptions.ClientCoordinates, out Point point);
                if (double.IsNaN(point.Y)) return false;
                baseline = point.Y;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static double Snap(double value, double raster) => Math.Round(value * raster) / raster;

        private static Rectangle? FindCaret(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                if (child is Rectangle rectangle && rectangle.ActualWidth is > 0.2 and < 3 && rectangle.ActualHeight > 4)
                    return rectangle;
                Rectangle? nested = FindCaret(child);
                if (nested is not null) return nested;
            }
            return null;
        }

        private bool ShouldShow(Control live)
        {
            if (live.XamlRoot is null || live.ActualWidth < 1 || live.ActualHeight < 1) return false;
            bool selected = live switch
            {
                TextBox text => text.SelectionLength > 0,
                RichEditBox rich => rich.Document.Selection.EndPosition != rich.Document.Selection.StartPosition,
                _ => false
            };
            if (!selected) return false;
            return live.FocusState != FocusState.Unfocused || KeepSelectionVisible?.Invoke() == true;
        }

        public void Update()
        {
            Control? live = Live();
            if (live is null) return;
            QueueFitCaret();
            if (_updating)
            {
                _pending = true;
                return;
            }
            _updating = true;
            try
            {
                if (!ShouldShow(live))
                {
                    if (_shown) Hide();
                    return;
                }
                EnsureSlot();
                MatchHost(live);
                EnsureMirror(live);
                HideSystemHighlight(live);
                CopyChrome(live);
                HookScroll(live);
                // 镜子放在折叠的格子里不会加载，内容就永远灌不进去。先展开，再等它加载。
                _host.Visibility = Visibility.Visible;
                _shown = true;
                if (_mirror is not { IsLoaded: true }) return;
                if (_dirty)
                {
                    // 整篇镜子还没裁到选区上时先藏住，避免未选中的字叠出第二层。
                    HideMirror();
                    ScheduleFinish();
                }
                else if (_contentReady && !CopyScroll())
                {
                    AlignMirror(live);
                    PlaceMarks(live);
                }
            }
            catch (Exception)
            {
                Hide();
            }
            finally
            {
                _updating = false;
                if (_pending)
                {
                    _pending = false;
                    Update();
                }
            }
        }

        private void ScheduleFinish()
        {
            Control? live = Live();
            if (live is null || _finishQueued) return;
            _finishQueued = true;
            int version = _contentVersion;
            live.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _finishQueued = false;
                FinishSync(version);
            });
        }

        private void FinishSync(int version)
        {
            Control? live = Live();
            if (live is null || _mirror is not { IsLoaded: true }) return;
            try
            {
                if (!ShouldShow(live))
                {
                    Hide();
                    return;
                }
                CopyContent(live);
                _contentReady = true;
                _host.Visibility = Visibility.Visible;
                _shown = true;
                HookScroll(live);
                if (!CopyScroll())
                {
                    AlignMirror(live);
                    PlaceMarks(live);
                }
                if (version == _contentVersion) _dirty = false;
                else ScheduleFinish();
            }
            catch (Exception)
            {
                Hide();
            }
            finally
            {
                if (_pending)
                {
                    _pending = false;
                    Update();
                }
            }
        }

        private void HideMirror()
        {
            if (_mirror is null) return;
            Visual visual = ElementCompositionPreview.GetElementVisual(_mirror);
            visual.Clip = visual.Compositor.CreateInsetClip(10000, 10000, 10000, 10000);
        }

        private void EnsureSlot()
        {
            Control? live = Live();
            if (live is null || _slot.Parent is not null) return;
            if (VisualTreeHelper.GetParent(live) is not Panel parent) return;
            int index = parent.Children.IndexOf(live);
            if (index < 0) return;
            _rewrapping = true;
            try
            {
                // 格子占满原来的单元格，输入框自己的对齐和最大宽度留在输入框上。
                // 标题宽度会在放进树之后再改，罩层跟着它的实际尺寸走。
                _slot.HorizontalAlignment = HorizontalAlignment.Stretch;
                _slot.VerticalAlignment = VerticalAlignment.Stretch;
                Grid.SetRow(_slot, Grid.GetRow(live));
                Grid.SetColumn(_slot, Grid.GetColumn(live));
                Grid.SetRowSpan(_slot, Grid.GetRowSpan(live));
                Grid.SetColumnSpan(_slot, Grid.GetColumnSpan(live));
                parent.Children.Remove(live);
                Grid.SetRow(live, 0);
                Grid.SetColumn(live, 0);
                Grid.SetRowSpan(live, 1);
                Grid.SetColumnSpan(live, 1);
                _slot.Children.Add(live);
                _slot.Children.Add(_host);
                parent.Children.Insert(index, _slot);
                MatchHost(live);
            }
            finally
            {
                _rewrapping = false;
            }
        }

        private void MatchHost(Control live)
        {
            // 罩层永远拉满格子，但自身期望尺寸是 0，不能把条目撑高。
            _host.HorizontalAlignment = HorizontalAlignment.Stretch;
            _host.VerticalAlignment = VerticalAlignment.Stretch;
            _host.Margin = new Thickness(0);
            _host.ClearValue(FrameworkElement.WidthProperty);
            _host.ClearValue(FrameworkElement.HeightProperty);
            if (_slot.Parent is null || live.ActualWidth < 1 || live.ActualHeight < 1) return;
            Point origin;
            try { origin = live.TransformToVisual(_slot).TransformPoint(new Point(0, 0)); }
            catch (Exception) { return; }
            _mirrorOrigin = origin;
            PlaceOverEditor(_mirror, origin, live.ActualWidth, live.ActualHeight);
            PlaceOverEditor(_marks, origin, live.ActualWidth, live.ActualHeight);
        }

        private static void PlaceOverEditor(FrameworkElement? element, Point origin, double width, double height)
        {
            if (element is null) return;
            element.Margin = new Thickness(0);
            element.HorizontalAlignment = HorizontalAlignment.Left;
            element.VerticalAlignment = VerticalAlignment.Top;
            if (double.IsNaN(Canvas.GetLeft(element)) || Math.Abs(Canvas.GetLeft(element) - origin.X) > 0.5)
                Canvas.SetLeft(element, origin.X);
            if (double.IsNaN(Canvas.GetTop(element)) || Math.Abs(Canvas.GetTop(element) - origin.Y) > 0.5)
                Canvas.SetTop(element, origin.Y);
            if (double.IsNaN(element.Width) || Math.Abs(element.Width - width) > 0.5)
                element.Width = width;
            if (double.IsNaN(element.Height) || Math.Abs(element.Height - height) > 0.5)
                element.Height = height;
        }

        private void EnsureMirror(Control live)
        {
            if (_mirror is not null) return;
            _mirror = live switch
            {
                RichEditBox => new RichEditBox { IsTabStop = false, IsHitTestVisible = false, IsSpellCheckEnabled = false, Tag = MirrorTag },
                _ => new TextBox { IsTabStop = false, IsHitTestVisible = false, IsSpellCheckEnabled = false, Tag = MirrorTag }
            };
            _mirror.ContextFlyout = null;
            SubjectTileControl.ApplyTransparentInputChrome(_mirror, showSelectionVeil: false);
            _host.Children.Insert(0, _mirror);
            _mirror.Loaded += (_, _) =>
            {
                _mirror.ApplyTemplate();
                _mirrorScroll = FindScrollViewer(_mirror);
                _dirty = true;
                _contentVersion++;
                Update();
            };
        }

        private void CopyChrome(Control live)
        {
            if (_mirror is null) return;
            _mirror.Padding = live.Padding;
            _mirror.FontFamily = live.FontFamily;
            _mirror.FontSize = live.FontSize;
            _mirror.FontWeight = live.FontWeight;
            _mirror.FontStyle = live.FontStyle;
            _mirror.FontStretch = live.FontStretch;
            _mirror.Foreground = live.Foreground;
            _mirror.FlowDirection = live.FlowDirection;
            _mirror.RequestedTheme = live.ActualTheme;
            _mirror.VerticalContentAlignment = live.VerticalContentAlignment;
            _mirror.HorizontalContentAlignment = live.HorizontalContentAlignment;
            _mirror.BorderThickness = new Thickness(0);
            _mirror.MinWidth = 0;
            _mirror.MinHeight = 0;
            if (_mirror.Background is not SolidColorBrush { Color.A: 0 })
                _mirror.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (live is TextBox liveText && _mirror is TextBox mirrorText)
            {
                mirrorText.TextWrapping = liveText.TextWrapping;
                mirrorText.TextAlignment = liveText.TextAlignment;
                mirrorText.AcceptsReturn = liveText.AcceptsReturn;
            }
            else if (live is RichEditBox liveRich && _mirror is RichEditBox mirrorRich)
            {
                mirrorRich.TextWrapping = liveRich.TextWrapping;
                mirrorRich.TextAlignment = liveRich.TextAlignment;
            }
            _liveScroll ??= FindScrollViewer(live);
        }

        private void CopyContent(Control live)
        {
            if (live is RichEditBox liveRich && _mirror is RichEditBox mirrorRich)
            {
                liveRich.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
                mirrorRich.Document.SetText(TextSetOptions.FormatRtf, rtf);
                mirrorRich.Document.Selection.SetRange(0, 0);
                FontService.RebindBundledFont(mirrorRich, FontService.CaptureFallbacks(liveRich));
                mirrorRich.Document.Selection.SetRange(0, 0);
            }
            else if (live is TextBox liveText && _mirror is TextBox mirrorText)
            {
                mirrorText.Text = liveText.Text;
                mirrorText.SelectionLength = 0;
            }
        }

        /// <summary>镜子的滚动要等视图真正跟上再量选区。已经在同一位置时立刻画。</summary>
        private bool CopyScroll()
        {
            if (_liveScroll is null || _mirrorScroll is null) return false;
            double horizontal = _liveScroll.HorizontalOffset;
            double vertical = _liveScroll.VerticalOffset;
            if (Math.Abs(_mirrorScroll.HorizontalOffset - horizontal) < 0.5
                && Math.Abs(_mirrorScroll.VerticalOffset - vertical) < 0.5)
                return false;
            _mirrorScroll.ViewChanged -= OnMirrorScroll;
            _mirrorScroll.ViewChanged += OnMirrorScroll;
            bool changing = _mirrorScroll.ChangeView(horizontal, vertical, null, true) == true;
            if (!changing)
            {
                _mirrorScroll.ViewChanged -= OnMirrorScroll;
                return false;
            }
            return true;
        }

        private void OnMirrorScroll(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            if (args.IsIntermediate) return;
            if (_mirrorScroll is not null) _mirrorScroll.ViewChanged -= OnMirrorScroll;
            Control? live = Live();
            if (live is null || !ShouldShow(live)) return;
            try
            {
                AlignMirror(live);
                PlaceMarks(live);
            }
            catch (Exception)
            {
                Hide();
            }
        }

        private void PlaceMarks(Control live)
        {
            _rects.Clear();
            Collect(live, _rects);
            int used = 0;
            foreach (Rect rect in _rects)
            {
                if (rect.Width < 0.5 || rect.Height < 0.5) continue;
                if (used == _bands.Count)
                {
                    Border band = new() { Background = VeilBrush, IsHitTestVisible = false };
                    _bands.Add(band);
                    _marks.Children.Add(band);
                }
                Border mark = _bands[used++];
                mark.Visibility = Visibility.Visible;
                Canvas.SetLeft(mark, rect.X);
                Canvas.SetTop(mark, rect.Y);
                mark.Width = rect.Width;
                mark.Height = rect.Height;
            }
            for (int index = used; index < _bands.Count; index++)
                _bands[index].Visibility = Visibility.Collapsed;
            ClipMirror();
        }

        private void ClipMirror()
        {
            if (_mirror is null) return;
            Visual visual = ElementCompositionPreview.GetElementVisual(_mirror);
            Compositor compositor = visual.Compositor;
            if (_rects.Count == 0 || _rects.All(rect => rect.Width < 0.5 || rect.Height < 0.5))
            {
                visual.Clip = compositor.CreateInsetClip(10000, 10000, 10000, 10000);
                return;
            }
            using CanvasPathBuilder builder = new(CanvasDevice.GetSharedDevice());
            foreach (Rect rect in _rects)
            {
                if (rect.Width < 0.5 || rect.Height < 0.5) continue;
                builder.BeginFigure(new Vector2((float)rect.X, (float)rect.Y));
                builder.AddLine(new Vector2((float)rect.Right, (float)rect.Y));
                builder.AddLine(new Vector2((float)rect.Right, (float)rect.Bottom));
                builder.AddLine(new Vector2((float)rect.X, (float)rect.Bottom));
                builder.EndFigure(CanvasFigureLoop.Closed);
            }
            using CanvasGeometry geometry = CanvasGeometry.CreatePath(builder);
            CompositionPathGeometry path = compositor.CreatePathGeometry(new CompositionPath(geometry));
            visual.Clip = compositor.CreateGeometricClip(path);
        }

        private static void Collect(Control live, List<Rect> rects)
        {
            switch (live)
            {
                case TextBox text:
                    CollectText(text, rects);
                    break;
                case RichEditBox rich:
                    CollectRich(rich, rects);
                    break;
            }
            // 字符矩形从文本区左缘算起，文本区比控件靠右一个内边距。不补上的话，选框左边多出一截、右边又盖不住最后一个字。
            Point inset = TextInset(live);
            if (Math.Abs(inset.X) < 0.5 && Math.Abs(inset.Y) < 0.5) return;
            for (int index = 0; index < rects.Count; index++)
            {
                Rect rect = rects[index];
                rects[index] = ClipToControl(live, new Rect(rect.X + inset.X, rect.Y + inset.Y, rect.Width, rect.Height));
            }
        }

        private static Point TextInset(Control editor)
        {
            ScrollViewer? viewer = FindScrollViewer(editor);
            if (viewer is null) return new Point(editor.Padding.Left, editor.Padding.Top);
            try
            {
                Point origin = viewer.TransformToVisual(editor).TransformPoint(new Point(0, 0));
                return new Point(origin.X + viewer.Padding.Left, origin.Y + viewer.Padding.Top);
            }
            catch (Exception)
            {
                return new Point(editor.Padding.Left, editor.Padding.Top);
            }
        }

        private static void CollectText(TextBox box, List<Rect> rects)
        {
            int start = box.SelectionStart;
            int length = box.SelectionLength;
            if (length <= 0) return;
            Rect band = default;
            bool any = false;
            for (int index = 0; index < length; index++)
            {
                if (!TryCharacterRect(box, start + index, out Rect glyph)) continue;
                if (!any || Math.Abs(glyph.Y - band.Y) > 1)
                {
                    if (any) rects.Add(FitTitleLine(box, band));
                    band = glyph;
                    any = true;
                    continue;
                }
                double left = Math.Min(band.X, glyph.X);
                double top = Math.Min(band.Y, glyph.Y);
                double right = Math.Max(band.Right, glyph.Right);
                double bottom = Math.Max(band.Bottom, glyph.Bottom);
                band = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            }
            if (any) rects.Add(FitTitleLine(box, band));
        }

        /// <summary>单行标题报出来的行盒比控件还高。灰条收到字号高度里，并仍留在控件内部。</summary>
        private static Rect FitTitleLine(TextBox box, Rect band)
        {
            double line = Math.Min(box.ActualHeight, Math.Max(1, box.FontSize * 1.2));
            double top = box.VerticalContentAlignment switch
            {
                VerticalAlignment.Top => 0,
                VerticalAlignment.Bottom => Math.Max(0, box.ActualHeight - line),
                _ => Math.Max(0, (box.ActualHeight - line) / 2)
            };
            double bottom = Math.Min(box.ActualHeight, top + line);
            double y = Math.Max(band.Y, top);
            double edge = Math.Min(band.Bottom, bottom);
            if (edge - y < 0.5) return band;
            return new Rect(band.X, y, band.Width, edge - y);
        }

        private static bool TryCharacterRect(TextBox box, int index, out Rect rect)
        {
            rect = default;
            try
            {
                Rect lead = box.GetRectFromCharacterIndex(index, false);
                Rect trail = box.GetRectFromCharacterIndex(index, true);
                double left = Math.Min(lead.X, trail.X);
                double right = Math.Max(Math.Max(lead.Right, trail.Right), Math.Max(lead.X, trail.X));
                double top = Math.Max(0, Math.Min(lead.Y, trail.Y));
                double bottom = Math.Min(box.ActualHeight, Math.Max(lead.Bottom, trail.Bottom));
                left = Math.Max(0, left);
                right = Math.Min(box.ActualWidth, right);
                if (right - left < 0.5 || bottom - top < 0.5) return false;
                rect = new Rect(left, top, right - left, bottom - top);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void CollectRich(RichEditBox editor, List<Rect> rects)
        {
            ITextSelection selection = editor.Document.Selection;
            int start = Math.Min(selection.StartPosition, selection.EndPosition);
            int end = Math.Max(selection.StartPosition, selection.EndPosition);
            int storyEnd = editor.Document.GetRange(0, int.MaxValue).EndPosition;
            if (storyEnd > 0) end = Math.Min(end, storyEnd - 1);
            if (end <= start) return;
            int cursor = start;
            while (cursor < end)
            {
                ITextRange line = editor.Document.GetRange(cursor, cursor);
                line.Expand(TextRangeUnit.Line);
                int lineEnd = line.EndPosition;
                if (lineEnd <= cursor) break;
                int sliceStart = Math.Max(line.StartPosition, start);
                int sliceEnd = Math.Min(lineEnd, end);
                if (storyEnd > 0) sliceEnd = Math.Min(sliceEnd, storyEnd - 1);
                if (sliceEnd > sliceStart)
                    AddRichSlice(editor, editor.Document.GetRange(sliceStart, sliceEnd), line, rects);
                cursor = lineEnd;
            }
        }

        private static void AddRichSlice(RichEditBox editor, ITextRange slice, ITextRange line, List<Rect> rects)
        {
            if (TryRect(slice, out Rect rect) && rect.Width >= 0.5 && rect.Height >= 0.5)
            {
                Rect clipped = ClipToControl(editor, rect);
                if (clipped.Width >= 0.5 && clipped.Height >= 0.5) rects.Add(clipped);
                return;
            }
            Rect lineRect = default;
            bool hasLine = TryRect(line, out lineRect) || TryRect(slice, out lineRect);
            if (!hasLine) return;
            if (lineRect.Height < 0.5) lineRect.Height = Math.Max(1, editor.FontSize * 1.2);
            if (lineRect.Width < 0.5)
            {
                lineRect.X = 0;
                lineRect.Width = Math.Max(1, editor.ActualWidth - editor.Padding.Left - editor.Padding.Right);
            }
            Rect blank = ClipToControl(editor, lineRect);
            if (blank.Width >= 0.5 && blank.Height >= 0.5) rects.Add(blank);
        }

        private static Rect ClipToControl(Control editor, Rect rect)
        {
            double left = Math.Max(0, rect.X);
            double top = Math.Max(0, rect.Y);
            double right = Math.Min(editor.ActualWidth, rect.Right);
            double bottom = Math.Min(editor.ActualHeight, rect.Bottom);
            if (right - left < 0.5 || bottom - top < 0.5) return default;
            return new Rect(left, top, right - left, bottom - top);
        }

        private static bool TryRect(ITextRange range, out Rect rect)
        {
            rect = default;
            try
            {
                range.GetRect(PointOptions.ClientCoordinates, out rect, out _);
                return rect.Height > 0.5;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void HideSystemHighlight(Control live)
        {
            if (_highlightHidden) return;
            SolidColorBrush transparent = new(Microsoft.UI.Colors.Transparent);
            switch (live)
            {
                case TextBox box:
                    box.SelectionHighlightColor = transparent;
                    box.SelectionHighlightColorWhenNotFocused = transparent;
                    break;
                case RichEditBox rich:
                    rich.SelectionHighlightColor = transparent;
                    rich.SelectionHighlightColorWhenNotFocused = transparent;
                    break;
            }
            _highlightHidden = true;
        }

        private void AlignMirror(Control live)
        {
            if (_mirror is null) return;
            if (!TryTextOrigin(live, out Point liveOrigin) || !TryTextOrigin(_mirror, out Point mirrorOrigin)) return;
            double dx = liveOrigin.X - mirrorOrigin.X;
            double dy = liveOrigin.Y - mirrorOrigin.Y;
            if (Math.Abs(dx) > 24 || Math.Abs(dy) > 24) return;
            double left = _mirrorOrigin.X + dx;
            double top = _mirrorOrigin.Y + dy;
            if (!double.IsNaN(Canvas.GetLeft(_mirror)) && Math.Abs(Canvas.GetLeft(_mirror) - left) < 0.05
                && !double.IsNaN(Canvas.GetTop(_mirror)) && Math.Abs(Canvas.GetTop(_mirror) - top) < 0.05)
                return;
            Canvas.SetLeft(_mirror, left);
            Canvas.SetTop(_mirror, top);
        }

        private static bool TryTextOrigin(Control editor, out Point point)
        {
            point = default;
            try
            {
                Rect rect = editor switch
                {
                    TextBox box => box.GetRectFromCharacterIndex(0, false),
                    RichEditBox rich => RichOrigin(rich),
                    _ => default
                };
                if (rect.Height < 1) return false;
                point = new Point(rect.X, rect.Y);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Rect RichOrigin(RichEditBox editor)
        {
            ITextRange range = editor.Document.GetRange(0, 1);
            range.GetRect(PointOptions.ClientCoordinates, out Rect rect, out _);
            return rect;
        }

        private void Hide()
        {
            _host.Visibility = Visibility.Collapsed;
            UnhookScroll();
            _shown = false;
        }

        private void HookScroll(Control live)
        {
            if (_scrollHooked) return;
            live.ApplyTemplate();
            _liveScroll ??= FindScrollViewer(live);
            if (_liveScroll is null) return;
            _liveScroll.ViewChanged += OnLiveScroll;
            _scrollHooked = true;
        }

        private void OnLiveScroll(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            if (args.IsIntermediate) return;
            Control? live = Live();
            if (live is null || !ShouldShow(live)) return;
            try
            {
                if (!CopyScroll())
                {
                    AlignMirror(live);
                    PlaceMarks(live);
                }
            }
            catch (Exception)
            {
                Hide();
            }
        }

        private void UnhookScroll()
        {
            if (_liveScroll is not null) _liveScroll.ViewChanged -= OnLiveScroll;
            if (_mirrorScroll is not null) _mirrorScroll.ViewChanged -= OnMirrorScroll;
            _scrollHooked = false;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                if (child is ScrollViewer viewer) return viewer;
                ScrollViewer? nested = FindScrollViewer(child);
                if (nested is not null) return nested;
            }
            return null;
        }
    }
}
