using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace WorkTiles.Controls;

/// <summary>
/// 纯透明系统背景：为 DesktopWindowXamlSource 提供 alpha=0 的 OS Composition 画刷，
/// 配合 WS_EX_NOREDIRECTIONBITMAP 与 DwmExtendFrameIntoClientArea(-1)，让圆角外及半透明区域直接透出桌面。
/// </summary>
internal sealed class TransparentDesktopBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? _compositor;
    private static nint _dispatcherQueueController;
    private Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        DesktopWidgetHost.Trace("TransparentDesktopBackdrop.OnTargetConnected start");
        base.OnTargetConnected(connectedTarget, xamlRoot);
        Windows.UI.Composition.Compositor compositor = EnsureCompositor();
        DesktopWidgetHost.Trace("EnsureCompositor done");
        _brush = compositor.CreateColorBrush(Colors.Transparent);
        connectedTarget.SystemBackdrop = _brush;
        DesktopWidgetHost.Trace("TransparentDesktopBackdrop.OnTargetConnected end");
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
    }

    private static Windows.UI.Composition.Compositor EnsureCompositor()
    {
        if (_compositor is not null) return _compositor;
        DesktopWidgetHost.Trace($"GetForCurrentThread={Windows.System.DispatcherQueue.GetForCurrentThread() is not null}");
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null && _dispatcherQueueController == 0)
        {
            DispatcherQueueOptions options = new()
            {
                dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                threadType = 2, // DQTYPE_THREAD_CURRENT
                apartmentType = 2 // DQTAT_COM_STA
            };
            int hr = CreateDispatcherQueueController(options, ref _dispatcherQueueController);
            DesktopWidgetHost.Trace($"CreateDispatcherQueueController hr=0x{hr:X8} ctrl={_dispatcherQueueController}");
        }
        _compositor = new Windows.UI.Composition.Compositor();
        DesktopWidgetHost.Trace("new Windows.UI.Composition.Compositor() done");
        return _compositor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, ref nint dispatcherQueueController);
}

/// <summary>
/// 桌面实时毛玻璃背景：通过 DesktopAcrylicController 对窗口后方的真实桌面壁纸/内容做亚克力模糊，
/// 并在失焦时保持 IsInputActive=true，避免桌面组件失去焦点后退化成不透明灰块。
/// </summary>
internal sealed class AcrylicDesktopBackdrop : SystemBackdrop
{
    private readonly Color _tintColor;
    private readonly float _tintOpacity;
    private readonly float _luminosityOpacity;
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _defaultConfiguration;

    public AcrylicDesktopBackdrop(Color tintColor, float tintOpacity, float luminosityOpacity)
    {
        _tintColor = tintColor;
        _tintOpacity = Math.Clamp(tintOpacity, 0f, 1f);
        _luminosityOpacity = Math.Clamp(luminosityOpacity, 0f, 1f);
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported())
        {
            return;
        }

        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            TintColor = _tintColor,
            TintOpacity = _tintOpacity,
            LuminosityOpacity = _luminosityOpacity,
            FallbackColor = _tintColor
        };
        _defaultConfiguration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _controller.AddSystemBackdropTarget(connectedTarget);
        ApplyConfiguration();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        ApplyConfiguration();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        _defaultConfiguration = null;
    }

    private void ApplyConfiguration()
    {
        if (_controller is null || _defaultConfiguration is null) return;
        _controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
        {
            Theme = _defaultConfiguration.Theme,
            IsHighContrast = _defaultConfiguration.IsHighContrast,
            IsInputActive = true
        });
    }
}

internal enum DesktopCornerMode
{
    None = 1,       // DWMWCP_DONOTROUND
    Round = 2,      // DWMWCP_ROUND
    RoundSmall = 3  // DWMWCP_ROUNDSMALL
}

