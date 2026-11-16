using System.Runtime.CompilerServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WorkTiles.Controls;

/// <summary>
/// 选中时系统会把文字改成白色，并拿选区底色换掉荧光笔。任何 SelectionHighlightColor 都是这样，量过像素。
/// 选中期间在输入框上盖一份没有选区的原文，再在选中范围铺一层浅灰，字色和高亮都从灰色里透出来。
/// 光标仍是系统原来的长条；没有选区时这层盖子收起。
/// </summary>
internal static class TransparentInputMarkers
{
    private static readonly SolidColorBrush VeilBrush = new(Windows.UI.Color.FromArgb(104, 186, 186, 186));
    private static readonly ConditionalWeakTable<Control, Marker> Markers = new();

    public static void Attach(Control editor)
    {
        if (editor is not (TextBox or RichEditBox)) return;
        if (Markers.TryGetValue(editor, out _)) return;
        Markers.Add(editor, new Marker(editor));
    }

    private sealed class Marker
    {
        private const int MaxLines = 300;
        private readonly Control _live;
        private readonly Popup _popup = new() { IsHitTestVisible = false, ShouldConstrainToRootBounds = false };
        private readonly Grid _host = new() { IsHitTestVisible = false };
        private readonly Canvas _marks = new() { IsHitTestVisible = false };
        private readonly List<Border> _bands = [];
        private readonly List<Rect> _rects = [];
        private readonly List<ScrollViewer> _ancestors = [];
        private Control? _mirror;
        private ScrollViewer? _liveScroll;
        private ScrollViewer? _mirrorScroll;
        private bool _dirty = true;
        private bool _shown;
        private bool _detached;
        private bool _updating;

        public Marker(Control live)
        {
            _live = live;
            _host.Children.Add(_marks);
            _popup.Child = _host;
            live.GotFocus += OnFocus;
            live.LostFocus += OnFocus;
            live.SizeChanged += (_, _) => { _dirty = true; Update(); };
            live.Unloaded += (_, _) => Detach();
            if (live is TextBox text)
            {
                text.SelectionChanged += (_, _) => Update();
                text.TextChanged += (_, _) => { _dirty = true; Update(); };
            }
            else if (live is RichEditBox rich)
            {
                rich.SelectionChanged += (_, _) => Update();
                rich.TextChanged += (_, _) => { _dirty = true; Update(); };
            }
        }

        private void OnFocus(object sender, RoutedEventArgs args) => Update();

        private bool Focused => _live.FocusState != FocusState.Unfocused;

        private bool ShouldShow()
        {
            if (!Focused || _live.XamlRoot is null || _live.ActualWidth < 1 || _live.ActualHeight < 1) return false;
            return _live switch
            {
                TextBox text => text.SelectionLength > 0,
                RichEditBox rich => rich.Document.Selection.EndPosition != rich.Document.Selection.StartPosition,
                _ => false
            };
        }

        private void Update()
        {
            if (_detached || _updating) return;
            _updating = true;
            try
            {
                if (!ShouldShow())
                {
                    Hide();
                    return;
                }
                EnsureMirror();
                Position();
                CopyChrome();
                _popup.IsOpen = true;
                if (_dirty && _mirror is { IsLoaded: true })
                {
                    _dirty = false;
                    CopyContent();
                    _popup.Opacity = 0;
                    _live.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FinishSync);
                }
                else if (_mirror is { IsLoaded: true })
                {
                    CopyScroll();
                    PlaceMarks();
                    _popup.Opacity = 1;
                }
                else
                {
                    _popup.Opacity = 0;
                }
                if (!_shown) HookMovement();
                _shown = true;
            }
            catch (Exception)
            {
                Hide();
            }
            finally
            {
                _updating = false;
            }
        }

        private void FinishSync()
        {
            if (_detached || !ShouldShow()) return;
            CopyScroll();
            PlaceMarks();
            Position();
            _popup.Opacity = 1;
        }

        private void EnsureMirror()
        {
            if (_mirror is not null)
            {
                _popup.XamlRoot = _live.XamlRoot;
                return;
            }
            _mirror = _live switch
            {
                RichEditBox => new RichEditBox { IsTabStop = false, IsHitTestVisible = false, IsSpellCheckEnabled = false },
                _ => new TextBox { IsTabStop = false, IsHitTestVisible = false, IsSpellCheckEnabled = false }
            };
            _mirror.ContextFlyout = null;
            SubjectTileControl.ApplyTransparentInputChrome(_mirror, showSelectionVeil: false);
            _host.Children.Insert(0, _mirror);
            _popup.XamlRoot = _live.XamlRoot;
            _mirror.Loaded += (_, _) =>
            {
                _mirror.ApplyTemplate();
                _mirrorScroll = FindScrollViewer(_mirror);
                _dirty = true;
                Update();
            };
        }

