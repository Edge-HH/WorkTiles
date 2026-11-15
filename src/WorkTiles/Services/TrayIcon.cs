using System.Runtime.InteropServices;
using System.Text;

namespace WorkTiles.Services;

/// <summary>任务栏托盘图标。左键打开主界面，右键弹出重启、设置、桌面磁贴和退出。</summary>
internal sealed class TrayIcon : IDisposable
{
    public const int CommandOpen = 1;
    public const int CommandShowTiles = 2;
    public const int CommandHideTiles = 3;
    public const int CommandSettings = 4;
    public const int CommandRestart = 5;
    public const int CommandExit = 6;

    private const int GwlpWndProc = -4;
    private const uint CallbackMessage = 0x8001;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;

    private static readonly Dictionary<nint, nint> Previous = new();
    private static readonly WndProcDelegate Proc = Hook;
    private static TrayIcon? _current;

    private readonly nint _hwnd;
    private readonly Func<bool> _tilesVisible;
    private bool _added;

    public event Action<int>? Command;

    public TrayIcon(nint hwnd, Func<bool> tilesVisible)
    {
        _hwnd = hwnd;
        _tilesVisible = tilesVisible;
        _current = this;
        if (Previous.TryAdd(hwnd, 0))
        {
            nint old = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(Proc));
            Previous[hwnd] = old;
        }
        AddIcon();
    }

    public void Dispose()
    {
        if (_current == this) _current = null;
        if (!_added) return;
        NotifyIconData data = new() { cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _hwnd, uID = 1 };
        ShellNotifyIcon(NimDelete, ref data);
        _added = false;
    }

    private void AddIcon()
    {
        ushort index = 0;
        StringBuilder path = new(260);
        path.Append(Environment.ProcessPath ?? "");
        nint icon = ExtractAssociatedIcon(0, path, ref index);
        if (icon == 0) icon = LoadIcon(0, (nint)32512);
        NotifyIconData data = new()
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = icon,
            szTip = "WorkTiles"
        };
        _added = ShellNotifyIcon(NimAdd, ref data);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, MfString, CommandOpen, "打开主界面");
            AppendMenu(menu, MfString, _tilesVisible() ? CommandHideTiles : CommandShowTiles,
                _tilesVisible() ? "隐藏桌面磁贴" : "显示桌面磁贴");
            AppendMenu(menu, MfSeparator, 0, "");
            AppendMenu(menu, MfString, CommandSettings, "应用设置");
            AppendMenu(menu, MfString, CommandRestart, "重启");
            AppendMenu(menu, MfString, CommandExit, "退出");
            GetCursorPos(out Point point);
            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, 0, _hwnd, 0);
            if (command != 0) Command?.Invoke(command);
        }
        finally { DestroyMenu(menu); }
    }

    private static nint Hook(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage && _current is { } tray && tray._hwnd == hwnd)
        {
            uint mouse = unchecked((uint)lParam);
            if (mouse == WmLButtonUp) tray.Command?.Invoke(CommandOpen);
            else if (mouse == WmRButtonUp) tray.ShowMenu();
        }
        return Previous.TryGetValue(hwnd, out nint old) && old != 0
            ? CallWindowProc(old, hwnd, msg, wParam, lParam)
            : DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractAssociatedIcon(nint hInst, StringBuilder iconPath, ref ushort index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint hInst, nint name);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint proc, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint menu, uint flags, nint id, string text);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