/// <summary>
/// 标准 Win32 + DesktopWindowXamlSource 桌面组件宿主：
/// 1. 创建纯 WS_POPUP + WS_EX_NOREDIRECTIONBITMAP + WS_EX_TOOLWINDOW 窗口（无边框、无标题栏、无白边）；
/// 2. 通过 DwmExtendFrameIntoClientArea(-1)、DWMWA_BORDER_COLOR=NONE 与透明/亚克力 SystemBackdrop 实现逐像素透明与圆角透底；
///    显示、改尺寸和 SetWindowRgn 之后要重新写这些属性，否则系统会沿窗口补回一圈白边；
/// 3. 非编辑态以 Progman 为 Owner 并保持 WS_EX_NOACTIVATE，普通窗口仍可盖在上面。
///    编辑态允许激活；主窗口还看得见时重排会暂时挂回 Progman，点到磁贴才卸 owner，避免整组被抬出桌面。
/// </summary>
internal sealed class DesktopWidgetHost : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsClipChildren = 0x02000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoRedirectionBitmap = 0x00200000;

    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int SwShowNoActivate = 4;
    private const int SwHide = 0;

    private const uint WmDestroy = 0x0002;
    private const uint WmSize = 0x0005;
    private const uint WmSetFocus = 0x0007;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmShowWindow = 0x0018;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmWindowPosChanging = 0x0046;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcPaint = 0x0085;
    private const uint WmNcActivate = 0x0086;
    private const uint WmSysCommand = 0x0112;
    private const uint WmDwmCompositionChanged = 0x031E;

    private const int MaActivate = 1;
    private const int MaNoActivate = 3;
    private const int ScMinimize = 0xF020;
    private const int HollowBrush = 5;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;

    private static readonly nint HwndBottom = 1;
    private static readonly nint HwndTop = 0;

    private static ushort _classAtom;
    private static WndProc? _wndProc;
    private static uint _taskbarCreatedMsg;
    private static readonly Dictionary<nint, DesktopWidgetHost> _instances = new();
    private static readonly List<DesktopWidgetHost> _activeHosts = [];
    private static nint _winEventHook;
    private static WinEventProc? _winEventProc;

    /// <summary>桌面成为前台、磁贴抬到普通窗口之上以前。全屏主窗口在这里先收起。</summary>
    internal static Action? BeforeDesktopForeground;

    /// <summary>主窗口仍可见地盖住桌面时为 true，这次不要把磁贴抬到它上面。</summary>
    internal static Func<bool>? AppCoversDesktop;
    private static readonly EnumChildProc StripChildBorderDelegate = StripChildBorder;

    private nint _hwnd;
    private DesktopWindowXamlSource? _xamlSource;
    private int _x;
    private int _y;
    private int _width = 1;
    private int _height = 1;
    private int _bridgeWidth = -1;
    private int _bridgeHeight = -1;
    private bool _visible;
    private bool _interactive;
    private bool _clickThrough;
    private bool _disposed;
    private bool _applyingFrame;
    private DesktopCornerMode _cornerMode = DesktopCornerMode.None;
    private bool _acrylic;
    private bool _backdropSuppressed;
    private int _regionRadius;
    private Color _acrylicTint;
    private float _acrylicTintOpacity;
    private float _acrylicLuminosityOpacity;

    public bool IsAlwaysAboveTiles { get; set; }

    /// <summary>
    /// 为 false 时不挂到桌面壳上。编辑中弹出的插入线若挂到 Progman，桌面会被抬到所有磁贴前面。
    /// </summary>
    public bool AnchorToDesktop { get; set; } = true;

    public nint Handle => _hwnd;
    public int X => _x;
    public int Y => _y;
    public int Width => _width;
    public int Height => _height;
    public bool IsInteractive => _interactive;

    public void Create(UIElement content, string title = "WorkTilesDesktopWidget")
    {
        if (_disposed || _hwnd != 0) return;
        Trace($"Create start: {title}");
        EnsureClass();

        _hwnd = CreateWindowEx(
            WsExNoRedirectionBitmap | WsExToolWindow | WsExNoActivate,
            (nint)_classAtom,
            title,
            unchecked((int)(WsPopup | WsClipSiblings | WsClipChildren)),
            0, 0, 1, 1,
            0, 0, GetModuleHandle(null), 0);

        Trace($"CreateWindowEx hwnd={_hwnd}");
        if (_hwnd == 0) return;
        _instances[_hwnd] = this;
        _activeHosts.Add(this);
        EnsureWinEventHook();

        ApplyDwmAttributes();
        Trace("ApplyDwmAttributes done");

        _xamlSource = new DesktopWindowXamlSource();
        try
        {
            Trace("new DesktopWindowXamlSource done");
            _xamlSource.Initialize(Win32Interop.GetWindowIdFromWindow(_hwnd));
            Trace("Initialize done");
            _xamlSource.ShouldConstrainPopupsToWorkArea = true;
            _xamlSource.Content = content;
            Trace("Content set");
        }
        catch
        {
            _instances.Remove(_hwnd);
            _activeHosts.Remove(this);
            _xamlSource?.Dispose();
            _xamlSource = null;
            DestroyWindow(_hwnd);
            _hwnd = 0;
            throw;
        }
        // 岛建好后再写一次。Initialize 会把显示前设过的无边框属性清掉，不透明白板也会露出来。
        ApplyBackdrop();
        ApplyDwmAttributes();

        UpdateDesktopAnchor();
        Trace("UpdateDesktopAnchor done");
    }

    internal static void Trace(string msg)
    {
        // 探针只在调试器下写文件；发布版不能每块磁贴都同步创建并追加 data/probe-trace.txt。
        if (!System.Diagnostics.Debugger.IsAttached) return;
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "probe-trace.txt"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public void Show()
    {
        if (_disposed || _hwnd == 0) return;
        _visible = true;
        ShowWindow(_hwnd, SwShowNoActivate);
        if (_xamlSource?.SiteBridge is { } bridge) { bridge.MoveAndResize(new RectInt32(0, 0, _width, _height)); _bridgeWidth = _width; _bridgeHeight = _height; }
        // 显示时系统会补上默认白边，当前这次调用可能仍早于 DWM 收尾，下一帧再写一次。
        ApplyDwmAttributes();
        if (_xamlSource?.Content is UIElement element)
            element.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed) ApplyDwmAttributes();
            });
    }

    public void Hide()
    {
        if (_disposed || _hwnd == 0) return;
        _visible = false;
        ShowWindow(_hwnd, SwHide);
    }

    public void SetTitle(string title)
    {
        if (_disposed || _hwnd == 0) return;
        SetWindowText(_hwnd, title ?? string.Empty);
    }

    public void MoveAndResize(int x, int y, int width, int height)
    {
        if (_disposed || _hwnd == 0) return;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        bool sizeChanged = _width != width || _height != height;
        bool posChanged = _x != x || _y != y;
        _x = x;
        _y = y;
        _width = width;
        _height = height;

        uint flags = SwpNoZOrder | SwpNoActivate;
        if (!posChanged) flags |= SwpNoMove;
        if (!sizeChanged) flags |= SwpNoSize;
        if (_visible) flags |= SwpShowWindow;

        if (posChanged || sizeChanged)
            SetWindowPos(_hwnd, 0, x, y, width, height, flags);

        if (sizeChanged)
        {
            if (_xamlSource?.SiteBridge is { } bridge)
            {
                bridge.MoveAndResize(new RectInt32(0, 0, width, height));
                _bridgeWidth = width;
                _bridgeHeight = height;
            }
            ApplyRoundedRegion();
        }
        else if (posChanged)
            ApplyDwmAttributes();
    }

    /// <summary>把窗口裁成设定的圆角。系统亚克力跟着这个区域走，不会比按钮色块大出一圈。</summary>
    public void SetRoundedRegion(int radiusPx)
    {
        _regionRadius = Math.Max(0, radiusPx);
        ApplyRoundedRegion();
    }

    /// <summary>自动隐藏时关掉系统毛玻璃。只改可见性，不丢掉原来的颜色设置。</summary>
    public void SetBackdropSuppressed(bool suppressed)
    {
        if (_disposed || _backdropSuppressed == suppressed) return;
        _backdropSuppressed = suppressed;
        if (_hwnd != 0) ApplyBackdrop();
    }

    public void Move(int x, int y)
    {
        if (_disposed || _hwnd == 0) return;
        if (_x == x && _y == y) return;
        _x = x;
        _y = y;
        SetWindowPos(_hwnd, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>
    /// 切换交互模式：
    /// 非编辑态保留 WS_EX_NOACTIVATE 且以 Progman 为 Owner，避免点击抬起桌面盖住其他窗口并天然抵抗 Win+D；
    /// 编辑态允许激活与输入法焦点，同时解除 Owner 防止点击磁贴时将桌面抬起遮挡参照窗口。
    /// </summary>
    public void SetInteractive(bool interactive)
    {
        if (_disposed || _hwnd == 0 || _interactive == interactive) return;
        _interactive = interactive;
        nint ex = GetWindowLongPtr(_hwnd, GwlExStyle);
        if (interactive) ex &= ~WsExNoActivate;
        else ex |= WsExNoActivate;
        SetWindowLongPtr(_hwnd, GwlExStyle, ex);
        SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        ApplyDwmAttributes();
        UpdateDesktopAnchor();
    }

    /// <summary>隐藏后让点击穿过窗口，避免看不见的控制窗挡住桌面。</summary>
    public void SetClickThrough(bool enabled)
    {
        if (_disposed || _hwnd == 0 || _clickThrough == enabled) return;
        _clickThrough = enabled;
        nint ex = GetWindowLongPtr(_hwnd, GwlExStyle);
        if (enabled) ex |= WsExTransparent;
        else ex &= ~WsExTransparent;
        SetWindowLongPtr(_hwnd, GwlExStyle, ex);
        SetWindowPos(_hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        ApplyDwmAttributes();
    }

    public void ConfigureBackdrop(bool useDesktopAcrylic, DesktopCornerMode cornerMode, Color tintColor, float tintOpacity, float luminosityOpacity)
    {
        if (_disposed || _hwnd == 0) return;
        bool backdropChanged = _acrylic != useDesktopAcrylic
            || (useDesktopAcrylic && (_acrylicTint != tintColor
                || Math.Abs(_acrylicTintOpacity - tintOpacity) > 0.005f
                || Math.Abs(_acrylicLuminosityOpacity - luminosityOpacity) > 0.005f));
        bool cornerChanged = _cornerMode != cornerMode;

        _acrylic = useDesktopAcrylic;
        _cornerMode = cornerMode;
        _acrylicTint = tintColor;
        _acrylicTintOpacity = tintOpacity;
        _acrylicLuminosityOpacity = luminosityOpacity;

        if (cornerChanged) ApplyDwmAttributes();
        if (backdropChanged) ApplyBackdrop();
    }

    public bool TryGetOrigin(out int x, out int y)
    {
        if (_hwnd != 0 && GetWindowRect(_hwnd, out ScreenRect rect))
        {
            x = rect.Left;
            y = rect.Top;
            return true;
        }
        x = _x;
        y = _y;
        return false;
    }

    /// <summary>
    /// 主窗口还看得见时，重排前先挂回桌面。编辑态平时没有 owner，直接 HWND_BOTTOM 会掉到壁纸后面。
    /// 插入线不挂桌面，避免把桌面抬到磁贴前面。
    /// </summary>
    internal static void HoldAnchoredOwners()
    {
        foreach (DesktopWidgetHost host in _activeHosts)
            host.HoldDesktopOwner();
    }

    /// <summary>挂回 Progman，不改能否点击。点到磁贴时再卸 owner。</summary>
    internal void HoldDesktopOwner()
    {
        if (_disposed || _hwnd == 0 || !AnchorToDesktop) return;
        nint owner = GetDesktopOwnerHandle();
        if (owner == 0) return;
        if (GetWindowLongPtr(_hwnd, GwlpHwndParent) != owner)
            SetWindowLongPtr(_hwnd, GwlpHwndParent, owner);
    }

    /// <summary>真正点到编辑中的磁贴时卸掉桌面归属，避免这次点击把整个桌面抬起来。</summary>
    internal void ReleaseOwnerForInput()
    {
        if (_disposed || _hwnd == 0 || !_interactive || !AnchorToDesktop) return;
        if (GetWindowLongPtr(_hwnd, GwlpHwndParent) != 0)
            SetWindowLongPtr(_hwnd, GwlpHwndParent, 0);
    }

    /// <summary>将命令栏保持在所有桌面磁贴之上，同时整组停留在桌面之上、普通窗口之下。编辑不改变这层 Z 序。</summary>
    public static void RestackDesktopGroup(IReadOnlyList<nint> commandHwnds, IReadOnlyList<nint> tileHwnds, bool editing)
    {
        _ = editing;
        List<nint> commands = [];
        foreach (nint hwnd in commandHwnds)
            if (hwnd != 0) commands.Add(hwnd);

        if (commands.Count > 0)
        {
            SetWindowPos(commands[0], HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            for (int i = 1; i < commands.Count; i++)
                SetWindowPos(commands[i], commands[i - 1], 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            nint anchor = commands[^1];
            foreach (nint tile in tileHwnds)
            {
                if (tile != 0)
                    SetWindowPos(tile, anchor, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
            return;
        }

        foreach (nint tile in tileHwnds)
        {
            if (tile != 0)
                SetWindowPos(tile, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    private void UpdateDesktopAnchor()
    {
        if (_hwnd == 0) return;
        nint owner = _interactive || !AnchorToDesktop ? 0 : GetDesktopOwnerHandle();
        if (GetWindowLongPtr(_hwnd, GwlpHwndParent) != owner)
            SetWindowLongPtr(_hwnd, GwlpHwndParent, owner);
    }

    private int _appliedRegionRadius = int.MinValue;
    private int _appliedRegionWidth = int.MinValue;
    private int _appliedRegionHeight = int.MinValue;

    private void ApplyRoundedRegion()
    {
        if (_hwnd == 0) return;
        // 缩放/动画每拍都会请求同一个半径；区域和尺寸没变时不要再强制重绘并重写 DWM 属性。
        if (_regionRadius == _appliedRegionRadius && _width == _appliedRegionWidth && _height == _appliedRegionHeight) return;
        _appliedRegionRadius = _regionRadius;
        _appliedRegionWidth = _width;
        _appliedRegionHeight = _height;
        if (_regionRadius <= 0 || _width < 2 || _height < 2)
        {
            SetWindowRgn(_hwnd, 0, true);
            ApplyDwmAttributes();
            return;
        }
        int diameter = Math.Min(_regionRadius * 2, Math.Min(_width, _height));
        nint region = CreateRoundRectRgn(0, 0, _width + 1, _height + 1, diameter, diameter);
        if (SetWindowRgn(_hwnd, region, true) == 0)
            DeleteObject(region);
        ApplyDwmAttributes();
    }

    private static nint GetDesktopOwnerHandle()
    {
        nint progman = GetShellWindow();
        if (progman != 0 && IsWindow(progman)) return progman;
        return FindWindow("Progman", null);
    }

    private void ApplyDwmAttributes()
    {
        if (_hwnd == 0 || _applyingFrame) return;
        _applyingFrame = true;
        try
        {
            Margins margins = new() { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            StripBorder(_hwnd);
            int corner = (int)_cornerMode;
            DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref corner, 4);
            // 扩展进客户区的系统底板默认是白色。亚克力控制器自己管理底板，这里再关掉会把毛玻璃一起关了。
            if (!_acrylic || _backdropSuppressed)
            {
                int backdrop = DwmsbtNone;
                DwmSetWindowAttribute(_hwnd, DwmwaSystemBackdropType, ref backdrop, 4);
            }
            EnumChildWindows(_hwnd, StripChildBorderDelegate, _hwnd);
        }
        finally
        {
            _applyingFrame = false;
        }
    }

    private static void StripBorder(nint hwnd)
    {
        int borderColor = DwmwaColorNone;
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, 4);
    }

    private static bool StripChildBorder(nint child, nint _)
    {
        // 岛的子窗口自己还有一条边。底板类型只改顶层，避免把岛里的内容一起关掉。
        StripBorder(child);
        return true;
    }

    private void ApplyBackdrop()
    {
        Trace($"ApplyBackdrop _xamlSource={_xamlSource is not null} acrylic={_acrylic}");
        if (_xamlSource is null) return;
        try
        {
            SystemBackdrop backdrop = _acrylic && !_backdropSuppressed
                ? new AcrylicDesktopBackdrop(_acrylicTint, _acrylicTintOpacity, _acrylicLuminosityOpacity)
                : new TransparentDesktopBackdrop();
            Trace("backdrop instantiated");
            _xamlSource.SystemBackdrop = backdrop;
            Trace("ApplyBackdrop assigned");
        }
        catch (Exception ex)
        {
            Trace($"ApplyBackdrop EX: {ex}");
        }
        ApplyDwmAttributes();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _visible = false;

        _activeHosts.Remove(this);
        if (_activeHosts.Count == 0 && _winEventHook != 0)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = 0;
        }

        if (_xamlSource is not null)
        {
            _xamlSource.Content = null;
            _xamlSource.Dispose();
            _xamlSource = null;
        }

        if (_hwnd != 0)
        {
            _instances.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    private static void EnsureClass()
    {
        if (_classAtom != 0) return;
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        _wndProc = WindowProc;
        WndClassEx wc = new()
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            style = 0x0008, // CS_DBLCLKS
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "WorkTilesDesktopHostClass",
            hCursor = LoadCursor(0, (nint)32512),
            // 类画刷留空时，系统会用 COLOR_WINDOW 把没盖住的边填白。
            hbrBackground = GetStockObject(HollowBrush)
        };
        _classAtom = RegisterClassEx(ref wc);
    }

    private static void EnsureWinEventHook()
    {
        if (_winEventHook != 0) return;
        _winEventProc = OnWinEvent;
        // 监听全局最小化与前台切换，确保 Win+D 触发后所有桌面组件立即保持可见且不被桌面压住。
        _winEventHook = SetWinEventHook(
            0x0003, // EVENT_SYSTEM_FOREGROUND
            0x0016, // EVENT_SYSTEM_MINIMIZESTART
            0,
            _winEventProc,
            0, 0,
            0x0000); // WINEVENT_OUTOFCONTEXT
    }

    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_activeHosts.Count == 0) return;
        if (eventType == 0x0016) // EVENT_SYSTEM_MINIMIZESTART
        {
            if (_instances.TryGetValue(hwnd, out DesktopWidgetHost? target) && target._visible)
            {
                ShowWindow(hwnd, SwShowNoActivate);
                // 全屏窗口还铺着时先别抬。显示桌面会先收起主窗口，再由前台分支把磁贴抬回桌面之上。
                if (AppCoversDesktop?.Invoke() != true)
                    target.EnsureVisibleAboveDesktop();
            }
            return;
        }

        if (eventType == 0x0003) // EVENT_SYSTEM_FOREGROUND
        {
            if (IsDesktopShellWindow(hwnd))
            {
                BeforeDesktopForeground?.Invoke();
                if (AppCoversDesktop?.Invoke() == true) return;
                RaiseVisibleHostsAboveDesktop();
            }
        }
    }

    internal static void RaiseVisibleHostsAboveDesktop()
    {
        foreach (DesktopWidgetHost item in _activeHosts)
        {
            if (!item._visible || item.IsAlwaysAboveTiles) continue;
            item.UpdateDesktopAnchor();
            item.EnsureVisibleAboveDesktop();
        }
        foreach (DesktopWidgetHost item in _activeHosts)
        {
            if (!item._visible || !item.IsAlwaysAboveTiles) continue;
            item.UpdateDesktopAnchor();
            item.EnsureVisibleAboveDesktop();
        }
    }

    private void EnsureVisibleAboveDesktop()
    {
        if (_disposed || !_visible || _hwnd == 0) return;
        if (IsIconic(_hwnd)) ShowWindow(_hwnd, SwShowNoActivate);
        // 交互态才抬到普通窗口之上；非交互态保持桌面语义，普通窗口仍可盖住磁贴。
        SetWindowPos(_hwnd, _interactive || !AnchorToDesktop ? HwndTop : HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        ApplyDwmAttributes();
    }

    internal static bool IsTileHost(nint hwnd) => _instances.ContainsKey(hwnd);

    internal static bool IsShellDesktopWindow(nint hwnd) => IsDesktopShellWindow(hwnd);

    private static bool IsDesktopShellWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        var sb = new System.Text.StringBuilder(64);
        if (GetClassName(hwnd, sb, sb.Capacity) <= 0) return false;
        string cls = sb.ToString();
        return cls is "Progman" or "WorkerW";
    }

    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            foreach (DesktopWidgetHost item in _activeHosts)
                item.UpdateDesktopAnchor();
            return 0;
        }

        if (_instances.TryGetValue(hwnd, out DesktopWidgetHost? host))
        {
            switch (msg)
            {
                case WmEraseBkgnd:
                    return 1;
                case WmNcPaint:
                    return 0;
                case WmNcActivate:
                    // lParam 为 -1 时 DefWindowProc 不会重画非客户区边框。
                    return DefWindowProc(hwnd, msg, wParam, -1);
                case WmDwmCompositionChanged:
                    host.ApplyDwmAttributes();
                    return 0;
                case WmNcCalcSize:
                    if (wParam != 0) return 0;
                    break;
                case WmMouseActivate:
                    if (!host._interactive) return MaNoActivate;
                    // 主界面重排时会暂时挂回桌面。这里再卸，点击才不会把 Progman 抬到参照窗口前面。
                    host.ReleaseOwnerForInput();
                    return MaActivate;
                case WmSysCommand:
                    if ((wParam.ToInt64() & 0xFFF0) == ScMinimize) return 0;
                    break;
                case WmShowWindow:
                    if (wParam == 0 && host._visible)
                    {
                        ShowWindow(hwnd, SwShowNoActivate);
                        return 0;
                    }
                    break;
                case WmSize:
                {
                    int w = (int)(lParam.ToInt64() & 0xFFFF);
                    int h = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                    if (w > 0 && h > 0)
                    {
                        host._width = w;
                        host._height = h;
                        if (host._xamlSource?.SiteBridge is { } bridge && (w != host._bridgeWidth || h != host._bridgeHeight))
                        {
                            bridge.MoveAndResize(new RectInt32(0, 0, w, h));
                            host._bridgeWidth = w;
                            host._bridgeHeight = h;
                        }
                    }
                    host.ApplyDwmAttributes();
                    return 0;
                }
                case WmSetFocus:
                    if (host._interactive && host._xamlSource is { HasFocus: false })
                        host._xamlSource.NavigateFocus(new XamlSourceFocusNavigationRequest(XamlSourceFocusNavigationReason.Programmatic));
                    return 0;
                case WmWindowPosChanging:
                    if (host._visible)
                    {
                        WindowPos wp = Marshal.PtrToStructure<WindowPos>(lParam);
                        const uint swpHideWindow = 0x0080;
                        if ((wp.flags & swpHideWindow) != 0)
                        {
                            wp.flags &= ~swpHideWindow;
                            Marshal.StructureToPtr(wp, lParam, false);
                        }
                    }
                    break;
                case WmDestroy:
                    _instances.Remove(hwnd);
                    break;
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumChildProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, nint className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int obj);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hwnd, EnumChildProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? cls, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(nint hwnd, string text);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, nint name);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out ScreenRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc proc,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}
