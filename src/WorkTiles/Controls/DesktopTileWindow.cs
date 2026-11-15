using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics;
using WorkTiles.Services;

namespace WorkTiles.Controls;

/// <summary>
/// 桌面组件的统一 WinUI 3 宿主基类：直接由 <see cref="DesktopWidgetHost"/> 承载在无边框、
/// 逐像素透明的 Win32 WS_POPUP 窗口中（不再使用离屏 RenderTargetBitmap + WorkerW 子窗口）。
/// </summary>
internal class DesktopSurfaceWindow
{
    private readonly Grid _root = new() { Background = null };
    private readonly DesktopWidgetHost _host = new();
    private bool _closed;
    private bool _shown;
    private bool _contentReady;
    private bool _holding;
    private int _x;
    private int _y;
    private double _dragOriginX;
    private double _dragOriginY;
    private int _cursorOriginX;
    private int _cursorOriginY;
    private RectInt32 _bounds;
    private bool _hasBounds;
    private bool _surfaceVisible;
    private bool _interactiveWanted;
    private bool _clickThroughWanted;
    private bool _hasBackdrop;
    private bool _backdropGlass;
    private DesktopCornerMode _backdropCorner;
    private Windows.UI.Color _backdropTint;
    private float _backdropTintOpacity;
    private float _backdropLuminosity;
    private string _title = "WorkTilesDesktopWidget";

    protected Grid Root => _root;
    protected DesktopWidgetHost Host => _host;

    public bool AboveTiles
    {
        get => _host.IsAlwaysAboveTiles;
        set => _host.IsAlwaysAboveTiles = value;
    }

    public nint Handle => _host.Handle;

    public event Action? ContentReady;

