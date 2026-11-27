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
    /// <summary>桌面磁贴可改用独立外观。未设置时沿用构造时传入的主界面设置。</summary>
    public Func<DesktopTileLook>? LookSource { get; set; }

    private DesktopTileLook CurrentLook(BoardSettingsState fallback) =>
        LookSource?.Invoke() ?? DesktopTileLook.Capture(fallback);
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
    private bool _keyboardNav;
    private RichEditBox? _formattingEditor;
    private bool _aligningParagraph;
    private readonly HashSet<RichEditBox> _paragraphAlignSkipped = [];
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
        Unloaded += (_, _) => FlushPendingText();
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

        PointerPressed += OnTilePointerPressed;
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

    internal (double Width, double Height) MeasureContentSize(double maxWidth = double.PositiveInfinity, bool allowGrow = false)
    {
        // 用实际富文本和附件控件测量，保留换行、字体和图片高度；不把滚动视口当成内容高度。
        _entriesPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _nameEditor.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double contentWidth = _nameEditor.DesiredSize.Width + 110;
        foreach (var child in _entriesPanel.Children)
        {
            if (child is Grid row && FindHomeworkEditor(row) is { } editor)
            {
                contentWidth = Math.Max(contentWidth, RichTextContentMetrics.Width(editor) + 134);
                if (allowGrow) contentWidth = Math.Max(contentWidth, NaturalLineWidth(editor) + 134);
            }
            else if (child is FrameworkElement attachment)
                contentWidth = Math.Max(contentWidth, attachment.DesiredSize.Width + 34);
        }
        // maxWidth 由自动排列的列宽决定：需要一行多放时收窄磁贴，让文字换行而不是溢出。
        // 自动收放不能被当前宽度卡住，否则内容变多时磁贴只会变高、不会变宽。
        double cap = double.IsFinite(maxWidth) ? maxWidth : MaximumTileWidth;
        double limit = allowGrow
            ? Math.Clamp(cap, MinimumTileWidth, MaximumTileWidth)
            : Math.Min(Width, Math.Max(MinimumTileWidth, cap));
        double width = Math.Max(MinimumTileWidth, Math.Min(limit, contentWidth));
        double inkRight = 0, inkBottom = 0;
        foreach (var stroke in _subject.InkStrokes)
        foreach (var point in stroke.Points)
        {
            inkRight = Math.Max(inkRight, point.X + stroke.Thickness * stroke.TipScaleX / 2);
            inkBottom = Math.Max(inkBottom, point.Y + stroke.Thickness * stroke.TipScaleY / 2);
        }
        width = Math.Max(width, inkRight);
        if (allowGrow) width = Math.Min(width, MaximumTileWidth);
        _entriesPanel.Measure(new Size(Math.Max(1, width - 34), double.PositiveInfinity));
        _nameEditor.Measure(new Size(Math.Max(1, width - 110), double.PositiveInfinity));
        double height = Math.Max(MinimumTileHeight, Math.Max(inkBottom,
            _entriesPanel.DesiredSize.Height + _nameEditor.DesiredSize.Height + 38));
        if (allowGrow) height = Math.Min(height, MaximumTileHeight);
        InvalidateMeasure();
        return (width, height);
    }

    private TextBlock? _lineMeasure;

    /// <summary>按不换行测量正文里最长的一行，供桌面磁贴按内容变宽。当前排版宽度只反映已经折行的结果。</summary>
    private double NaturalLineWidth(RichEditBox editor)
    {
        string text = editor.Tag is HomeworkEntry homework ? homework.Content : "";
        if (string.IsNullOrEmpty(text)) return 0;
        _lineMeasure ??= new TextBlock { TextWrapping = TextWrapping.NoWrap };
        _lineMeasure.FontFamily = editor.FontFamily;
        _lineMeasure.FontSize = editor.FontSize;
        _lineMeasure.FontWeight = editor.FontWeight;
        _lineMeasure.FontStyle = editor.FontStyle;
        double max = 0;
        foreach (string line in text.Split('\n', '\r'))
        {
            if (line.Length == 0) continue;
            _lineMeasure.Text = line;
            _lineMeasure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            max = Math.Max(max, _lineMeasure.DesiredSize.Width);
        }
        return max + editor.Padding.Left + editor.Padding.Right + 4;
    }

    public void ApplyAppearance(BoardSettingsState settings, bool applyBodyDefaults = true)
    {
        DesktopTileLook look = CurrentLook(settings);
        ApplyTitleSize(look.TitleSize);
        // 预览拖动只更新标题和背景；正文格式写入 RichEditBox 很贵，不能跟在网格/间隔滑块后面跑。
        if (applyBodyDefaults) ApplyBodyDefaults(settings);
        _nameEditor.MaxWidth = Math.Max(240, Width - 150);
        _watermark.Visibility = look.ShowSubjectLabel ? Visibility.Visible : Visibility.Collapsed;
        _frame.CornerRadius = new CornerRadius(Math.Clamp(look.CornerRadius, 0, 40));
        // 保持透明画刷以接收双击等命中测试，同时不遮挡桌面透明与圆角透底。
        _frame.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _backgroundVisual.Apply(look.Background, BoardTheme.SurfaceBrush, surface: true);
    }

    public void ApplyBodyDefaults(BoardSettingsState settings, bool overrideFormatted = false)
    {
        foreach (Grid row in _entriesPanel.Children.OfType<Grid>())
        {
            if (FindHomeworkEditor(row) is { Tag: HomeworkEntry entry } editor &&
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
            ApplyTitleForeground(_subject.AccentBrush);
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
        PinTitleForeground(editor, editor.Foreground);
    }

    /// <summary>
    /// 学科标题和作业正文直接铺在磁贴背景上。输入框模板在悬停、聚焦和禁用时会改用主题填充色，
    /// 设置页预览、导出预览和桌面透明磁贴都会被这块底色挡住。这些状态改为全透明；
    /// 控件自身保留几乎全透明的背景，空白处仍能点中。
    /// 系统选区会把选中字画成白色并抹掉荧光笔。选中时改由一层浅灰盖住原文，字色和高亮都透出来。
    /// </summary>
    internal static void ApplyTransparentInputChrome(Control editor, bool showSelectionVeil = true, Func<bool>? keepSelectionVisible = null)
    {
        editor.Background = InvisibleHitBrush();
        editor.BorderBrush = InvisibleHitBrush();
        editor.BorderThickness = new Thickness(0);
        foreach (string theme in new[] { "Light", "Dark", "HighContrast" })
        {
            ResourceDictionary dictionary = new() { [LocalThemeMarker] = true };
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
        if (showSelectionVeil) TransparentInputMarkers.Attach(editor, keepSelectionVisible);
    }

    private static SolidColorBrush InvisibleHitBrush() => new(Windows.UI.Color.FromArgb(1, 0, 0, 0));

    /// <summary>标题前景跟着学科色走。模板在悬停、聚焦和禁用时会改读这些主题画刷，不钉住就会变成白色。</summary>
    private void ApplyTitleForeground(Brush brush)
    {
        _nameEditor.Foreground = brush;
        PinTitleForeground(_nameEditor, brush);
    }

    private static readonly string[] TitleForegroundKeys =
    [
        "TextControlForeground",
        "TextControlForegroundPointerOver",
        "TextControlForegroundFocused",
        "TextControlForegroundDisabled"
    ];

    private const string LocalThemeMarker = "WorkTilesLocalTheme";

    private static void PinTitleForeground(TextBox editor, Brush brush)
    {
        if (brush is not SolidColorBrush solid) return;
        foreach (string theme in new[] { "Light", "Dark", "HighContrast" })
            WriteForeground(LocalThemeDictionary(editor, theme), solid.Color);
        WriteForeground(editor.Resources, solid.Color);
    }

    /// <summary>只往这个控件自己建的主题字典里写。取出来的系统主题字典是只读的，里面的画刷也不能改颜色。</summary>
    private static ResourceDictionary LocalThemeDictionary(FrameworkElement editor, string theme)
    {
        if (editor.Resources.ThemeDictionaries[theme] is ResourceDictionary dictionary && dictionary.ContainsKey(LocalThemeMarker))
            return dictionary;
        ResourceDictionary created = new() { [LocalThemeMarker] = true };
        editor.Resources.ThemeDictionaries[theme] = created;
        return created;
    }

    private static void WriteForeground(ResourceDictionary dictionary, Windows.UI.Color color)
    {
        // 字典里查不到本地项时，索引会拿到主题里那支冻结画刷。改它的 Color 会抛 UnauthorizedAccessException。
        // 每次放一支新的，只盖住这个输入框自己的状态色。
        foreach (string key in TitleForegroundKeys)
            dictionary[key] = new SolidColorBrush(color);
    }

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

    private bool _entriesRebuildPending;
    private bool _lostFocusHooked;
    private bool _localEntryEdit;

    private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        // 按钮在这块磁贴里。这一份必须马上改行，不能等失焦。另一份正在组字时只推迟，不拆编辑器。
        if (_localEntryEdit)
        {
            bool rebuild = _entriesRebuildPending || !TryApplyEntryDelta(e);
            if (rebuild) RebuildEntries();
            else _modelStamp = ModelStamp();
            NoteEntryPerf(rebuild ? "rebuild" : "delta", e.Action, started);
            return;
        }
        // 视觉树已经和模型脱节，等失焦后的整表重建一次收齐，不要在旧树上打补丁。
        if (_entriesRebuildPending)
        {
            NoteEntryPerf("pending-hold", e.Action, started);
            return;
        }
        if (ContainsFocus())
        {
            DeferEntryRebuild();
            NoteEntryPerf("defer", e.Action, started);
            return;
        }
        if (TryApplyEntryDelta(e))
        {
            _modelStamp = ModelStamp();
            NoteEntryPerf("delta", e.Action, started);
            return;
        }
        RebuildEntries();
        NoteEntryPerf("rebuild", e.Action, started);
    }

    private static void NoteEntryPerf(string path, NotifyCollectionChangedAction action, long started) =>
        System.Diagnostics.Debug.WriteLine(
            $"[PERF-a4f2] entries {path} {action} {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}ms");

    private void DeferEntryRebuild()
    {
        _entriesRebuildPending = true;
        if (_lostFocusHooked) return;
        _lostFocusHooked = true;
        LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_entriesRebuildPending || ContainsFocus()) return;
            RebuildEntries();
        });
    }

    /// <summary>单条添加或删除只改对应的行。其它变化返回 false，由调用方整表重建。</summary>
    private bool TryApplyEntryDelta(NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0
                && e.NewItems is { Count: 1 } && e.NewItems[0] is HomeworkEntry added:
                return InsertEntryVisual(added, e.NewStartingIndex);
            case NotifyCollectionChangedAction.Remove when e.OldItems is { Count: 1 }
                && e.OldItems[0] is HomeworkEntry removed:
                return RemoveEntryVisual(removed);
            default:
                return false;
        }
    }

    private bool InsertEntryVisual(HomeworkEntry homework, int index)
    {
        int at = ChildIndexForRow(index);
        if (at < 0) return false;
        (Grid row, AttachmentArea? images) = BuildEntryVisuals(homework, index + 1);
        _entriesPanel.Children.Insert(at, row);
        if (images is not null) _entriesPanel.Children.Insert(at + 1, images);
        RenumberEntryLabels();
        return true;
    }

    private bool RemoveEntryVisual(HomeworkEntry homework)
    {
        for (int i = 0; i < _entriesPanel.Children.Count; i++)
        {
            if (_entriesPanel.Children[i] is not Grid row) continue;
            if (FindHomeworkEditor(row) is not { Tag: HomeworkEntry entry } editor || !ReferenceEquals(entry, homework)) continue;
            ReleaseEntryEditor(editor);
            _entriesPanel.Children.RemoveAt(i);
            if (i < _entriesPanel.Children.Count && _entriesPanel.Children[i] is AttachmentArea)
                _entriesPanel.Children.RemoveAt(i);
            RenumberEntryLabels();
            return true;
        }
        return false;
    }

    /// <summary>第 index 条作业行在面板里的子元素位置。刚好接在末尾时返回 Count。</summary>
    private int ChildIndexForRow(int rowIndex)
    {
        int seen = 0;
        for (int i = 0; i < _entriesPanel.Children.Count; i++)
        {
            if (_entriesPanel.Children[i] is not Grid row || FindHomeworkEditor(row) is null) continue;
            if (seen == rowIndex) return i;
            seen++;
        }
        return seen == rowIndex ? _entriesPanel.Children.Count : -1;
    }

    private void RenumberEntryLabels()
    {
        int index = 1;
        foreach (Grid row in _entriesPanel.Children.OfType<Grid>())
        {
            if (FindHomeworkEditor(row) is null) continue;
            if (row.Children.Count > 0 && row.Children[0] is TextBlock label)
                label.Text = $"{index}.";
            index++;
        }
    }

    private void ReleaseEntryEditor(RichEditBox editor)
    {
        _pendingRichText.Remove(editor);
        if (_pendingRichText.Count == 0) _richTextTimer?.Stop();
        _paragraphAlignSkipped.Remove(editor);
        if (ReferenceEquals(_formattingEditor, editor)) _formattingEditor = null;
        for (int i = _entryAutofills.Count - 1; i >= 0; i--)
        {
            if (!_entryAutofills[i].Owns(editor)) continue;
            _entryAutofills[i].Flush();
            _entryAutofills[i].Hide();
            _entryAutofills.RemoveAt(i);
        }
    }

    private void RebuildEntries()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _entriesRebuildPending = false;
        // 停手后的富文本还在计时器里。先写回作业，再建新的编辑器，否则新编辑器会读到旧的 RTF，刚打的字和格式会消失。
        FlushPendingText();
        _formattingEditor = null;
        // 重建编辑器前先结算统计，避免未失焦的作业被漏计。
        foreach (HomeworkAutofillInput input in _entryAutofills) input.Flush();
        _entryAutofills.Clear();
        _paragraphAlignSkipped.Clear();
        _titleAutofill?.Hide();
        _entriesPanel.Children.Clear();
        int index = 1;
        foreach (HomeworkEntry homework in _subject.Entries)
        {
            (Grid row, AttachmentArea? images) = BuildEntryVisuals(homework, index);
            _entriesPanel.Children.Add(row);
            if (images is not null) _entriesPanel.Children.Add(images);
            index++;
        }
        _modelStamp = ModelStamp();
        NoteEntryPerf("rebuild-body", NotifyCollectionChangedAction.Reset, started);
    }

    private (Grid Row, AttachmentArea? Images) BuildEntryVisuals(HomeworkEntry homework, int index)
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
                RichEditBox? previous = _formattingEditor;
                _formattingEditor = editor;
                formatting ??= BuildFormattingToolbar(editor, homework);
                FormattingToolbarChanged?.Invoke(formatting, true);
                if (previous is not null && !ReferenceEquals(previous, editor))
                    TransparentInputMarkers.Refresh(previous);
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
        return (row, images);
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
        DesktopTileLook body = CurrentLook(_settings);
        RichEditBox editor = new()
        {
            FontFamily = FontService.Family(body.BodyFontFamily),
            FontSize = Math.Clamp(body.BodyFontSize, 12, 72),
            FontWeight = body.BodyBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = body.BodyItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
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
        ApplyTransparentInputChrome(editor, keepSelectionVisible: () => ReferenceEquals(_formattingEditor, editor));
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
            // 纯文本马上记下，补全和中途保存能看到刚打的字；整篇 RTF 等停手后再导出，避免每个按键都走一遍字体规范化。
            RememberPlainText(editor, homework);
            ScheduleRichCapture(editor, homework);
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

        editor.PointerPressed += (_, _) => _keyboardNav = false;
        editor.SelectionChanged += (_, _) =>
        {
            if (_clampingSelection || !_keyboardNav) return;
            _keyboardNav = false;
            PullSelectionOffParagraphMark(editor);
        };
        editor.TextChanged += (_, _) =>
        {
            if (_aligningParagraph) return;
            AlignTrailingParagraphMark(editor);
        };
        editor.PreviewKeyDown += (_, args) =>
        {
            bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
                or VirtualKey.Home or VirtualKey.End or VirtualKey.A)
                _keyboardNav = true;
            int end = editor.Document.GetRange(0, int.MaxValue).EndPosition;
            if (end <= 1) return;
            int limit = end - 1;
            ITextSelection selection = editor.Document.Selection;
            // 全选和行末方向键会把选区推进段落标记。只收回越过标记的那一端，鼠标落点不动。
            if (ctrl && !shift && args.Key == VirtualKey.A)
            {
                SetSelection(selection, 0, limit);
                _keyboardNav = false;
                args.Handled = true;
                return;
            }
            if (args.Key == VirtualKey.End)
            {
                int anchor = Math.Min(selection.StartPosition, limit);
                SetSelection(selection, shift ? anchor : limit, limit);
                _keyboardNav = false;
                args.Handled = true;
                return;
            }
            if (args.Key is not (VirtualKey.Right or VirtualKey.Down) || selection.EndPosition < limit) return;
            int active = Math.Min(selection.EndPosition, limit);
            int start = Math.Min(selection.StartPosition, limit);
            SetSelection(selection, shift ? start : active, active);
            _keyboardNav = false;
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
            DesktopTileLook look = CurrentLook(_settings);
            ITextRange range = editor.Document.GetRange(0, styleEnd);
            range.CharacterFormat.Name = FontService.RtfFamilyName(look.BodyFontFamily);
            range.CharacterFormat.Size = (float)Math.Clamp(look.BodyFontSize, 12, 72);
            range.CharacterFormat.Bold = look.BodyBold ? FormatEffect.On : FormatEffect.Off;
            range.CharacterFormat.Italic = look.BodyItalic ? FormatEffect.On : FormatEffect.Off;
            range.CharacterFormat.ForegroundColor = BoardTheme.TextColor;
            editor.Document.Selection.CharacterFormat.Name = FontService.RtfFamilyName(look.BodyFontFamily);
            editor.Document.Selection.CharacterFormat.Size = (float)Math.Clamp(look.BodyFontSize, 12, 72);
            editor.Document.Selection.CharacterFormat.Bold = look.BodyBold ? FormatEffect.On : FormatEffect.Off;
            editor.Document.Selection.CharacterFormat.Italic = look.BodyItalic ? FormatEffect.On : FormatEffect.Off;
        }
        finally
        {
            if (readOnly) editor.IsReadOnly = true;
        }
    }

    /// <summary>行末段落标记沿用前一个字的字号，但保持可见，点击行末外侧才能落到这一行。</summary>
    private void AlignTrailingParagraphMark(RichEditBox editor)
    {
        if (_paragraphAlignSkipped.Contains(editor)) return;
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
            bool shown = mark.CharacterFormat.Hidden != FormatEffect.On;
            if (sizeMatches && shown) return;
            if (!sizeMatches)
            {
                mark.CharacterFormat.Name = sample.CharacterFormat.Name;
                mark.CharacterFormat.Size = sample.CharacterFormat.Size;
                mark.CharacterFormat.Bold = sample.CharacterFormat.Bold;
                mark.CharacterFormat.Italic = sample.CharacterFormat.Italic;
                mark.CharacterFormat.Underline = sample.CharacterFormat.Underline;
            }
            if (mark.CharacterFormat.Hidden == FormatEffect.On)
                mark.CharacterFormat.Hidden = FormatEffect.Off;
            // 写回去仍然对不上时就不能再写。选中标题会让这段格式来回触发文本变化，布局循环后进程直接崩掉。
            bool settled = Math.Abs(mark.CharacterFormat.Size - sample.CharacterFormat.Size) < 0.1
                && mark.CharacterFormat.Hidden != FormatEffect.On;
            if (!settled) _paragraphAlignSkipped.Add(editor);
        }
        finally
        {
            _aligningParagraph = false;
            if (readOnly) editor.IsReadOnly = true;
        }
    }

    /// <summary>键盘把选区推进全文最后一个段落标记时，只收回越过标记的那一端。</summary>
    private void PullSelectionOffParagraphMark(RichEditBox editor)
    {
        int end = editor.Document.GetRange(0, int.MaxValue).EndPosition;
        if (end <= 1) return;
        int limit = end - 1;
        ITextSelection selection = editor.Document.Selection;
        if (selection.StartPosition <= limit && selection.EndPosition <= limit) return;
        SetSelection(selection, Math.Min(selection.StartPosition, limit), Math.Min(selection.EndPosition, limit));
    }

    private void SetSelection(ITextSelection selection, int start, int end)
    {
        if (selection.StartPosition == start && selection.EndPosition == end) return;
        _clampingSelection = true;
        try { selection.SetRange(start, end); }
        finally { _clampingSelection = false; }
    }

    /// <summary>点在输入框以外时收起选区。输入框内部的点击仍由控件自己放置光标。</summary>
    private void OnTilePointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (args.OriginalSource is not DependencyObject source || IsInsideEditor(source)) return;
        if (_nameEditor.SelectionLength > 0)
            _nameEditor.Select(_nameEditor.SelectionStart, 0);
        foreach (RichEditBox editor in FindHomeworkEditors())
        {
            ITextSelection selection = editor.Document.Selection;
            if (selection.StartPosition == selection.EndPosition) continue;
            int caret = selection.StartPosition;
            selection.SetRange(caret, caret);
        }
    }

    private static bool IsInsideEditor(DependencyObject source)
    {
        for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox or RichEditBox) return true;
        }
        return false;
    }

    private IEnumerable<RichEditBox> FindHomeworkEditors()
    {
        foreach (RichEditBox editor in FindVisualEditors(_entriesPanel))
        {
            if (editor.Tag is HomeworkEntry) yield return editor;
        }
    }

    /// <summary>透明输入罩层会把编辑器移进 _slot，不能再按直接子级查找；按 Tag 递归找当前行的作业编辑器。</summary>
    private static RichEditBox? FindHomeworkEditor(DependencyObject row)
    {
        foreach (RichEditBox editor in FindVisualEditors(row))
        {
            if (editor.Tag is HomeworkEntry) return editor;
        }
        return null;
    }

    private static IEnumerable<RichEditBox> FindVisualEditors(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is RichEditBox editor) yield return editor;
            foreach (RichEditBox nested in FindVisualEditors(child)) yield return nested;
        }
    }

    private StackPanel BuildFormattingToolbar(RichEditBox editor, HomeworkEntry homework)
    {
        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
        tools.Children.Add(FontService.CreateFontPicker(editor, () => CaptureRichText(editor, homework)));
        tools.Children.Add(FontService.CreateFontSizePicker(editor, () =>
        {
            TransparentInputMarkers.FitCaret(editor);
            CaptureRichText(editor, homework);
        }));
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

    private readonly Dictionary<RichEditBox, HomeworkEntry> _pendingRichText = [];
    private DispatcherTimer? _richTextTimer;

    /// <summary>把还没导出的富文本写回作业。保存、切换项目和卸载磁贴前都要调用，避免最后几十个字只留在输入框里。</summary>
    public void FlushPendingText()
    {
        _richTextTimer?.Stop();
        foreach (var (editor, homework) in _pendingRichText.ToArray()) CaptureRichText(editor, homework);
        _pendingRichText.Clear();
    }

    /// <summary>重建看板前解除对长寿命模型的订阅。否则模型事件持有旧磁贴，整棵视觉树随每次重建永久滞留。</summary>
    public void Detach()
    {
        FlushPendingText();
        _subject.Entries.CollectionChanged -= Entries_CollectionChanged;
        if (_richTextTimer is not null)
        {
            _richTextTimer.Stop();
            _richTextTimer.Tick -= FlushRichTextTick;
            _richTextTimer = null;
        }
    }

    private void ScheduleRichCapture(RichEditBox editor, HomeworkEntry homework)
    {
        // 行已经拆走时不要再排进计时器，否则到点会去读已卸载的文档。
        if (!editor.IsLoaded) return;
        _pendingRichText[editor] = homework;
        _richTextTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _richTextTimer.Tick -= FlushRichTextTick;
        _richTextTimer.Tick += FlushRichTextTick;
        _richTextTimer.Stop();
        _richTextTimer.Start();
    }

    private void FlushRichTextTick(object? sender, object e) => FlushPendingText();

    private void RememberPlainText(RichEditBox editor, HomeworkEntry homework)
    {
        ContentRange(editor).GetText(TextGetOptions.None, out string plain);
        if (homework.Content == plain) return;
        homework.Content = plain;
        _contentChanged();
    }

    private static ITextRange ContentRange(RichEditBox editor)
    {
        ITextRange contentRange = editor.Document.GetRange(0, int.MaxValue);
        if (contentRange.EndPosition > 0)
        {
            // RichEditBox 的最后一个字符是控件维护的段落标记，不属于用户内容。
            contentRange.EndPosition -= 1;
        }
        return contentRange;
    }

    private void CaptureRichText(RichEditBox editor, HomeworkEntry homework)
    {
        _pendingRichText.Remove(editor);
        if (!editor.IsLoaded) return;
        ITextRange contentRange = ContentRange(editor);
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
        _watermark.Foreground = _subject.AccentBrush;
        ApplyTitleForeground(_subject.AccentBrush);
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
        // 悬停和聚焦会把标题前景换成主题色（深色下是白字）。状态色钉在学科色上，光标和悬停都不再变白。
        PinTitleForeground(editor, foreground);
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

    private void AddHomework()
    {
        // 点击已经把焦点移出编辑器。先写回还在计时器里的富文本，不能只靠 150 毫秒后的导出。
        FlushPendingText();
        _localEntryEdit = true;
        try
        {
            // 新增的作业是空内容，编辑框里只显示灰色的“在这里输入作业内容”提示。
            _subject.Entries.Add(new HomeworkEntry());
            _subject.NotifyEntriesChanged();
        }
        finally { _localEntryEdit = false; }
        _contentChanged();
    }

    private void DeleteHomework(HomeworkEntry homework)
    {
        FlushPendingText();
        _localEntryEdit = true;
        try
        {
            _subject.Entries.Remove(homework);
            _subject.NotifyEntriesChanged();
        }
        finally { _localEntryEdit = false; }
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
            // 高频指针相隔不到 0.75 DIP 的点不进入模型，减少 Polyline 重绘、JSON 体积与橡皮擦扫描量。
            Point last = _activeStrokeData.Points[^1];
            double dx = point.X - last.X, dy = point.Y - last.Y;
            if (dx * dx + dy * dy < 0.5625) { e.Handled = true; return; }
            _activeStrokeData.Points.Add(point);
            _activeStrokeShape.Points.Add(point);
        }
        else
        {
            // 越界后重新进入：另起一段，既不丢墨也不画出跨越空隙的连线。
            _activeStrokeData = new InkStrokeData { Color = _inkSettings.Color, Thickness = _inkSettings.Thickness };
            _activeStrokeData.Points.Add(point);
            _activeStrokeData.Points.Add(new Point(point.X + 0.01, point.Y));
            _subject.InkStrokes.Add(_activeStrokeData);
            _activeStrokeShape = InkStrokeVisual.Create(_activeStrokeData);
            _activeStrokeShape.Points.Add(point);
            _activeStrokeShape.Points.Add(new Point(point.X + 0.01, point.Y));
            _inkCanvas.Children.Add(_activeStrokeShape);
            _renderedStrokes[_activeStrokeShape] = _activeStrokeData;
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
        Polyline? shape = null;
        InkStrokeData? stroke = null;
        // 后加入的笔迹在上；命中即停，避免每次移动都扫描全部笔迹。
        foreach (var pair in _renderedStrokes.Reverse())
        {
            double radius = Math.Max(8, pair.Value.Thickness * Math.Max(pair.Value.TipScaleX, pair.Value.TipScaleY));
            if (!InkHit.Contains(pair.Value.Points, point.X, point.Y, radius)) continue;
            shape = pair.Key;
            stroke = pair.Value;
            break;
        }
        if (shape is null || stroke is null) return;
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
                shape.Points.Add(new Point(point.X / Math.Max(0.0001, stroke.TipScaleX), point.Y / Math.Max(0.0001, stroke.TipScaleY)));
            }
            _inkCanvas.Children.Add(shape);
            _renderedStrokes[shape] = stroke;
        }
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
