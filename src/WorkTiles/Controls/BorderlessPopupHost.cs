using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;

namespace WorkTiles.Controls;

/// <summary>
/// 托盘菜单用的无边框弹出窗口。WinUI 的 <see cref="Window"/> 始终带着系统底板，
/// 只改 DWM 边框色去不掉那圈浅色边，系统圆角也会沿这条边发虚。
/// 这里和桌面磁贴同一套：WS_POPUP、无重定向位图、空心类画刷、客户区铺满窗口，圆角外才是真正的透明。
/// </summary>
internal sealed class BorderlessPopupHost : IDisposable
{
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x00000008;
    private const uint WsPopup = 0x80000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsClipChildren = 0x02000000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwShowNoActivate = 4;

    private const uint WmDestroy = 0x0002;
    private const uint WmSize = 0x0005;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcPaint = 0x0085;
    private const uint WmNcActivate = 0x0086;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmDwmCompositionChanged = 0x031E;
    private const int MaNoActivate = 3;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmsbtNone = 1;
    private const int HollowBrush = 5;

    private static readonly nint HwndTopmost = -1;
    private static ushort _classAtom;
    private static WndProc? _wndProc;
    private static readonly Dictionary<nint, BorderlessPopupHost> Instances = new();
    private static readonly EnumChildProc StripChildDelegate = StripChild;

    private nint _hwnd;
    private DesktopWindowXamlSource? _source;
    private int _width = 1;
    private int _height = 1;
    private bool _disposed;
    private bool _applying;

    public nint Handle => _hwnd;
    public int Width => _width;
    public int Height => _height;

    public void Create(UIElement content, int x, int y, int width, int height)
    {
        if (_disposed || _hwnd != 0) return;
        EnsureClass();
        if (_classAtom == 0) return;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _hwnd = CreateWindowEx(
            WsExNoRedirectionBitmap | WsExToolWindow | WsExNoActivate | WsExTopmost,
            (nint)_classAtom,
            "WorkTiles",
            unchecked((int)(WsPopup | WsClipSiblings | WsClipChildren)),
            x, y, _width, _height,
            0, 0, GetModuleHandle(null), 0);
        if (_hwnd == 0) return;
        Instances[_hwnd] = this;
        ApplyFrame();

        _source = new DesktopWindowXamlSource();
        _source.Initialize(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _source.Content = content;
        // 系统亚克力是一整块矩形底板，圆角外和边缘都会多出一圈。这里只要全透明，圆角由内容自己画。
        _source.SystemBackdrop = new TransparentDesktopBackdrop();
        _source.SiteBridge?.MoveAndResize(new RectInt32(0, 0, _width, _height));
        ApplyFrame();
    }

    public void Show()
    {
        if (_disposed || _hwnd == 0) return;
        ShowWindow(_hwnd, SwShowNoActivate);
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        ApplyFrame();
        _source?.Content?.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed) ApplyFrame();
        });
    }

    public void MoveAndResize(int x, int y, int width, int height)
    {
        if (_disposed || _hwnd == 0) return;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        bool sizeChanged = _width != width || _height != height;
        _width = width;
        _height = height;
        SetWindowPos(_hwnd, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
        if (!sizeChanged) return;
        _source?.SiteBridge?.MoveAndResize(new RectInt32(0, 0, width, height));
        ApplyFrame();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source is not null)
        {
            _source.Content = null;
            _source.Dispose();
            _source = null;
        }
        if (_hwnd == 0) return;
        nint hwnd = _hwnd;
        _hwnd = 0;
        Instances.Remove(hwnd);
        DestroyWindow(hwnd);
    }

    private void ApplyFrame()
    {
        if (_hwnd == 0 || _applying) return;
        _applying = true;
        try
        {
            Margins margins = new() { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            // 不要系统圆角。那一档圆角自带描边，和内容圆角对不齐，看起来就是白边和发虚的拐角。
            int corner = DwmwcpDoNotRound;
            DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref corner, 4);
            int border = DwmwaColorNone;
            DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref border, 4);
            int backdrop = DwmsbtNone;
            DwmSetWindowAttribute(_hwnd, DwmwaSystemBackdropType, ref backdrop, 4);
            EnumChildWindows(_hwnd, StripChildDelegate, 0);
        }
        finally { _applying = false; }
    }

    private static bool StripChild(nint child, nint _)
    {
        int corner = DwmwcpDoNotRound;
        DwmSetWindowAttribute(child, DwmwaWindowCornerPreference, ref corner, 4);
        int border = DwmwaColorNone;
        DwmSetWindowAttribute(child, DwmwaBorderColor, ref border, 4);
        return true;
    }

    private static void EnsureClass()
    {
        if (_classAtom != 0) return;
        _wndProc = WindowProc;
        WndClassEx wc = new()
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            style = 0x0008,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "WorkTilesPopupHostClass",
            hCursor = LoadCursor(0, (nint)32512),
            // 类画刷留空时，系统会用 COLOR_WINDOW 把圆角外的像素填白。
            hbrBackground = GetStockObject(HollowBrush)
        };
        _classAtom = RegisterClassEx(ref wc);
    }

    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (!Instances.TryGetValue(hwnd, out BorderlessPopupHost? host))
            return DefWindowProc(hwnd, msg, wParam, lParam);
        switch (msg)
        {
            case WmEraseBkgnd:
                return 1;
            case WmNcPaint:
                return 0;
            case WmNcActivate:
                return DefWindowProc(hwnd, msg, wParam, -1);
            case WmNcCalcSize:
                if (wParam != 0) return 0;
                break;
            case WmMouseActivate:
                return MaNoActivate;
            case WmDwmCompositionChanged:
                host.ApplyFrame();
                return 0;
            case WmSize:
            {
                int width = (int)(lParam.ToInt64() & 0xFFFF);
                int height = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (width > 0 && height > 0)
                {
                    host._width = width;
                    host._height = height;
                    host._source?.SiteBridge?.MoveAndResize(new RectInt32(0, 0, width, height));
                }
                host.ApplyFrame();
                return 0;
            }
            case WmDestroy:
                Instances.Remove(hwnd);
                break;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hwnd, EnumChildProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, nint name);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int obj);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}