    /// <summary>先创建并显示透明宿主，等 XamlRoot 就绪后再创建 RichEditBox 等 WinUI 控件。</summary>
    public void EnsureShown()
    {
        if (_closed) return;
        if (_shown)
        {
            if (_contentReady) ContentReady?.Invoke();
            return;
        }
        _shown = true;
        _root.Loaded += (_, _) => _root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_closed || _contentReady) return;
            _contentReady = true;
            ContentReady?.Invoke();
        });
        _host.Create(_root, _title);
        if (_hasBounds)
            _host.MoveAndResize(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height);
        _host.Show();
        _surfaceVisible = Handle != 0;
        ApplyPendingWindowState();
    }

    public RectInt32 PixelBounds => _bounds;

    public string TitleText
    {
        set
        {
            _title = value ?? string.Empty;
            _host.SetTitle(_title);
        }
    }

    public void ApplyTheme(ElementTheme theme)
    {
        _root.RequestedTheme = theme;
        // 根容器必须保持无背景，否则圆角外部会被填成不透明底色。
        _root.Background = null;
    }

    public void Place(int x, int y, int width, int height)
    {
        if (_closed || _holding) return;
        _x = x;
        _y = y;
        RectInt32 next = new(x, y, Math.Max(1, width), Math.Max(1, height));
        if (!_shown) EnsureShown();
        if (!_hasBounds || _bounds.X != next.X || _bounds.Y != next.Y || _bounds.Width != next.Width || _bounds.Height != next.Height)
        {
            _bounds = next;
            _hasBounds = true;
            _host.MoveAndResize(next.X, next.Y, next.Width, next.Height);
        }
    }

    public void SetInteractive(bool interactive)
    {
        if (_closed) return;
        _interactiveWanted = interactive;
        if (Handle != 0) _host.SetInteractive(interactive);
    }

    public void SetClickThrough(bool enabled)
    {
        if (_closed) return;
        _clickThroughWanted = enabled;
        if (Handle != 0) _host.SetClickThrough(enabled);
    }

    public void SetContentOpacity(double opacity)
    {
        if (_closed) return;
        opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
        if (Math.Abs(_root.Opacity - opacity) >= 0.001 || _root.IsHitTestVisible != opacity > 0.01)
        {
            _root.Opacity = opacity;
            _root.IsHitTestVisible = opacity > 0.01;
        }
        // 系统毛玻璃不在控件透明度里。淡出一开始就关掉它，避免按钮消失后玻璃框还留着。
        if (_backdropGlass)
            _host.SetBackdropSuppressed(opacity < 0.98);
    }

    /// <summary>已经创建过的窗口再次显示。第一次仍走 <see cref="EnsureShown"/>。</summary>
    public void ShowSurface()
    {
        if (_closed || _surfaceVisible) return;
        EnsureShown();
        if (Handle == 0) return;
        _host.Show();
        _surfaceVisible = true;
        ApplyPendingWindowState();
    }

    public void SetRegionRadius(int radiusPx) => _host.SetRoundedRegion(radiusPx);

    public void HideSurface()
    {
        if (_closed || !_surfaceVisible) return;
        _host.Hide();
        _surfaceVisible = false;
    }

    public void ConfigureBackdrop(bool useDesktopAcrylic, DesktopCornerMode cornerMode, Windows.UI.Color tintColor, float tintOpacity, float luminosityOpacity)
    {
        if (_closed) return;
        _hasBackdrop = true;
        _backdropGlass = useDesktopAcrylic;
        _backdropCorner = cornerMode;
        _backdropTint = tintColor;
        _backdropTintOpacity = tintOpacity;
        _backdropLuminosity = luminosityOpacity;
        if (Handle != 0)
            _host.ConfigureBackdrop(useDesktopAcrylic, cornerMode, tintColor, tintOpacity, luminosityOpacity);
    }

    private void ApplyPendingWindowState()
    {
        if (Handle == 0) return;
        if (_hasBackdrop)
            _host.ConfigureBackdrop(_backdropGlass, _backdropCorner, _backdropTint, _backdropTintOpacity, _backdropLuminosity);
        _host.SetInteractive(_interactiveWanted);
        _host.SetClickThrough(_clickThroughWanted);
    }

    public void BeginExternalDrag()
    {
        if (_closed) return;
        _holding = true;
        if (_host.TryGetOrigin(out int screenX, out int screenY))
        {
            _x = screenX;
            _y = screenY;
        }
        _dragOriginX = _x;
        _dragOriginY = _y;
        if (DesktopWindowNative.GetCursorPos(out DesktopWindowNative.DesktopPoint cursor))
        {
            _cursorOriginX = cursor.X;
            _cursorOriginY = cursor.Y;
        }
    }

    /// <summary>
    /// 用屏幕光标与按下瞬间的窗口原点算坐标，避免拖拽过程中相对位移正反馈卡顿。
    /// </summary>
    public void FollowCursor()
    {
        if (_closed || !_holding) return;
        if (!DesktopWindowNative.GetCursorPos(out DesktopWindowNative.DesktopPoint cursor)) return;
        _x = (int)Math.Round(_dragOriginX + (cursor.X - _cursorOriginX));
        _y = (int)Math.Round(_dragOriginY + (cursor.Y - _cursorOriginY));
        _host.Move(_x, _y);
        if (_hasBounds)
        {
            _bounds.X = _x;
            _bounds.Y = _y;
        }
    }

    public void EndExternalDrag() => _holding = false;

    public void ResizeTo(int x, int y, int width, int height)
    {
        if (_closed) return;
        _holding = true;
        _x = x;
        _y = y;
        _bounds = new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height));
        _hasBounds = true;
        _host.MoveAndResize(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height);
    }

    public void EndResize() => _holding = false;

    public void CloseSurface()
    {
        if (_closed) return;
        _closed = true;
        _host.Dispose();
    }
}

