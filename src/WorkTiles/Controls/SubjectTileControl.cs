using System.Text;
using WorkTiles.Services;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WorkTiles.Models;
using WorkTiles.ViewModels;
using Windows.Foundation;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Windows.System;
using Windows.UI.Core;
using Windows.ApplicationModel.DataTransfer;

namespace WorkTiles.Controls;

/// <summary>
/// Owns every direct manipulation of a subject tile so touch, ink, and layout changes
/// can explicitly lock the parent viewport instead of competing with page scrolling.
/// </summary>
public sealed class SubjectTileControl : Grid
{
    internal const double MinimumTileWidth = 280;
    internal const double MinimumTileHeight = 96;
    internal const double MaximumTileWidth = 900;
    internal const double MaximumTileHeight = 680;
    private const double EdgeHitTarget = 22;
    private const double CornerHitTarget = 34;

    private readonly SubjectBoard _subject;
    private readonly Action<SubjectBoard> _deleteSubject;
    private readonly Action<SubjectBoard> _layoutChanged;
    private readonly Action<SubjectBoard> _layoutCommitted;
    private readonly Action<bool> _interactionChanged;
    private readonly Func<HomeworkEntry, Task> _addAttachment;
    private readonly Action _contentChanged;
    private readonly BoardSettingsState _settings;
    private readonly AutofillService? _autofill;
    private readonly AutofillPopup? _autofillPopup;
    private readonly List<HomeworkAutofillInput> _entryAutofills = [];
    private SubjectAutofillInput? _titleAutofill;
    public event Action<FrameworkElement, bool>? FormattingToolbarChanged;
    private readonly Canvas _inkCanvas = new()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
        ManipulationMode = ManipulationModes.None
    };
    private readonly Dictionary<Polyline, InkStrokeData> _renderedStrokes = [];
    private readonly StackPanel _entriesPanel = new() { Spacing = 8 };
    private readonly StackPanel _editingTools = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBox _nameEditor;
    private readonly Thumb _headerMoveThumb;
    private readonly Border _frame;
    private readonly BackgroundVisual _backgroundVisual = new();
    private readonly TextBlock _watermark;
    private InkToolSettings _inkSettings = new();
    private uint? _inkPointerId;
    public event Action<SubjectBoard>? InkActivated;
    private bool _isEditing;
    private bool _clampingSelection;
    private bool _aligningParagraph;
    private bool _lockBoardGeometry;
    private bool _applyingModel;
    private string _modelStamp = "";
    private bool _isDrawing;
    private bool _isErasing;
    private InkStrokeData? _activeStrokeData;
    private Polyline? _activeStrokeShape;

    public SubjectTileControl(
        SubjectBoard subject,
        Action<SubjectBoard> deleteSubject,
        Action<SubjectBoard> layoutChanged,
        Action<SubjectBoard> layoutCommitted,
        Action<bool> interactionChanged,
        Func<HomeworkEntry, Task> addAttachment,
        Action contentChanged,
        BoardSettingsState settings,
        AutofillService? autofill = null,
        AutofillPopup? autofillPopup = null)
    {
        _subject = subject;
        _deleteSubject = deleteSubject;
        _layoutChanged = layoutChanged;
        _layoutCommitted = layoutCommitted;
        _interactionChanged = interactionChanged;
        _addAttachment = addAttachment;
        _contentChanged = contentChanged;
        _settings = settings;
        _autofill = autofill;
        _autofillPopup = autofillPopup;

        Width = subject.TileWidth;
        Height = subject.TileHeight;
        MinWidth = MinimumTileWidth;
        MinHeight = MinimumTileHeight;
        ManipulationMode = ManipulationModes.None;

        _frame = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = subject.AccentBrush,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(3)
        };
        DoubleTapped += (_, args) =>
        {
            if (_lockBoardGeometry && !_isEditing)
            {
                args.Handled = true;
                DesktopDoubleTapped?.Invoke();
            }
        };
        Children.Add(_frame);

        Grid content = new() { Padding = new Thickness(14, 10, 10, 10) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid layers = new();
        layers.Children.Add(_backgroundVisual);
        layers.Children.Add(content);
        _frame.Child = layers;

        _watermark = new TextBlock
        {
            Text = subject.Watermark,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -10),
            FontSize = 72,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = subject.AccentBrush,
            Opacity = 0.16,
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(_watermark, 3);
        content.Children.Add(_watermark);

        Grid header = new() { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(header);
        Canvas.SetZIndex(header, 30);

        _headerMoveThumb = new Thumb
        {
            Name = "HeaderMoveThumb",
            Height = EdgeHitTarget,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
            ManipulationMode = ManipulationModes.None
        };
        double dragX = 0, dragY = 0;
        _headerMoveThumb.DragStarted += (_, _) =>
        {
            IsMoving = !_lockBoardGeometry;
            HideAutofill();
            dragX = _subject.X; dragY = _subject.Y;
            _interactionChanged(true);
            if (_lockBoardGeometry) DesktopDragStarted?.Invoke();
        };
        _headerMoveThumb.DragDelta += (_, args) =>
        {
            if (_lockBoardGeometry)
            {
                DesktopDragDelta?.Invoke(args.HorizontalChange, args.VerticalChange);
                return;
            }
            // 从原始指针累计位移，避免小幅拖动始终粘在吸附线上。
            dragX += args.HorizontalChange; dragY += args.VerticalChange;
            _subject.X = Math.Max(0, dragX);
            _subject.Y = Math.Max(0, dragY);
            _layoutChanged(_subject);
        };
        _headerMoveThumb.DragCompleted += (_, _) =>
        {
            if (_lockBoardGeometry)
            {
                IsMoving = false;
                _interactionChanged(false);
                DesktopDragCompleted?.Invoke();
                return;
            }
            _layoutCommitted(_subject);
            IsMoving = false;
            _interactionChanged(false);
        };
        // 顶部边缘统一用于移动，并覆盖左右缩放区在顶部的交叉部分。
        Canvas.SetZIndex(_headerMoveThumb, 70);
        Children.Add(_headerMoveThumb);

        _nameEditor = CreateInlineEditor(subject.Name, 29, subject.AccentBrush, true);
        _nameEditor.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _nameEditor.HorizontalAlignment = HorizontalAlignment.Left;
        _nameEditor.MinWidth = 120;
        _nameEditor.MaxWidth = 240;
        _nameEditor.TextChanged += (_, _) =>
        {
            if (_applyingModel) return;
            _subject.Name = _nameEditor.Text;
            _watermark.Text = _subject.Watermark;
            _contentChanged();
        };
        if (_autofill is not null && _autofillPopup is not null)
            // 学科开启随机配色时，每次采纳都换一个预设色；否则使用学科自身的颜色。
            _titleAutofill = new SubjectAutofillInput(_autofill, _autofillPopup, _nameEditor, suggestion => ApplyAccent(_autofill.ResolveSubjectColor(suggestion)));
        header.Children.Add(_nameEditor);

        _editingTools.Children.Add(CreateIconButton(FluentGlyphs.Add, "添加一条作业", (_, _) => AddHomework()));
        _editingTools.Children.Add(CreateThemeButton());

        _editingTools.Children.Add(CreateIconButton(FluentGlyphs.Delete, "删除科目", (_, _) => _deleteSubject(_subject), true));
        Grid.SetColumn(_editingTools, 1);
        header.Children.Add(_editingTools);

        ScrollViewer entriesScroller = new()
        {
            Content = _entriesPanel,
            Margin = new Thickness(0, 8, 4, 4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled
        };
        Grid.SetRow(entriesScroller, 1);
        content.Children.Add(entriesScroller);

        // 画布直接覆盖磁贴，以 DIP 保存坐标；调整磁贴只改变裁剪范围，不缩放笔迹。
        _inkCanvas.IsHitTestVisible = false;
        Canvas.SetZIndex(_inkCanvas, 80);
        Children.Add(_inkCanvas);
        _inkCanvas.SizeChanged += (_, args) => _inkCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        _inkCanvas.PointerPressed += InkCanvas_PointerPressed;
        _inkCanvas.PointerMoved += InkCanvas_PointerMoved;
        _inkCanvas.PointerReleased += InkCanvas_PointerReleased;
        _inkCanvas.PointerCanceled += InkCanvas_PointerReleased;
        _inkCanvas.PointerCaptureLost += InkCanvas_PointerReleased;

        AddResizeHandles();
        _subject.Entries.CollectionChanged += Entries_CollectionChanged;
        RebuildEntries();
        RenderStoredStrokes();
        SetEditing(false);
    }

    public bool IsMoving { get; private set; }

    public bool IsEditing => _isEditing;

    /// <summary>标题编辑框。查看模式验收用来确认焦点和输入外观都已收起。</summary>
    internal TextBox TitleEditor => _nameEditor;

    /// <summary>桌面磁贴拖动标题时通知宿主换顺序，而不是改作业板坐标。</summary>
    public event Action? DesktopDragStarted;
    public event Action<double, double>? DesktopDragDelta;
    public event Action? DesktopDragCompleted;
    /// <summary>桌面磁贴拖边缘时由窗口按光标改尺寸。不能用控件内部的位移，窗口一变下一次位移就反了。</summary>
    public event Action<DesktopResizeEdge>? DesktopResizeStarted;
    public event Action? DesktopResizeDelta;
    public event Action? DesktopResizeCompleted;
    public event Action? DesktopDoubleTapped;

    /// <summary>桌面磁贴不改作业板坐标。尺寸仍可拖，由宿主写回宽高。</summary>
    public bool LockBoardGeometry
    {
        get => _lockBoardGeometry;
        set
        {
            if (_lockBoardGeometry == value) return;
            _lockBoardGeometry = value;
            ApplyEditChrome();
        }
    }

    /// <summary>收起候选浮层；统计只在编辑真正结束时结算，避免滚动时把半截文字计入。</summary>
    public void HideAutofill()
    {
        _titleAutofill?.Hide();
        foreach (HomeworkAutofillInput input in _entryAutofills) input.Hide();
    }

    internal (double Width, double Height) MeasureContentSize(double maxWidth = double.PositiveInfinity)
    {
        // 用实际富文本和附件控件测量，保留换行、字体和图片高度；不把滚动视口当成内容高度。
        _entriesPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _nameEditor.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double contentWidth = _nameEditor.DesiredSize.Width + 110;
        foreach (var child in _entriesPanel.Children)
        {
            if (child is Grid row && row.Children.OfType<RichEditBox>().FirstOrDefault() is { } editor)
                contentWidth = Math.Max(contentWidth, RichTextContentMetrics.Width(editor) + 134);
            else if (child is FrameworkElement attachment)
                contentWidth = Math.Max(contentWidth, attachment.DesiredSize.Width + 34);
        }
        // maxWidth 由自动排列的列宽决定：需要一行多放时收窄磁贴，让文字换行而不是溢出。
        double limit = Math.Min(Width, Math.Max(MinimumTileWidth, maxWidth));
        double width = Math.Max(MinimumTileWidth, Math.Min(limit, contentWidth));
        double inkRight = 0, inkBottom = 0;
        foreach (var stroke in _subject.InkStrokes)
        foreach (var point in stroke.Points)
        {
            inkRight = Math.Max(inkRight, point.X + stroke.Thickness * stroke.TipScaleX / 2);
            inkBottom = Math.Max(inkBottom, point.Y + stroke.Thickness * stroke.TipScaleY / 2);
        }
        width = Math.Max(width, inkRight);
        _entriesPanel.Measure(new Size(Math.Max(1, width - 34), double.PositiveInfinity));
        _nameEditor.Measure(new Size(Math.Max(1, width - 110), double.PositiveInfinity));
        double height = Math.Max(MinimumTileHeight, Math.Max(inkBottom,
            _entriesPanel.DesiredSize.Height + _nameEditor.DesiredSize.Height + 38));
        InvalidateMeasure();
        return (width, height);
    }

    public void ApplyAppearance(BoardSettingsState settings, bool applyBodyDefaults = true)
    {
        ApplyTitleSize(settings.TileTitleSize);
        // 预览拖动只更新标题和背景；正文格式写入 RichEditBox 很贵，不能跟在网格/间隔滑块后面跑。
        if (applyBodyDefaults) ApplyBodyDefaults(settings);
        _nameEditor.MaxWidth = Math.Max(240, Width - 150);
        _watermark.Visibility = settings.ShowTileSubjectLabel ? Visibility.Visible : Visibility.Collapsed;
        _frame.CornerRadius = new CornerRadius(Math.Clamp(settings.TileCornerRadius, 0, 40));
        // 保持透明画刷以接收双击等命中测试，同时不遮挡桌面透明与圆角透底。
        _frame.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _backgroundVisual.Apply(settings.TileBackground, BoardTheme.SurfaceBrush, surface: true);
    }

    public void ApplyBodyDefaults(BoardSettingsState settings, bool overrideFormatted = false)
    {
        foreach (Grid row in _entriesPanel.Children.OfType<Grid>())
        {
            if (row.Children.OfType<RichEditBox>().FirstOrDefault() is { Tag: HomeworkEntry entry } editor &&
                editor.IsLoaded &&
                (overrideFormatted || string.IsNullOrWhiteSpace(entry.RtfContent)))
            {
                ApplyDefaultBodyStyle(editor, entry.Content.Length);
            }
        }
    }

    // 字号拖动只触发布局，不重建背景媒体和毛玻璃合成资源。
    public void ApplyTitleSize(double size) => _nameEditor.FontSize = size;

    public void ApplyModelLayout()
    {
        Width = _subject.TileWidth;
        Height = _subject.TileHeight;
        // 标题换行上限跟随磁贴宽度：测量和真实布局用同一个宽度，自动排列复测才不会低估标题高度。
        _nameEditor.MaxWidth = Math.Max(240, Width - 150);
    }

    /// <summary>去掉圆角，避免窗口直角和磁贴圆角之间露出一圈底色。</summary>
    public void UseFlushFrame() => _frame.CornerRadius = new CornerRadius(0);

    /// <summary>只改这块控件的显示尺寸，不写回科目的宽高。桌面窗口比磁贴矮时用来夹进工作区。</summary>
    public void FitDisplaySize(double width, double height)
    {
        // 桌面缩放会小于磁贴最小尺寸。不放开 MinWidth，缩小后控件仍停在 280，窗口里就只露出一块。
        MinWidth = 0;
        MinHeight = 0;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _nameEditor.MaxWidth = Math.Max(40, Width - 150);
    }

    /// <summary>另一块界面改了同一科目时，把没有焦点的这份刷成模型里的内容。</summary>
    public void ReloadFromModel()
    {
        if (ContainsFocus() || ModelStamp() == _modelStamp) return;
        _applyingModel = true;
        try
        {
            if (!string.Equals(_nameEditor.Text, _subject.Name, StringComparison.Ordinal))
                _nameEditor.Text = _subject.Name;
            _watermark.Text = _subject.Watermark;
            _watermark.Foreground = _subject.AccentBrush;
            _frame.BorderBrush = _subject.AccentBrush;
            _nameEditor.Foreground = _subject.AccentBrush;
            RebuildEntries();
            RenderStoredStrokes();
            _modelStamp = ModelStamp();
        }
        finally { _applyingModel = false; }
    }

    public void SetEditing(bool editing)
    {
        // 先把焦点移出磁贴，再拆掉作业编辑框。否则焦点会落到标题框上，
        // 查看模式里就剩一个关不掉的输入框。
        if (_isEditing && !editing) ReleaseContainedFocus();
        _isEditing = editing;
        if (!editing) HideAutofill();
        ApplyEditChrome();
        SetInkMode(false, _inkSettings);
        RebuildEntries();
    }

    private void ApplyEditChrome()
    {
        _editingTools.Visibility = _isEditing ? Visibility.Visible : Visibility.Collapsed;
        foreach (Thumb thumb in Children.OfType<Thumb>())
            thumb.Visibility = _isEditing ? Visibility.Visible : Visibility.Collapsed;
        ApplyInlineEditorChrome(_nameEditor, _isEditing);
    }

    /// <summary>
    /// 查看模式的标题不能再接收焦点，也不能留下聚焦时的白底和强调色下划线。
    /// 编辑时同样保持透明：悬停、聚焦和禁用会改用主题填充色，挡住磁贴底图和毛玻璃。
    /// </summary>
    private static void ApplyInlineEditorChrome(TextBox editor, bool editing)
    {
        editor.IsReadOnly = !editing;
        editor.IsTabStop = editing;
        editor.IsHitTestVisible = editing;
        ApplyTransparentInputChrome(editor);
    }

    /// <summary>
    /// 学科标题和作业正文直接铺在磁贴背景上。输入框模板在悬停、聚焦和禁用时会改用主题填充色，
    /// 设置页预览、导出预览和桌面透明磁贴都会被这块底色挡住。这些状态改为全透明；
    /// 控件自身保留几乎全透明的背景，空白处仍能点中。
    /// 系统选区会把选中字画成白色并抹掉荧光笔。选中时改由一层浅灰盖住原文，字色和高亮都透出来。
    /// </summary>
    internal static void ApplyTransparentInputChrome(Control editor, bool showSelectionVeil = true)
    {
        editor.Background = InvisibleHitBrush();
        editor.BorderBrush = InvisibleHitBrush();
        editor.BorderThickness = new Thickness(0);
        foreach (string theme in new[] { "Light", "Dark", "HighContrast" })
        {
            ResourceDictionary dictionary = new();
            foreach (string key in InlineEditorChromeKeys)
                dictionary[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            dictionary["TextControlBorderThemeThickness"] = new Thickness(0);
            dictionary["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
            editor.Resources.ThemeDictionaries[theme] = dictionary;
        }
        foreach (string key in InlineEditorChromeKeys)
            editor.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        editor.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        editor.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        if (showSelectionVeil) TransparentInputMarkers.Attach(editor);
    }

    private static SolidColorBrush InvisibleHitBrush() => new(Windows.UI.Color.FromArgb(1, 0, 0, 0));

    private static readonly string[] InlineEditorChromeKeys =
    [
        "TextControlBackground",
        "TextControlBackgroundPointerOver",
        "TextControlBackgroundFocused",
        "TextControlBackgroundDisabled",
        "TextControlBorderBrush",
        "TextControlBorderBrushPointerOver",
        "TextControlBorderBrushFocused",
        "TextControlBorderBrushDisabled"
    ];

    private void ReleaseContainedFocus()
    {
        _nameEditor.IsTabStop = false;
        if (!ContainsFocus() || XamlRoot?.Content is not UIElement root) return;
        if (!root.Focus(FocusState.Programmatic))
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
    }

    private bool ContainsFocus()
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused) return false;
        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, this)) return true;
        return false;
    }

    private string ModelStamp()
    {
        StringBuilder stamp = new();
        stamp.Append(_subject.Name).Append('\u001f').Append(_subject.AccentHex).Append('\u001f').Append(_subject.InkStrokes.Count);
        foreach (HomeworkEntry entry in _subject.Entries)
        {
            stamp.Append('\u001e').Append(entry.Id).Append('\u001f').Append(entry.Content).Append('\u001f').Append(entry.RtfContent)
                .Append('\u001f').Append(entry.HasHandwriting);
            foreach (AttachmentItem attachment in entry.Attachments)
                stamp.Append('\u001f').Append(attachment.Path).Append('@').Append(attachment.Scale).Append(',').Append(attachment.OffsetX)
                    .Append(',').Append(attachment.OffsetY).Append(',').Append(attachment.PositionX).Append(',').Append(attachment.PositionY);
        }
        return stamp.ToString();
    }

    private void AddResizeHandles()
    {
        AddResizeHandle(ResizeEdge.Left, HorizontalAlignment.Left, VerticalAlignment.Stretch, EdgeHitTarget, double.NaN);
        AddResizeHandle(ResizeEdge.Right, HorizontalAlignment.Right, VerticalAlignment.Stretch, EdgeHitTarget, double.NaN);
        AddResizeHandle(ResizeEdge.Bottom, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, EdgeHitTarget);
        AddResizeHandle(ResizeEdge.BottomLeft, HorizontalAlignment.Left, VerticalAlignment.Bottom, CornerHitTarget, CornerHitTarget);
        AddResizeHandle(ResizeEdge.BottomRight, HorizontalAlignment.Right, VerticalAlignment.Bottom, CornerHitTarget, CornerHitTarget);
    }

    private void AddResizeHandle(ResizeEdge edge, HorizontalAlignment horizontal, VerticalAlignment vertical, double width, double height)
    {
        Thumb thumb = new()
        {
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
            ManipulationMode = ManipulationModes.None,
            Tag = edge
        };
        thumb.DragStarted += (_, _) =>
        {
            HideAutofill();
            _interactionChanged(true);
            if (_lockBoardGeometry) DesktopResizeStarted?.Invoke(ToDesktopEdge(edge));
        };
        thumb.DragDelta += (sender, args) =>
        {
            if (_lockBoardGeometry) { DesktopResizeDelta?.Invoke(); return; }
            ResizeHandle_DragDelta(sender, args);
        };
        thumb.DragCompleted += (_, _) =>
        {
            if (_lockBoardGeometry)
            {
                _interactionChanged(false);
                DesktopResizeCompleted?.Invoke();
                return;
            }
            _layoutCommitted(_subject);
            _interactionChanged(false);
        };
        Canvas.SetZIndex(thumb, edge is ResizeEdge.BottomLeft or ResizeEdge.BottomRight ? 60 : 50);
        Children.Add(thumb);
    }

    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeEdge edge = (ResizeEdge)((Thumb)sender).Tag;
        double right = _subject.X + _subject.TileWidth;
        double bottom = _subject.Y + _subject.TileHeight;
        double nextX = _subject.X;
        double nextY = _subject.Y;
        double nextWidth = _subject.TileWidth;
        double nextHeight = _subject.TileHeight;

        if (edge is ResizeEdge.Left or ResizeEdge.BottomLeft)
        {
            nextWidth = Math.Clamp(_subject.TileWidth - e.HorizontalChange, MinimumTileWidth, MaximumTileWidth);
            nextX = Math.Max(0, right - nextWidth);
        }
        else if (edge is ResizeEdge.Right or ResizeEdge.BottomRight)
        {
            nextWidth = Math.Clamp(_subject.TileWidth + e.HorizontalChange, MinimumTileWidth, MaximumTileWidth);
        }

        if (edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
        {
            nextHeight = Math.Clamp(_subject.TileHeight + e.VerticalChange, MinimumTileHeight, MaximumTileHeight);
        }

        _subject.X = nextX;
        _subject.Y = nextY;
        _subject.TileWidth = nextWidth;
        _subject.TileHeight = nextHeight;
        ApplyModelLayout();
        _layoutChanged(_subject);
    }

    private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildEntries();

    private void RebuildEntries()
    {
        // 重建编辑器前先结算统计，避免未失焦的作业被漏计。
        foreach (HomeworkAutofillInput input in _entryAutofills) input.Flush();
        _entryAutofills.Clear();
        _titleAutofill?.Hide();
        _entriesPanel.Children.Clear();
        int index = 1;
        foreach (HomeworkEntry homework in _subject.Entries)
        {
            Grid row = new() { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            RichEditBox editor = CreateRichEditor(homework);
            // 序号和正文第一行共用字号、字体和上沿。正文默认在编辑框里垂直居中，序号会显得偏高。
            editor.VerticalContentAlignment = VerticalAlignment.Top;
            row.Children.Add(new TextBlock
            {
                Text = $"{index}.",
                FontSize = editor.FontSize,
                FontFamily = editor.FontFamily,
                FontWeight = editor.FontWeight,
                FontStyle = editor.FontStyle,
                Foreground = BoardTheme.TextBrush,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, editor.Padding.Top, 6, 0)
            });

            Grid.SetColumn(editor, 1);
            row.Children.Add(editor);

            if (_isEditing)
            {
                StackPanel? formatting = null;
                editor.GotFocus += (_, _) =>
                {
                    // 工具栏由窗口统一承载，只在当前作业获得焦点时创建并切换。
                    formatting ??= BuildFormattingToolbar(editor, homework);
                    FormattingToolbarChanged?.Invoke(formatting, true);
                };
                editor.Unloaded += (_, _) =>
                {
                    if (formatting is not null) FormattingToolbarChanged?.Invoke(formatting, false);
                };
            }

            if (_isEditing)
            {
                StackPanel actions = new() { Orientation = Orientation.Horizontal };
                actions.Children.Add(CreateIconButton(FluentGlyphs.ImageAdd, "添加图片", async (_, _) => await _addAttachment(homework)));
                actions.Children.Add(CreateIconButton(FluentGlyphs.Delete, "删除这条作业", (_, _) => DeleteHomework(homework), true));
                Grid.SetColumn(actions, 2);
                row.Children.Add(actions);
            }

            _entriesPanel.Children.Add(row);
            // 每条作业的图片放进自己的图片区，移动和缩放都不越出这条作业，区域变高时下一条作业自动下移。
            AttachmentArea? images = null;
            foreach (AttachmentItem attachment in homework.Attachments.Where(item => item.Kind == "图片" && !string.IsNullOrWhiteSpace(item.Path)))
            {
                AttachmentImageControl image = new(
                    attachment,
                    () => DeleteAttachment(homework, attachment),
                    _contentChanged,
                    _interactionChanged);
                image.SetEditing(_isEditing);
                images ??= new AttachmentArea { Margin = new Thickness(29, -4, 4, 4) };
                images.Children.Add(image);
            }
            if (images is not null) _entriesPanel.Children.Add(images);
            index++;
        }
        _modelStamp = ModelStamp();
    }

    private void DeleteAttachment(HomeworkEntry homework, AttachmentItem attachment)
    {
        homework.Attachments.Remove(attachment);
        homework.NotifyAttachmentsChanged();
        RebuildEntries();
        _contentChanged();
    }

    private RichEditBox CreateRichEditor(HomeworkEntry homework)
    {
        RichEditBox editor = new()
        {
            FontFamily = FontService.Family(_settings.TileBodyFontFamily),
            FontSize = Math.Clamp(_settings.TileBodyFontSize, 12, 72),
            FontWeight = _settings.TileBodyBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = _settings.TileBodyItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            Padding = new Thickness(2, 0, 2, 0),
            MinHeight = 42,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = !_isEditing,
            IsTabStop = _isEditing,
            IsHitTestVisible = _isEditing,
            VerticalContentAlignment = VerticalAlignment.Top,
            // 空作业只显示灰色提示（颜色见 Themes/ThemeResources.xaml 的 TextControlPlaceholderForeground）；
            // 查看模式没有输入入口，不再显示提示文字。
            PlaceholderText = _isEditing ? HomeworkState.PlaceholderText : string.Empty,
            Tag = homework
        };
        ApplyTransparentInputChrome(editor);
        editor.Paste += async (_, args) =>
        {
            if (!_settings.PastePlainTextOnly) return;
            // RichEditBox 的 Paste 事件同时覆盖 Ctrl+V 和右键菜单；先拦截默认富文本，再只写入剪贴板文本。
            args.Handled = true;
            DataPackageView clipboard = Clipboard.GetContent();
            if (!clipboard.Contains(StandardDataFormats.Text)) return;
            string text = await clipboard.GetTextAsync();
            editor.Document.Selection.SetText(TextSetOptions.None, text);
            CaptureRichText(editor, homework);
        };
        bool documentReady = false;
        editor.TextChanged += (_, _) =>
        {
            if (!documentReady || _aligningParagraph) return;
            CaptureRichText(editor, homework);
        };
        editor.Loaded += (_, _) =>
        {
            if (documentReady) return;
            // WinUI 的只读 RichEdit 文档拒绝 SetText，初始化时短暂解除只读后再恢复。
            bool readOnly = editor.IsReadOnly;
            editor.IsReadOnly = false;
            editor.Document.SetText(string.IsNullOrWhiteSpace(homework.RtfContent) ? TextSetOptions.None : TextSetOptions.FormatRtf,
                string.IsNullOrWhiteSpace(homework.RtfContent) ? homework.Content : DefaultContentColors.AdaptRtf(homework.RtfContent, BoardTheme.IsLight));
            bool repairedTrailingParagraphs = RemoveGeneratedTrailingParagraphs(editor, homework.Content);
            if (string.IsNullOrWhiteSpace(homework.RtfContent))
            {
                // 空作业也要写入默认样式：它决定用户接下来输入的字体、字号和颜色。
                ApplyDefaultBodyStyle(editor, homework.Content.Length);
            }
            // 行末多出来的那一格是控件自己的段落标记。不改它的话，字号停在 15，第一行输入和行末光标都会落回去。
            AlignTrailingParagraphMark(editor);
            FontService.RebindBundledFont(editor, homework.FontFallbacks);
            editor.IsReadOnly = readOnly;
            documentReady = true;
            if (repairedTrailingParagraphs)
            {
                CaptureRichText(editor, homework);
            }
        };
        if (_autofill is not null && _autofillPopup is not null)
        {
            _entryAutofills.Add(new HomeworkAutofillInput(
                _autofill,
                _autofillPopup,
                editor,
                () => _subject.Name,
                () => documentReady,
                () => homework.Content,
                _contentChanged));
        }

        editor.SelectionChanged += (_, _) => ClampSelectionAwayFromParagraphMark(editor);
        // 鼠标拖选时 SelectionChanged 总在画完选区后才纠正，行末那一格会闪一下。拖动过程中持续收选区，松开再收一次。
        editor.PointerMoved += (_, args) =>
        {
            if (args.GetCurrentPoint(editor).Properties.IsLeftButtonPressed)
                ClampSelectionAwayFromParagraphMark(editor);
        };
        editor.PointerReleased += (_, _) => ClampSelectionAwayFromParagraphMark(editor);
        editor.TextChanged += (_, _) =>
        {
            if (_aligningParagraph) return;
            AlignTrailingParagraphMark(editor);
        };
        editor.PreviewKeyDown += (_, args) =>
        {
            bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            // 全选和到行末的方向键都会把选区推进段落标记，提前停在最后一个字，免得选中那一格闪一下。
            bool selectAll = ctrl && args.Key == VirtualKey.A;
            if (!selectAll && args.Key is not (VirtualKey.Right or VirtualKey.End or VirtualKey.Down)) return;
            int end = editor.Document.GetRange(0, int.MaxValue).EndPosition;
            if (end <= 0) return;
            int limit = end - 1;
            var selection = editor.Document.Selection;
            if (selectAll)
            {
                _clampingSelection = true;
                try { selection.SetRange(0, limit); }
                finally { _clampingSelection = false; }
                args.Handled = true;
                return;
            }
            bool extending = args.Key == VirtualKey.End || shift;
            if (!extending && args.Key != VirtualKey.End) return;
            if (selection.EndPosition < limit && args.Key != VirtualKey.End) return;
            _clampingSelection = true;
            try { selection.SetRange(extending ? Math.Min(selection.StartPosition, limit) : limit, limit); }
            finally { _clampingSelection = false; }
            args.Handled = true;
        };
        return editor;
    }

    /// <summary>默认正文样式只覆盖没有保存过富文本格式的内容，避免设置页改默认值时破坏用户已有的局部格式。</summary>
    private void ApplyDefaultBodyStyle(RichEditBox editor, int textLength)
    {
        // WinUI 会拒绝修改只读 RichEditBox 的 CharacterFormat；设置预览和查看模式磁贴需要短暂解锁文档。
        bool readOnly = editor.IsReadOnly;
        if (readOnly) editor.IsReadOnly = false;
        try
        {
            ITextRange story = editor.Document.GetRange(0, int.MaxValue);
            int end = story.EndPosition;
            int styleEnd = Math.Clamp(textLength, 0, end);
            if (styleEnd < end)
            {
                ITextRange tail = editor.Document.GetRange(styleEnd, end);
                tail.GetText(TextGetOptions.None, out string tailText);
                if (tailText.Length > 0 && tailText.All(character => character is '\r' or '\n'))
                    styleEnd = end;
            }
            ITextRange range = editor.Document.GetRange(0, styleEnd);
            range.CharacterFormat.Name = FontService.RtfFamilyName(_settings.TileBodyFontFamily);
            range.CharacterFormat.Size = (float)Math.Clamp(_settings.TileBodyFontSize, 12, 72);
            range.CharacterFormat.Bold = _settings.TileBodyBold ? FormatEffect.On : FormatEffect.Off;
            range.CharacterFormat.Italic = _settings.TileBodyItalic ? FormatEffect.On : FormatEffect.Off;
            range.CharacterFormat.ForegroundColor = BoardTheme.TextColor;
            editor.Document.Selection.CharacterFormat.Name = FontService.RtfFamilyName(_settings.TileBodyFontFamily);
            editor.Document.Selection.CharacterFormat.Size = (float)Math.Clamp(_settings.TileBodyFontSize, 12, 72);
            editor.Document.Selection.CharacterFormat.Bold = _settings.TileBodyBold ? FormatEffect.On : FormatEffect.Off;
            editor.Document.Selection.CharacterFormat.Italic = _settings.TileBodyItalic ? FormatEffect.On : FormatEffect.Off;
        }
        finally
        {
            if (readOnly) editor.IsReadOnly = true;
        }
    }

    /// <summary>行末段落标记沿用前一个字的格式，并藏起来，避免光标再往后一格时字号跳回 15 或闪一下。</summary>
    private void AlignTrailingParagraphMark(RichEditBox editor)
    {
        bool readOnly = editor.IsReadOnly;
        if (readOnly) editor.IsReadOnly = false;
        // 已经对齐过就立刻返回。每次按键都重写格式会让 RichEdit 连续重排，输入会卡到无法使用。
        _aligningParagraph = true;
        try
        {
            ITextRange story = editor.Document.GetRange(0, int.MaxValue);
            int end = story.EndPosition;
            if (end < 2) return;
            ITextRange mark = editor.Document.GetRange(end - 1, end);
            mark.GetText(TextGetOptions.None, out string markText);
            if (markText is not "\r" and not "\n") return;
            ITextRange sample = editor.Document.GetRange(end - 2, end - 1);
            sample.GetText(TextGetOptions.None, out string sampleText);
            if (sampleText is "\r" or "\n" || sample.CharacterFormat.Size is < 8 or > 96) return;
            bool sizeMatches = Math.Abs(mark.CharacterFormat.Size - sample.CharacterFormat.Size) < 0.1;
            if (sizeMatches && mark.CharacterFormat.Hidden == FormatEffect.On) return;
            if (!sizeMatches)
            {
                mark.CharacterFormat.Name = sample.CharacterFormat.Name;
                mark.CharacterFormat.Size = sample.CharacterFormat.Size;
                mark.CharacterFormat.Bold = sample.CharacterFormat.Bold;
                mark.CharacterFormat.Italic = sample.CharacterFormat.Italic;
                mark.CharacterFormat.Underline = sample.CharacterFormat.Underline;
            }
            if (mark.CharacterFormat.Hidden != FormatEffect.On)
                mark.CharacterFormat.Hidden = FormatEffect.On;
        }
        finally
        {
            _aligningParagraph = false;
            if (readOnly) editor.IsReadOnly = true;
        }
    }

    /// <summary>行末那一格是控件自己的段落标记，选区和光标都停在最后一个字上。</summary>
    private void ClampSelectionAwayFromParagraphMark(RichEditBox editor)
    {
        if (_clampingSelection) return;
        ITextSelection selection = editor.Document.Selection;
        int end = editor.Document.GetRange(0, int.MaxValue).EndPosition;
        if (end <= 0) return;
        int limit = end - 1;
        if (selection.StartPosition <= limit && selection.EndPosition <= limit) return;
        _clampingSelection = true;
        try { selection.SetRange(Math.Min(selection.StartPosition, limit), Math.Min(selection.EndPosition, limit)); }
        finally { _clampingSelection = false; }
    }

    private StackPanel BuildFormattingToolbar(RichEditBox editor, HomeworkEntry homework)
    {
        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
        tools.Children.Add(FontService.CreateFontPicker(editor, () => CaptureRichText(editor, homework)));
        tools.Children.Add(FontService.CreateFontSizePicker(editor, () => CaptureRichText(editor, homework)));
        tools.Children.Add(CreateIconButton(FluentGlyphs.Bold, "加粗", (_, _) =>
        {
            editor.Document.Selection.CharacterFormat.Bold = FormatEffect.Toggle;
            CaptureRichText(editor, homework);
        }));
        tools.Children.Add(CreateIconButton(FluentGlyphs.Italic, "斜体", (_, _) =>
        {
            editor.Document.Selection.CharacterFormat.Italic = FormatEffect.Toggle;
            CaptureRichText(editor, homework);
        }));
        tools.Children.Add(CreateIconButton(FluentGlyphs.Underline, "下划线", (_, _) =>
        {
            var format = editor.Document.Selection.CharacterFormat;
            format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
            CaptureRichText(editor, homework);
        }));
        tools.Children.Add(CreateColorFlyoutButton(FluentGlyphs.Color, "文字颜色", editor, homework, false));
        tools.Children.Add(CreateColorFlyoutButton(FluentGlyphs.Highlight, "高光颜色", editor, homework, true));
        foreach (Button button in tools.Children.OfType<Button>())
        {
            // 点击顶栏时保持编辑器的焦点和选区；键盘仍可通过 Tab 访问按钮。
            button.AllowFocusOnInteraction = false;
        }
        return tools;
    }

    private void CaptureRichText(RichEditBox editor, HomeworkEntry homework)
    {
        var contentRange = editor.Document.GetRange(0, int.MaxValue);
        if (contentRange.EndPosition > 0)
        {
            // RichEditBox 的最后一个字符是控件维护的段落标记，不属于用户内容。
            contentRange.EndPosition -= 1;
        }

        contentRange.GetText(TextGetOptions.None, out string plain);
        contentRange.GetText(TextGetOptions.FormatRtf, out string rtf);
        homework.FontFallbacks = FontService.CaptureFallbacks(editor);
        homework.Content = plain;
        homework.RtfContent = DefaultContentColors.AdaptRtf(FontService.NormalizeRtf(rtf.TrimEnd('\0')), BoardTheme.IsLight, saving: true);
        _contentChanged();
    }

    private static bool RemoveGeneratedTrailingParagraphs(RichEditBox editor, string expectedPlainText)
    {
        editor.Document.GetText(TextGetOptions.None, out string loadedText);
        string loadedContent = loadedText.EndsWith('\r') ? loadedText[..^1] : loadedText;
        if (loadedContent.Length <= expectedPlainText.Length ||
            !loadedContent.StartsWith(expectedPlainText, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> unexpectedTail = loadedContent.AsSpan(expectedPlainText.Length);
        foreach (char character in unexpectedTail)
        {
            if (character is not ('\r' or '\n'))
            {
                return false;
            }
        }

        // 旧版本把 RichEditBox 自动段落写进 RTF；纯文本是可信边界，只删除其后的换行。
        var trailingRange = editor.Document.GetRange(expectedPlainText.Length, int.MaxValue);
        trailingRange.SetText(TextSetOptions.None, string.Empty);
        return true;
    }

    private Button CreateColorFlyoutButton(string glyph, string tooltip, RichEditBox editor, HomeworkEntry homework, bool isHighlight)
    {
        int selectionStart = 0;
        int selectionEnd = 0;
        StackPanel colors = new() { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(8) };
        void RefreshSelection()
        {
            var format = editor.Document.GetRange(selectionStart, selectionEnd).CharacterFormat;
            Windows.UI.Color selected = isHighlight ? format.BackgroundColor : format.ForegroundColor;
            foreach (ColorSwatchButton swatch in colors.Children.OfType<ColorSwatchButton>())
                swatch.SetSelected(!selected.Equals(TextConstants.UndefinedColor) &&
                    (swatch.Tag as string == "clear" ? selected.Equals(TextConstants.AutoColor) : swatch.Color.Equals(selected)));
        }
        Button button = CreateIconButton(glyph, tooltip, (_, _) =>
        {
            // Flyout 会夺走编辑器焦点，必须在打开前保存文本选区。
            selectionStart = editor.Document.Selection.StartPosition;
            selectionEnd = editor.Document.Selection.EndPosition;
            RefreshSelection();
        });
        if (isHighlight)
        {
            Button clearHighlight = CreateColorSwatch("#FFFFFF", 30);
            clearHighlight.Tag = "clear";
            ((Grid)clearHighlight.Content).Children.Insert(0, new Line
            {
                X1 = 2, Y1 = 2, X2 = 28, Y2 = 28,
                Stroke = MainViewModel.BrushFromHex("#EF4444"), StrokeThickness = 2
            });
            ToolTipService.SetToolTip(clearHighlight, "取消高光");
            clearHighlight.Click += (_, _) =>
            {
                var range = editor.Document.GetRange(selectionStart, selectionEnd);
                // RichEdit 的自动背景色表示“无高光”；透明黑色会被当成黑色背景持久化。
                range.CharacterFormat.BackgroundColor = TextConstants.AutoColor;
                editor.Document.Selection.SetRange(selectionStart, selectionEnd);
                CaptureRichText(editor, homework);
                RefreshSelection();
                button.Flyout.Hide();
                editor.Focus(FocusState.Programmatic);
            };
            colors.Children.Add(clearHighlight);
        }

        IEnumerable<string> colorHexes = isHighlight
            ? new[] { "#F7F7F9", "#FBBF24", "#F87171", "#60A5FA", "#4ADE80", "#F472B6" }.Select(ColorPalette.Resolve)
            : new[] { "#000000", "#FFFFFF" }.Concat(
                new[] { "#FBBF24", "#F87171", "#60A5FA", "#4ADE80", "#F472B6" }.Select(ColorPalette.Resolve));
        foreach (string hex in colorHexes)
        {
            Button swatch = CreateColorSwatch(hex, 30);
            swatch.Click += (_, _) =>
            {
                var range = editor.Document.GetRange(selectionStart, selectionEnd);
                if (isHighlight) range.CharacterFormat.BackgroundColor = BoardTheme.DisplayContentColor(MainViewModel.BrushFromHex(hex).Color);
                else range.CharacterFormat.ForegroundColor = BoardTheme.DisplayContentColor(MainViewModel.BrushFromHex(hex).Color);
                editor.Document.Selection.SetRange(selectionStart, selectionEnd);
                CaptureRichText(editor, homework);
                RefreshSelection();
                button.Flyout.Hide();
                editor.Focus(FocusState.Programmatic);
            };
            colors.Children.Add(swatch);
        }
        button.Flyout = new Flyout { Content = colors };
        button.Flyout.Opening += (_, _) =>
        {
            // 键盘、自动化及指针打开菜单的事件顺序不同，在 Opening 也捕获一次选区。
            selectionStart = editor.Document.Selection.StartPosition;
            selectionEnd = editor.Document.Selection.EndPosition;
            RefreshSelection();
        };
        return button;
    }

    private Button CreateThemeButton()
    {
        Button button = CreateIconButton(FluentGlyphs.Color, "磁贴主题色", (_, _) => { });
        StackPanel colors = new() { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(8) };
        foreach (string hex in new[] { "#4ADE80", "#818CF8", "#60A5FA", "#FBBF24", "#F472B6", "#2DD4BF", "#F87171" }.Select(ColorPalette.Resolve))
        {
            Button swatch = CreateColorSwatch(hex, 32);
            swatch.Click += (_, _) =>
            {
                ApplyAccent(hex);
                button.Flyout.Hide();
            };
            colors.Children.Add(swatch);
        }
        button.Flyout = new Flyout { Content = colors };
        button.Flyout.Opening += (_, _) =>
        {
            foreach (ColorSwatchButton swatch in colors.Children.OfType<ColorSwatchButton>())
                swatch.SetSelected(swatch.Color.Equals(_subject.AccentBrush.Color));
        };
        return button;
    }

    /// <summary>
    /// 套用学科颜色：边框、标题与水印共用同一个画刷；预设色仍跟随色系切换，自定义色保持不变。
    /// </summary>
    private void ApplyAccent(string hex)
    {
        _subject.IsAccentExplicit = true;
        _subject.AccentHex = hex;
        _subject.AccentBrush = MainViewModel.BrushFromHex(ColorPalette.ResolveAccent(hex, true, ColorPalette.IsMacaron));
        _frame.BorderBrush = _subject.AccentBrush;
        _nameEditor.Foreground = _subject.AccentBrush;
        _watermark.Foreground = _subject.AccentBrush;
        _contentChanged();
    }

    private static ColorSwatchButton CreateColorSwatch(string hex, double size) =>
        new(BoardTheme.DisplayContentColor(MainViewModel.BrushFromHex(hex).Color), size);

    private static TextBox CreateInlineEditor(string text, double fontSize, Brush foreground, bool singleLine)
    {
        TextBox editor = new()
        {
            Text = text, FontSize = fontSize, Foreground = foreground,
            Padding = new Thickness(2, 0, 2, 0), MinHeight = 34,
            AcceptsReturn = !singleLine,
            TextWrapping = singleLine ? TextWrapping.NoWrap : TextWrapping.Wrap
        };
        ApplyTransparentInputChrome(editor);
        return editor;
    }

    private static Button CreateIconButton(string glyph, string tooltip, RoutedEventHandler click, bool danger = false)
    {
        Button button = new()
        {
            Width = 38, Height = 38, Padding = new Thickness(8),
            Background = BoardTheme.ButtonClearBrush, BorderThickness = new Thickness(0),
            Content = new FontIcon
            {
                Glyph = glyph, FontSize = 15,
                Foreground = new SolidColorBrush(danger ? Windows.UI.Color.FromArgb(255, 248, 113, 113) : BoardTheme.TextColor)
            }
        };
        button.Click += click;
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static ToggleButton CreateIconToggle(string glyph, string tooltip)
    {
        ToggleButton button = new()
        {
            Width = 38, Height = 38, Padding = new Thickness(8),
            Background = BoardTheme.ButtonClearBrush, BorderThickness = new Thickness(0),
            Content = new FontIcon
            {
                Glyph = glyph, FontSize = 16,
                Foreground = BoardTheme.TextBrush
            }
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private void AddHomework()
    {
        // 新增的作业是空内容，编辑框里只显示灰色的“在这里输入作业内容”提示。
        _subject.Entries.Add(new HomeworkEntry());
        _subject.NotifyEntriesChanged();
        _contentChanged();
    }

    private void DeleteHomework(HomeworkEntry homework)
    {
        _subject.Entries.Remove(homework);
        _subject.NotifyEntriesChanged();
        _contentChanged();
    }

    public void SetInkMode(bool drawing, InkToolSettings settings)
    {
        _inkSettings = settings;
        _isDrawing = drawing && _isEditing;
        if (_isDrawing) HideAutofill();
        _inkCanvas.IsHitTestVisible = _isDrawing;
        _entriesPanel.IsHitTestVisible = !_isDrawing;
        _editingTools.Visibility = _isEditing && !_isDrawing ? Visibility.Visible : Visibility.Collapsed;
        _nameEditor.IsHitTestVisible = _isEditing && !_isDrawing;
        foreach (Thumb thumb in Children.OfType<Thumb>()) thumb.Visibility = _isEditing && !_isDrawing ? Visibility.Visible : Visibility.Collapsed;
        if (!_isDrawing)
        {
            _inkPointerId = null;
            _activeStrokeData = null;
            _activeStrokeShape = null;
            _isErasing = false;
            _inkCanvas.ReleasePointerCaptures();
        }
    }

    private bool IsInInkBounds(Point point) => point.X >= 0 && point.Y >= 0 && point.X <= ActualWidth && point.Y <= ActualHeight;

    private void InkCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawing || _inkPointerId is not null)
        {
            return;
        }

        Point point = e.GetCurrentPoint(_inkCanvas).Position;
        if (!IsInInkBounds(point)) return;
        _inkPointerId = e.Pointer.PointerId;
        InkActivated?.Invoke(_subject);
        if (_inkSettings.Eraser)
        {
            _isErasing = true;
            EraseStrokeAt(point);
            _inkCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        _activeStrokeData = new InkStrokeData { Color = _inkSettings.Color, Thickness = _inkSettings.Thickness };
        _activeStrokeData.Points.Add(point);
        _activeStrokeData.Points.Add(new Point(point.X + 0.01, point.Y));
        _subject.InkStrokes.Add(_activeStrokeData);
        _activeStrokeShape = InkStrokeVisual.Create(_activeStrokeData);
        _activeStrokeShape.Points.Add(point);
        _activeStrokeShape.Points.Add(new Point(point.X + 0.01, point.Y));
        _inkCanvas.Children.Add(_activeStrokeShape);
        _renderedStrokes[_activeStrokeShape] = _activeStrokeData;
        _inkCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void InkCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_inkPointerId != e.Pointer.PointerId) return;
        Point point = e.GetCurrentPoint(_inkCanvas).Position;
        if (!IsInInkBounds(point))
        {
            // 越界即结束当前笔段，避免重新进入时画出跨越空隙的连接线。
            _activeStrokeData = null; _activeStrokeShape = null;
            return;
        }
        if (_isErasing)
        {
            EraseStrokeAt(point);
        }
        else if (_activeStrokeData is not null && _activeStrokeShape is not null)
        {
            _activeStrokeData.Points.Add(point);
            _activeStrokeShape.Points.Add(point);
        }
        e.Handled = true;
    }

    private void InkCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_inkPointerId != e.Pointer.PointerId) return;
        _inkPointerId = null;
        _activeStrokeData = null;
        _activeStrokeShape = null;
        _isErasing = false;
        _inkCanvas.ReleasePointerCapture(e.Pointer);
        _contentChanged();
        e.Handled = true;
    }

    private void EraseStrokeAt(Point point)
    {
        var hit = _renderedStrokes.LastOrDefault(pair => InkGeometry.HitTest(pair.Value.Points.Select(p => (p.X, p.Y)).ToList(), point.X, point.Y,
            Math.Max(8, pair.Value.Thickness * Math.Max(pair.Value.TipScaleX, pair.Value.TipScaleY))));
        Polyline? shape = hit.Key;
        if (shape is null) return;
        InkStrokeData stroke = hit.Value;
        _renderedStrokes.Remove(shape);
        _subject.InkStrokes.Remove(stroke);
        _inkCanvas.Children.Remove(shape);
        _contentChanged();
    }

    private void RenderStoredStrokes()
    {
        _inkCanvas.Children.Clear();
        _renderedStrokes.Clear();
        foreach (InkStrokeData stroke in _subject.InkStrokes)
        {
            Polyline shape = InkStrokeVisual.Create(stroke);
            foreach (Point point in stroke.Points)
            {
                shape.Points.Add(new Point(point.X / stroke.TipScaleX, point.Y / stroke.TipScaleY));
            }
            _inkCanvas.Children.Add(shape);
            _renderedStrokes[shape] = stroke;
        }
    }

    private void UndoLastStroke()
    {
        if (_subject.InkStrokes.Count == 0) return;
        _subject.InkStrokes.RemoveAt(_subject.InkStrokes.Count - 1);
        RenderStoredStrokes();
        _contentChanged();
    }

    public void ClearStrokes()
    {
        _subject.InkStrokes.Clear();
        RenderStoredStrokes();
        _contentChanged();
    }

    private static DesktopResizeEdge ToDesktopEdge(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Left => DesktopResizeEdge.Left,
        ResizeEdge.Right => DesktopResizeEdge.Right,
        ResizeEdge.Bottom => DesktopResizeEdge.Bottom,
        ResizeEdge.BottomLeft => DesktopResizeEdge.BottomLeft,
        _ => DesktopResizeEdge.BottomRight
    };

    private enum ResizeEdge { Left, Right, Bottom, BottomLeft, BottomRight }
}

public enum DesktopResizeEdge { Left, Right, Bottom, BottomLeft, BottomRight }