        private void CopyChrome()
        {
            if (_mirror is null) return;
            _host.Width = _live.ActualWidth;
            _host.Height = _live.ActualHeight;
            _mirror.Width = _live.ActualWidth;
            _mirror.Height = _live.ActualHeight;
            _mirror.Padding = _live.Padding;
            _mirror.FontFamily = _live.FontFamily;
            _mirror.FontSize = _live.FontSize;
            _mirror.FontWeight = _live.FontWeight;
            _mirror.FontStyle = _live.FontStyle;
            _mirror.FontStretch = _live.FontStretch;
            _mirror.Foreground = _live.Foreground;
            _mirror.FlowDirection = _live.FlowDirection;
            _mirror.RequestedTheme = _live.ActualTheme;
            _mirror.VerticalContentAlignment = VerticalAlignment.Top;
            _mirror.BorderThickness = new Thickness(0);
            _mirror.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (_live is TextBox liveText && _mirror is TextBox mirrorText)
            {
                mirrorText.TextWrapping = liveText.TextWrapping;
                mirrorText.TextAlignment = liveText.TextAlignment;
                mirrorText.AcceptsReturn = liveText.AcceptsReturn;
            }
            else if (_live is RichEditBox liveRich && _mirror is RichEditBox mirrorRich)
            {
                mirrorRich.TextWrapping = liveRich.TextWrapping;
                mirrorRich.TextAlignment = liveRich.TextAlignment;
            }
            _live.ApplyTemplate();
            _liveScroll ??= FindScrollViewer(_live);
        }

        private void CopyContent()
        {
            if (_live is RichEditBox liveRich && _mirror is RichEditBox mirrorRich)
            {
                liveRich.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
                mirrorRich.Document.SetText(TextSetOptions.FormatRtf, rtf);
                mirrorRich.Document.Selection.SetRange(0, 0);
            }
            else if (_live is TextBox liveText && _mirror is TextBox mirrorText)
            {
                mirrorText.Text = liveText.Text;
                mirrorText.SelectionLength = 0;
            }
        }

        private void CopyScroll()
        {
            if (_liveScroll is null || _mirrorScroll is null) return;
            _mirrorScroll.ChangeView(_liveScroll.HorizontalOffset, _liveScroll.VerticalOffset, null, true);
        }

        private void PlaceMarks()
        {
            _rects.Clear();
            Collect(_rects);
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
        }

        private void Collect(List<Rect> rects)
        {
            switch (_live)
            {
                case TextBox text:
                    CollectText(text, rects);
                    break;
                case RichEditBox rich:
                    CollectRich(rich, rects);
                    break;
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
                Rect glyph = box.GetRectFromCharacterIndex(start + index, false);
                if (glyph.Height < 0.5) continue;
                if (!any || Math.Abs(glyph.Y - band.Y) > 1)
                {
                    if (any) rects.Add(band);
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
            if (any) rects.Add(band);
        }

        private static void CollectRich(RichEditBox editor, List<Rect> rects)
        {
            ITextSelection selection = editor.Document.Selection;
            int start = Math.Min(selection.StartPosition, selection.EndPosition);
            int end = Math.Max(selection.StartPosition, selection.EndPosition);
            if (end <= start) return;
            int cursor = start;
            for (int guard = 0; cursor < end && guard < MaxLines; guard++)
            {
                ITextRange line = editor.Document.GetRange(cursor, cursor);
                line.Expand(TextRangeUnit.Line);
                int lineEnd = line.EndPosition;
                if (lineEnd <= cursor) lineEnd = cursor + 1;
                int sliceStart = Math.Max(line.StartPosition, start);
                int sliceEnd = Math.Min(lineEnd, end);
                if (sliceEnd > sliceStart && TryRect(editor.Document.GetRange(sliceStart, sliceEnd), out Rect rect))
                    rects.Add(rect);
                cursor = lineEnd;
            }
            if (rects.Count == 0 && TryRect(editor.Document.GetRange(start, end), out Rect bounds))
                rects.Add(bounds);
        }

        private static bool TryRect(ITextRange range, out Rect rect)
        {
            rect = default;
            try
            {
                range.GetRect(PointOptions.ClientCoordinates, out rect, out _);
                return rect.Width > 0.5 && rect.Height > 0.5;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Position()
        {
            if (_live.XamlRoot?.Content is not UIElement root) return;
            Point origin = _live.TransformToVisual(root).TransformPoint(new Point(0, 0));
            _popup.HorizontalOffset = origin.X;
            _popup.VerticalOffset = origin.Y;
        }

        private void Hide()
        {
            _popup.IsOpen = false;
            _popup.Opacity = 0;
            if (_shown)
            {
                _live.LayoutUpdated -= OnLayoutUpdated;
                UnhookAncestors();
                if (_liveScroll is not null) _liveScroll.ViewChanged -= OnLiveScroll;
                _shown = false;
            }
        }

        private void HookMovement()
        {
            _live.LayoutUpdated += OnLayoutUpdated;
            _live.ApplyTemplate();
            _liveScroll ??= FindScrollViewer(_live);
            if (_liveScroll is not null) _liveScroll.ViewChanged += OnLiveScroll;
            for (DependencyObject? node = VisualTreeHelper.GetParent(_live); node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is not ScrollViewer viewer || ReferenceEquals(viewer, _liveScroll)) continue;
                viewer.ViewChanged += OnAncestorScroll;
                _ancestors.Add(viewer);
            }
        }

        private void OnLayoutUpdated(object? sender, object args)
        {
            if (_updating || !_shown) return;
            Position();
        }

        private void OnLiveScroll(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            CopyScroll();
            if (ShouldShow()) PlaceMarks();
        }

        private void OnAncestorScroll(object? sender, ScrollViewerViewChangedEventArgs args) => Position();

        private void UnhookAncestors()
        {
            foreach (ScrollViewer viewer in _ancestors)
                viewer.ViewChanged -= OnAncestorScroll;
            _ancestors.Clear();
        }

        private void Detach()
        {
            if (_detached) return;
            _detached = true;
            Hide();
            _popup.IsOpen = false;
            Markers.Remove(_live);
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