/// <summary>
/// 一块科目磁贴的桌面窗口：同一块真实 WinUI 窗口同时承担展示与编辑，
/// 圆角外透明透出桌面、无白边、按 Win+D 保持可见。
/// </summary>
internal sealed class DesktopTileWindow : DesktopSurfaceWindow
{
    private readonly Viewbox _zoomBox = new()
    {
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private AutofillPopup? _popup;
    private SubjectTileControl? _tile;
    private bool _editing;

    public DesktopTileWindow()
    {
        Root.Children.Add(_zoomBox);
    }

    public SubjectTileControl? Tile => _tile;

    public event Action? HostDoubleClick;

    public void Attach(SubjectTileControl tile, AutofillPopup popup)
    {
        _tile = tile;
        _popup = popup;
        _zoomBox.Child = tile;
        tile.DesktopDoubleTapped += () => HostDoubleClick?.Invoke();
        popup.AttachTo(Root);
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, args) => _popup.HandleKey(args)), true);
    }

    public void SetEditing(bool editing)
    {
        if (_editing != editing)
        {
            _editing = editing;
            SetInteractive(editing);
        }
        if (_tile is not null && _tile.IsEditing != editing)
            _tile.SetEditing(editing);
    }

    public void ApplyBackgroundSettings(BackgroundSettings style)
    {
        // 非毛玻璃模式由 TransparentDesktopBackdrop + XAML Border.CornerRadius 精确裁剪，圆角外完全 alpha=0；
        // 开启毛玻璃且未铺不透明底图时，启用 DesktopAcrylicBackdrop 真正模糊窗口后方的桌面内容。
        bool hasMedia = style.PlaylistEnabled
            ? style.Playlist.Any(p => !string.IsNullOrWhiteSpace(p))
            : !string.IsNullOrWhiteSpace(style.ImagePath);
        bool useAcrylic = style.Glass && !hasMedia;
        if (!useAcrylic)
        {
            ConfigureBackdrop(false, DesktopCornerMode.None, Microsoft.UI.Colors.Transparent, 0f, 0f);
            return;
        }

        Windows.UI.Color tint = GridAppearance.ParseColor(style.Color, BoardTheme.SurfaceColorFor(BoardTheme.IsLight));
        float alpha = style.ColorCleared ? 0f : (float)Math.Clamp(style.ColorOpacity, 0, 1);
        // 由系统 DesktopAcrylicController 提供桌面模糊，_backgroundVisual 自身仅覆盖局部色调；
        // 这里保持低 tint/luminosity 让背景模糊自然透出。
        ConfigureBackdrop(
            true,
            DesktopCornerMode.None,
            tint,
            Math.Min(alpha, 0.25f),
            Math.Min(alpha * 0.5f, 0.35f));
    }
}
/// <summary>
/// 固定在桌面工作区右下角的控制窗。查看模式只有编辑和返回；
/// 编辑模式与软件控制窗一样，把富文本、操作和缩放分成同一排上的多段窗口。
/// 窗口尺寸跟随控制窗的大小设置，而不只放大里面的图标。
/// </summary>
internal sealed class DesktopCommandWindow
{
    private static readonly double[] ZoomSteps = [0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2];

    private readonly FluentIcon _editIcon;
    private readonly FluentIcon _addIcon;
    private readonly FluentIcon _arrangeIcon;
    private readonly FluentIcon _backIcon;
    private readonly FluentIcon _zoomOutIcon;
    private readonly FluentIcon _zoomInIcon;
    private readonly Button _editButton;
    private readonly Button _addButton;
    private readonly Button _backButton;
    private readonly ToggleButton _arrangeButton;
    private readonly List<ButtonBase> _buttons = [];
    private readonly StackPanel _commandStack;
    private readonly StackPanel _zoomStack;
    private readonly ScrollViewer _formatScroll;
    private readonly Segment _format;
    private readonly Segment _commands;
    private readonly Segment _zoom;
    private readonly List<Segment> _segments;
    private readonly List<PlacedSegment> _placed = [];
    private readonly TextBlock _zoomText;
    private readonly Slider _zoomSlider;
    private FrameworkElement? _formatting;
    private bool _editing;
    private bool _writingZoom;
    private bool _writingArrange;
    private bool _chromeApplied;
    private bool _layingOut;
    private bool _clickThrough;
    private int _offsetX;
    private int _offsetY;
    private double _opacity = 1;
    private double _button = 40;
    private double _padding = 7;
    private double _spacing = 4;
    private double _segmentGap = 8;
    private double _border;
    private double _icon = 16;
    private double _cornerRadius = 8;
    private double _pixelScale = 1;

    public DesktopCommandWindow()
    {
        _editIcon = new FluentIcon { Symbol = "Edit", FontSize = 16 };
        _addIcon = new FluentIcon { Symbol = "Add", FontSize = 16 };
        _arrangeIcon = new FluentIcon { Symbol = "Layout", FontSize = 16 };
        _backIcon = new FluentIcon { Symbol = "Back", FontSize = 16 };
        _zoomOutIcon = new FluentIcon { Symbol = "ZoomOut", FontSize = 16 };
        _zoomInIcon = new FluentIcon { Symbol = "ZoomIn", FontSize = 16 };
        _editButton = IconButton(_editIcon, "编辑", () => EditInvoked?.Invoke());
        _addButton = IconButton(_addIcon, "添加科目", () => AddInvoked?.Invoke());
        _arrangeButton = new ToggleButton
        {
            Width = 40, Height = 40, Padding = new Thickness(0),
            Background = HitBrush(),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = _arrangeIcon,
            IsChecked = true
        };
        _arrangeButton.Checked += (_, _) => { if (!_writingArrange) AutoArrangeInvoked?.Invoke(true); };
        _arrangeButton.Unchecked += (_, _) => { if (!_writingArrange) AutoArrangeInvoked?.Invoke(false); };
        ToolTipService.SetToolTip(_arrangeButton, "关闭自动排列");
        _buttons.Add(_arrangeButton);
        _backButton = IconButton(_backIcon, "返回作业板", () => ReturnInvoked?.Invoke());
        _commandStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _editButton, _addButton, _arrangeButton, _backButton }
        };
        _zoomText = new TextBlock
        {
            Text = "100%", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center
        };
        _zoomSlider = new Slider
        {
            Minimum = 50, Maximum = 200, Value = 100, Width = 100, MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        _zoomSlider.ValueChanged += (_, _) =>
        {
            if (_writingZoom) return;
            ZoomChanged?.Invoke(_zoomSlider.Value / 100);
        };
        Button resetZoom = new()
        {
            Content = _zoomText, Background = HitBrush(), BorderThickness = new Thickness(0), Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        resetZoom.Click += (_, _) => ZoomChanged?.Invoke(1);
        ToolTipService.SetToolTip(resetZoom, "恢复 100%");
        _buttons.Add(resetZoom);
        _zoomStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                IconButton(_zoomOutIcon, "缩小", () => StepZoom(-1)),
                resetZoom,
                _zoomSlider,
                IconButton(_zoomInIcon, "放大", () => StepZoom(1))
            }
        };
        _formatScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        _format = new Segment(_formatScroll);
        _commands = new Segment(_commandStack);
        _zoom = new Segment(_zoomStack);
        _segments = [_format, _commands, _zoom];
        foreach (Segment segment in _segments)
            segment.ThemeChanged += Paint;
        Paint();
        Layout(double.PositiveInfinity, notify: false);
    }

    public double DesiredWidthDip { get; private set; }
    public double DesiredHeightDip { get; private set; }
    public RectInt32 HomeBounds { get; private set; }
    public nint Handle => _commands.Handle;
    public IReadOnlyList<nint> Handles => _placed.Select(item => item.Segment.Handle).Where(handle => handle != 0).ToList();
    public (int X, int Y, double Opacity) Presentation => (_offsetX, _offsetY, _opacity);

    public bool AboveTiles
    {
        set
        {
            foreach (Segment segment in _segments) segment.AboveTiles = value;
        }
    }

    public event Action? EditInvoked;
    public event Action? AddInvoked;
    public event Action<bool>? AutoArrangeInvoked;
    public event Action? ReturnInvoked;
    public event Action<double>? ZoomChanged;
    public event Action? ChromeChanged;

    public void ApplyTheme(ElementTheme theme)
    {
        foreach (Segment segment in _segments) segment.ApplyTheme(theme);
    }

    public void CloseSurface()
    {
        foreach (Segment segment in _segments) segment.CloseSurface();
    }

    /// <summary>把当前作业的字体和富文本工具放到控制窗左侧的一段。传 null 表示收起。</summary>
    public void ShowFormatting(FrameworkElement? toolbar)
    {
        if (toolbar is null)
        {
            if (_formatting is null) return;
            WatchFormatting(null);
        }
        else if (!ReferenceEquals(_formatting, toolbar))
        {
            WatchFormatting(toolbar);
        }
        Layout(double.PositiveInfinity, notify: true);
    }

    public void HideFormatting(FrameworkElement toolbar)
    {
        if (!ReferenceEquals(_formatting, toolbar)) return;
        ShowFormatting(null);
    }

    public void SetEditing(bool editing)
    {
        _editing = editing;
        _editIcon.Symbol = editing ? "Checkmark" : "Edit";
        ToolTipService.SetToolTip(_editButton, editing ? "完成编辑" : "编辑");
        foreach (Segment segment in _segments)
            segment.SetInteractive(editing);
        Layout(double.PositiveInfinity, notify: true);
    }

    public void SetAutoArrange(bool enabled)
    {
        _writingArrange = true;
        _arrangeButton.IsChecked = enabled;
        _arrangeIcon.Opacity = 1;
        ToolTipService.SetToolTip(_arrangeButton, enabled ? "关闭自动排列" : "自动排列");
        _writingArrange = false;
    }

    /// <summary>外观与主界面控制窗同一套。按钮变大时，每一段窗口的外框一起变大。</summary>
    public void ApplyToolbarChrome(
        Brush background,
        Brush borderBrush,
        double borderThickness,
        double radius,
        double buttonRadius,
        double buttonSize,
        double iconSize,
        bool glass = false,
        string colorHex = "",
        double opacity = 0.8,
        bool colorCleared = false,
        double blur = 20)
    {
        _chromeApplied = true;
        _button = Math.Max(1, buttonSize);
        _padding = _button * 7 / 40;
        _spacing = _button * 4 / 40;
        _segmentGap = _button * 8 / 40;
        _border = Math.Max(0, borderThickness);
        _icon = iconSize;
        // 桌面毛玻璃画在圆角背景上，不用整窗系统亚克力。系统层是矩形（或固定的系统圆角），
        // 会比色块大一圈，模糊滑块和透明度也作用不到它。
        Windows.UI.Color tint = GridAppearance.ParseColor(colorHex, BoardTheme.SurfaceColorFor(BoardTheme.IsLight));
        double alpha = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 0.8;
        tint.A = colorCleared ? (byte)0 : (byte)Math.Round(tint.A * alpha);
        _cornerRadius = Math.Max(0, radius);
        foreach (Segment segment in _segments)
        {
            segment.Frame.CornerRadius = new CornerRadius(radius);
            segment.SetClipRadius(radius);
            if (glass)
            {
                if (segment.Frame.Background is DesktopGlassBrush existing) existing.Update(blur, tint);
                else segment.Frame.Background = new DesktopGlassBrush(blur, tint) { FallbackColor = tint };
            }
            else segment.Frame.Background = background;
            segment.Frame.BorderBrush = borderBrush;
            // 系统亚克力只提供桌面模糊，颜色和模糊程度画在圆角背景上。窗口区域再裁成同一个圆角。
            if (glass)
                segment.ConfigureBackdrop(true, DesktopCornerMode.None, Microsoft.UI.Colors.Transparent, 0f, 0.35f);
            else
                segment.ConfigureBackdrop(false, DesktopCornerMode.None, Microsoft.UI.Colors.Transparent, 0f, 0f);
        }
        foreach (ButtonBase button in _buttons)
        {
            button.Width = button.Height = _button;
            button.Padding = new Thickness(0);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.CornerRadius = new CornerRadius(buttonRadius);
            if (button.Content is FluentIcon icon) icon.FontSize = iconSize;
        }
        Layout(double.PositiveInfinity, notify: true);
    }

    public void SetZoom(double zoom)
    {
        _writingZoom = true;
        _zoomText.Text = $"{Math.Round(zoom * 100)}%";
        _zoomSlider.Value = Math.Clamp(zoom * 100, _zoomSlider.Minimum, _zoomSlider.Maximum);
        _writingZoom = false;
    }

    /// <summary>把自然宽度收到工作区里。不发出尺寸变化，调用方紧接着就会摆放。</summary>
    public void FitWidth(double maxDip) =>
        Layout(double.IsFinite(maxDip) && maxDip > 0 ? maxDip : double.PositiveInfinity, notify: false);

    /// <summary>右缘和底缘对齐工作区。从右往左排：缩放、操作、富文本。</summary>
    public void PlaceGroup(int right, int bottom, double pixelScale)
    {
        if (!double.IsFinite(pixelScale) || pixelScale <= 0) pixelScale = 1;
        _pixelScale = pixelScale;
        _placed.Clear();
        int gap = Math.Max(0, (int)Math.Round(_segmentGap * pixelScale));
        int cursor = right;
        List<Segment> visible = [];
        if (_format.WidthDip > 0.5) visible.Add(_format);
        if (_commands.WidthDip > 0.5) visible.Add(_commands);
        if (_zoom.WidthDip > 0.5) visible.Add(_zoom);
        visible.Reverse();
        bool first = true;
        foreach (Segment segment in visible)
        {
            int width = Math.Max(1, (int)Math.Round(segment.WidthDip * pixelScale));
            int height = Math.Max(1, (int)Math.Round(segment.HeightDip * pixelScale));
            if (!first) cursor -= gap;
            first = false;
            cursor -= width;
            _placed.Add(new PlacedSegment(segment, cursor, bottom - height, width, height));
        }
        HomeBounds = Union(_placed);
        foreach (Segment segment in _segments)
        {
            if (_placed.All(item => item.Segment != segment))
                segment.HideSurface();
        }
        ApplyVisual();
    }

    public void SetPresentation(int offsetX, int offsetY, double opacity)
    {
        _offsetX = offsetX;
        _offsetY = offsetY;
        _opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
        ApplyVisual();
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        foreach (Segment segment in _segments)
            segment.SetClickThrough(enabled);
    }

    private void WatchFormatting(FrameworkElement? toolbar)
    {
        if (_formatting is not null)
            _formatting.SizeChanged -= OnFormattingSizeChanged;
        _formatting = toolbar;
        _formatScroll.Content = toolbar;
        if (_formatting is not null)
            _formatting.SizeChanged += OnFormattingSizeChanged;
    }

    private void OnFormattingSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layingOut || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5) return;
        Layout(double.PositiveInfinity, notify: true);
    }

    private void StepZoom(int direction)
    {
        double current = _zoomSlider.Value / 100;
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(step => step > current + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(step => step < current - 0.001, ZoomSteps[0]);
        ZoomChanged?.Invoke(next);
    }

    private void Layout(double maxDip, bool notify)
    {
        if (_layingOut) return;
        _layingOut = true;
        bool notifyLater = false;
        try
        {
            double button = Math.Max(1, _button);
            double spacing = _spacing;
            int commands = _editing ? 4 : 2;
            double commandOuter = Outer(commands * button + Math.Max(0, commands - 1) * spacing, _padding, _border);
            double height = Outer(button, _padding, _border);
            double slider = Math.Max(80, button * 2.5);
            double minSlider = Math.Max(48, button * 1.2);
            double zoomOuter = _editing ? Outer(button * 3 + slider + spacing * 3, _padding, _border) : 0;
            double formatInner = 0;
            bool formatClipped = false;
            if (_editing && _formatting is not null)
            {
                _formatting.Measure(new Size(double.PositiveInfinity, button));
                double natural = _formatting.DesiredSize.Width;
                if (natural < 1 || !double.IsFinite(natural)) natural = button * 8;
                double cap = button * 12;
                // 放得下时多留两个像素，否则滚动条会在内容已经完整可见时冒出来。
                formatClipped = natural > cap + 0.5;
                formatInner = formatClipped ? cap : Math.Min(cap, natural + IslandScroll.FitSlack);
            }
            double formatOuter = formatInner > 0 ? Outer(formatInner, _padding, _border) : 0;
            double minFormat = Outer(button * 3, _padding, _border);
            double Total(double format, double zoom)
            {
                double total = commandOuter;
                if (format > 0) total += _segmentGap + format;
                if (zoom > 0) total += _segmentGap + zoom;
                return total;
            }
            while (zoomOuter > 0 && Total(formatOuter, zoomOuter) > maxDip + 0.5 && slider > minSlider + 0.5)
            {
                slider = Math.Max(minSlider, slider - button * 0.5);
                zoomOuter = Outer(button * 3 + slider + spacing * 3, _padding, _border);
            }
            while (formatOuter > 0 && Total(formatOuter, zoomOuter) > maxDip + 0.5 && formatOuter > minFormat + 0.5)
                formatOuter = Math.Max(minFormat, formatOuter - button);

            _zoomSlider.Width = slider;
            _zoomSlider.Height = Math.Min(button, 32 * button / 40);
            _zoomText.FontSize = Math.Max(10, button * 12 / 40);
            _commandStack.Spacing = spacing;
            _zoomStack.Spacing = spacing;
            _addButton.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
            _arrangeButton.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
            ApplyFrame(_commands, commandOuter, height);
            ApplyFrame(_zoom, zoomOuter, height);
            ApplyFrame(_format, formatOuter, height);
            if (formatOuter > 0)
            {
                _formatScroll.Width = Math.Max(0, formatOuter - _padding * 2 - _border * 2);
                _formatScroll.Height = Math.Max(0, height - _padding * 2 - _border * 2);
                _formatScroll.HorizontalScrollBarVisibility = formatClipped ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
                _formatScroll.HorizontalScrollMode = formatClipped ? ScrollMode.Enabled : ScrollMode.Disabled;
                _formatScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                _formatScroll.VerticalScrollMode = ScrollMode.Disabled;
            }
            _commands.WidthDip = commandOuter;
            _commands.HeightDip = height;
            _zoom.WidthDip = zoomOuter;
            _zoom.HeightDip = height;
            _format.WidthDip = formatOuter;
            _format.HeightDip = height;
            double width = Total(formatOuter, zoomOuter);
            notifyLater = notify && (Math.Abs(DesiredWidthDip - width) > 0.5 || Math.Abs(DesiredHeightDip - height) > 0.5);
            DesiredWidthDip = width;
            DesiredHeightDip = height;
        }
        finally
        {
            _layingOut = false;
        }
        if (notifyLater) ChromeChanged?.Invoke();
    }

    private void ApplyFrame(Segment segment, double width, double height)
    {
        segment.Frame.Width = Math.Max(0, width);
        segment.Frame.Height = Math.Max(0, height);
        segment.Frame.Padding = new Thickness(_padding);
        segment.Frame.BorderThickness = new Thickness(_border);
    }

    private static double Outer(double content, double padding, double border) => content + padding * 2 + border * 2;

    private void ApplyVisual()
    {
        foreach (PlacedSegment item in _placed)
        {
            item.Segment.ShowSurface();
            item.Segment.Place(item.X + _offsetX, item.Y + _offsetY, item.W, item.H);
            item.Segment.SetRegionRadius((int)Math.Round(_cornerRadius * _pixelScale));
            item.Segment.SetContentOpacity(_opacity);
            item.Segment.SetClickThrough(_clickThrough);
        }
    }

    private static RectInt32 Union(List<PlacedSegment> placed)
    {
        if (placed.Count == 0) return default;
        int left = placed.Min(item => item.X);
        int top = placed.Min(item => item.Y);
        int right = placed.Max(item => item.X + item.W);
        int bottom = placed.Max(item => item.Y + item.H);
        return new RectInt32(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void Paint()
    {
        SolidColorBrush foreground = new(BoardTheme.TextColor);
        if (!_chromeApplied)
        {
            foreach (Segment segment in _segments)
            {
                segment.Frame.Background = BoardTheme.SurfaceBrush;
                segment.Frame.BorderThickness = new Thickness(0);
            }
        }
        _editIcon.Foreground = foreground;
        _addIcon.Foreground = foreground;
        _arrangeIcon.Foreground = foreground;
        _backIcon.Foreground = foreground;
        _zoomOutIcon.Foreground = foreground;
        _zoomInIcon.Foreground = foreground;
        _zoomText.Foreground = foreground;
    }

    private Button IconButton(FluentIcon icon, string tooltip, Action click)
    {
        Button button = new()
        {
            Width = 40, Height = 40, Padding = new Thickness(0),
            Background = HitBrush(),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = icon
        };
        button.Click += (_, _) => click();
        ToolTipService.SetToolTip(button, tooltip);
        _buttons.Add(button);
        return button;
    }

    private static Brush HitBrush() => new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0));

    private readonly record struct PlacedSegment(Segment Segment, int X, int Y, int W, int H);

    private sealed class Segment : DesktopSurfaceWindow
    {
        public Segment(UIElement content)
        {
            Frame = new Border
            {
                Child = content,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(8)
            };
            Root.Children.Add(Frame);
            TitleText = "桌面磁贴";
            AboveTiles = true;
            Frame.SizeChanged += (_, _) => UpdateClipSize();
            Root.ActualThemeChanged += (_, _) => ThemeChanged?.Invoke();
        }

        public Border Frame { get; }
        private Microsoft.UI.Composition.CompositionRoundedRectangleGeometry? _clipGeometry;

        /// <summary>把毛玻璃和按钮裁进设定的圆角。系统窗口圆角只有固定几档，跟不上这个半径。</summary>
        public void SetClipRadius(double radius)
        {
            Microsoft.UI.Composition.Visual visual = ElementCompositionPreview.GetElementVisual(Frame);
            if (radius <= 0.5)
            {
                visual.Clip = null;
                _clipGeometry = null;
                return;
            }
            Microsoft.UI.Composition.Compositor compositor = visual.Compositor;
            _clipGeometry ??= compositor.CreateRoundedRectangleGeometry();
            _clipGeometry.CornerRadius = new Vector2((float)radius, (float)radius);
            UpdateClipSize();
            visual.Clip ??= compositor.CreateGeometricClip(_clipGeometry);
        }

        private void UpdateClipSize()
        {
            if (_clipGeometry is null) return;
            _clipGeometry.Size = new Vector2((float)Math.Max(0, Frame.ActualWidth), (float)Math.Max(0, Frame.ActualHeight));
        }
        public double WidthDip { get; set; }
        public double HeightDip { get; set; }
        public event Action? ThemeChanged;
    }
}

internal static class DesktopWindowNative
{
    public static bool ScreenOrigin(nint hwnd, out int x, out int y)
    {
        if (!GetWindowRect(hwnd, out ScreenRect rect)) { x = y = 0; return false; }
        x = rect.Left;
        y = rect.Top;
        return true;
    }

    public static void StackCommandAboveTiles(IEnumerable<nint> commands, IEnumerable<nint> tiles, bool editing)
    {
        DesktopWidgetHost.RestackDesktopGroup(commands.ToList(), tiles.ToList(), editing);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public static bool IsMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x02) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x04) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out ScreenRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out DesktopPoint point);

    [StructLayout(LayoutKind.Sequential)]
    public struct DesktopPoint
    {
        public int X;
        public int Y;
    }
}
