using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WorkTiles.Controls;
using WorkTiles.Services;

namespace WorkTiles;

public sealed partial class MainWindow
{
    private const int ShowDesktopSignalWindowMs = 2000;
    private const int RestoreAfterToggleDelayMs = 150;

    private enum ShowDesktopYield
    {
        None,
        /// <summary>已离开全屏呈现器，等壳把窗口最小化并记入还原名单。</summary>
        Preparing,
        /// <summary>已经因为显示桌面收起，回来时要回到全屏。</summary>
        Parked
    }

    private ShowDesktopYield _yield = ShowDesktopYield.None;
    private bool _yieldedOutsideShellList;
    private bool _parkedWasExamForced;
    private bool _showDesktopInstalled;
    private bool _parking;
    private int _winDHookDepth;
    private bool _winDChordHeld;
    private long _winDAt;
    private long _foreignMinimizeAt;

    private static MainWindow? _showDesktopOwner;
    private static LowLevelKeyboardProc? _keyboardProc;
    private static WinEventProc? _showDesktopWinEventProc;
    private static nint _keyboardHook;
    private static nint _foregroundHook;
    private static nint _minimizeStartHook;

    /// <summary>
    /// 全屏呈现器没有最小化状态，显示桌面不会收它。Win+D 到达壳之前先切回重叠窗口，
    /// 让壳自己把它记进还原名单；来不及或走任务栏按钮时，再在桌面成为前台后补一次最小化。
    /// </summary>
    private void InstallShowDesktopMonitor()
    {
        if (_showDesktopInstalled) return;
        _showDesktopInstalled = true;
        _showDesktopOwner = this;
        DesktopWidgetHost.BeforeDesktopForeground = OnDesktopBecameForeground;
        DesktopWidgetHost.AppCoversDesktop = AppCoversDesktop;

        _keyboardProc = OnKeyboardHook;
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, 0, 0);

        _showDesktopWinEventProc = OnShowDesktopWinEvent;
        // 两条窄钩子：前台切换和最小化开始。宽范围会把菜单、滚动也派到 UI 线程。
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _showDesktopWinEventProc, 0, 0, WinEventOutOfContext);
        _minimizeStartHook = SetWinEventHook(EventSystemMinimizeStart, EventSystemMinimizeStart, 0, _showDesktopWinEventProc, 0, 0, WinEventOutOfContext);

        if (_appWindow is not null)
            _appWindow.Changed += OnAppWindowChangedForShowDesktop;
        Activated += OnActivatedForShowDesktop;
    }

    private void UninstallShowDesktopMonitor()
    {
        if (!_showDesktopInstalled && _showDesktopOwner != this) return;
        _showDesktopInstalled = false;
        if (_showDesktopOwner == this) _showDesktopOwner = null;
        if (DesktopWidgetHost.BeforeDesktopForeground == OnDesktopBecameForeground)
            DesktopWidgetHost.BeforeDesktopForeground = null;
        if (DesktopWidgetHost.AppCoversDesktop == AppCoversDesktop)
            DesktopWidgetHost.AppCoversDesktop = null;
        if (_keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        if (_foregroundHook != 0)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }
        if (_minimizeStartHook != 0)
        {
            UnhookWinEvent(_minimizeStartHook);
            _minimizeStartHook = 0;
        }
        _keyboardProc = null;
        _showDesktopWinEventProc = null;
        if (_appWindow is not null)
            _appWindow.Changed -= OnAppWindowChangedForShowDesktop;
        Activated -= OnActivatedForShowDesktop;
        _yield = ShowDesktopYield.None;
        _yieldedOutsideShellList = false;
    }

    private void OnAppWindowChangedForShowDesktop(AppWindow sender, AppWindowChangedEventArgs args)
        => OnShowDesktopWindowChanged();

    private void OnActivatedForShowDesktop(object sender, WindowActivatedEventArgs args)
        => OnShowDesktopWindowChanged();

    private static nint OnKeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && lParam != 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown || wParam == WmKeyUp || wParam == WmSysKeyUp))
        {
            try
            {
                LowLevelKeyboardInput input = Marshal.PtrToStructure<LowLevelKeyboardInput>(lParam);
                bool keyUp = (input.Flags & LlkhfUp) != 0;
                MainWindow? window = _showDesktopOwner;
                if (window is not null)
                {
                    if (keyUp)
                    {
                        if (input.VirtualKey is VkD or VkLwin or VkRwin)
                            window.ReleaseWinDChord();
                    }
                    else if (IsWinDChord(input.VirtualKey))
                    {
                        window.OnWinDChord();
                    }
                }
            }
            catch
            {
                // 低级键盘钩子里抛异常会让系统卸掉钩子，后续按键也不能丢。
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static void OnShowDesktopWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjidWindow) return;
        MainWindow? window = _showDesktopOwner;
        if (window is null) return;
        try
        {
            if (eventType == EventSystemMinimizeStart) window.NoteForeignMinimize(hwnd);
            else if (eventType == EventSystemForeground && DesktopWidgetHost.IsShellDesktopWindow(hwnd))
                window.OnDesktopBecameForeground();
        }
        catch
        {
            // 显示桌面的事件钩子不能把主窗口打崩。
        }
    }

    private static bool IsWinDChord(uint virtualKey)
    {
        if (virtualKey != VkD) return false;
        if ((GetAsyncKeyState(VkShift) & 0x8000) != 0) return false;
        if ((GetAsyncKeyState(VkControl) & 0x8000) != 0) return false;
        if ((GetAsyncKeyState(VkMenu) & 0x8000) != 0) return false;
        return (GetAsyncKeyState(VkLwin) & 0x8000) != 0 || (GetAsyncKeyState(VkRwin) & 0x8000) != 0;
    }

    private void ReleaseWinDChord() => _winDChordHeld = false;

    /// <summary>
    /// 在壳处理 Win+D 之前离开全屏。不要在钩子里最小化：窗口已经是图标时，壳不会把它写入还原名单，再按一次就回不来。
    /// 呈现器切换必须同步做完再返回，否则壳仍会跳过全屏窗口。
    /// </summary>
    private void OnWinDChord()
    {
        if (_winDChordHeld) return;
        _winDChordHeld = true;
        _winDAt = Environment.TickCount64;
        if (_yield == ShowDesktopYield.Parked && _yieldedOutsideShellList)
        {
            QueueRestoreAfterShellToggle();
            return;
        }
        if (!_isFullScreen || _appWindow is null) return;
        nint hwnd = WindowHandle;
        if (!IsWindowShowing(hwnd)) return;
        _yield = ShowDesktopYield.Preparing;
        _yieldedOutsideShellList = false;
        _parkedWasExamForced = _examForcedFullScreen;
        _winDHookDepth++;
        try { SetFullScreen(false); }
        finally { _winDHookDepth--; }
        // 钩子返回后壳才处理 Win+D。若它仍跳过本窗口，稍后再自己收起，避免停在刚切出的重叠窗口上。
        QueueParkIfShellSkipped();
    }

    private void NoteForeignMinimize(nint hwnd)
    {
        if (hwnd == 0 || hwnd == WindowHandle) return;
        if (DesktopWidgetHost.IsTileHost(hwnd)) return;
        _foreignMinimizeAt = Environment.TickCount64;
    }

    /// <summary>桌面已成为前台。全屏窗口若还铺在屏幕上，补一次最小化。</summary>
    private void OnDesktopBecameForeground()
    {
        if (_winDHookDepth > 0 || _parking) return;
        nint hwnd = WindowHandle;
        if (hwnd == 0 || !IsWindowVisible(hwnd)) return;
        if (IsIconic(hwnd) || Minimized)
        {
            if (_yield == ShowDesktopYield.Preparing)
                _yield = ShowDesktopYield.Parked;
            return;
        }
        // Preparing 表示 Win+D 已经把呈现器切走，但壳没有收掉窗口，必须自己补上。
        if (_yield != ShowDesktopYield.Preparing)
        {
            if (!_isFullScreen) return;
            if (!ShouldYieldToShowDesktop(hwnd)) return;
        }
        ParkFromShowDesktop();
    }

    private void ParkFromShowDesktop()
    {
        if (_parking || _yield == ShowDesktopYield.Parked) return;
        _parking = true;
        bool parked = false;
        try
        {
            if (_yield != ShowDesktopYield.Preparing)
                _parkedWasExamForced = _examForcedFullScreen;
            _yield = ShowDesktopYield.Preparing;
            _yieldedOutsideShellList = true;
            if (_isFullScreen) SetFullScreen(false);
            nint hwnd = WindowHandle;
            if (hwnd != 0 && !IsIconic(hwnd))
            {
                if (_appWindow?.Presenter is OverlappedPresenter overlapped
                    && overlapped.State != OverlappedPresenterState.Minimized)
                {
                    overlapped.IsMinimizable = true;
                    overlapped.Minimize();
                }
                if (!IsIconic(hwnd) && !Minimized)
                    ShowWindow(hwnd, SwForceMinimize);
            }
            if (hwnd == 0 || !IsWindowVisible(hwnd) || IsIconic(hwnd) || Minimized)
            {
                _yield = ShowDesktopYield.Parked;
                parked = true;
            }
        }
        finally { _parking = false; }
        // 磁贴在主窗口还铺着时不会抬升。收起之后补一次，否则它们会被刚抬起的桌面盖住。
        if (parked) DesktopWidgetHost.RaiseVisibleHostsAboveDesktop();
    }

    private void QueueParkIfShellSkipped()
    {
        DispatcherQueue.TryEnqueue(StartParkIfSkippedTimer);
    }

    private void StartParkIfSkippedTimer()
    {
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_showDesktopInstalled || _winDHookDepth > 0)
            {
                if (_showDesktopInstalled && _winDHookDepth > 0) StartParkIfSkippedTimer();
                return;
            }
            if (_yield != ShowDesktopYield.Preparing) return;
            if (!IsWindowShowing(WindowHandle)) return;
            ParkFromShowDesktop();
        };
        timer.Start();
    }

    private bool ShouldYieldToShowDesktop(nint hwnd)
    {
        long now = Environment.TickCount64;
        if (IsRecent(_winDAt, now) || IsRecent(_foreignMinimizeAt, now) || CursorOnShowDesktopButton())
            return true;
        // 没有显示桌面的信号，光标又在本窗口没盖住的屏幕上：是去点另一块屏的桌面。
        return !CursorOnUncoveredMonitor(hwnd);
    }

    /// <summary>主窗口仍可见且未最小化时，磁贴不要抬到它上面。不限全屏。</summary>
    private bool AppCoversDesktop()
    {
        if (_yield == ShowDesktopYield.Parked) return false;
        return IsWindowShowing(WindowHandle);
    }

    private void OnShowDesktopWindowChanged()
    {
        if (_parking || _yield == ShowDesktopYield.None) return;
        if (_yield == ShowDesktopYield.Preparing)
        {
            bool showDesktop = IsRecentShowDesktop();
            if (IsParkedDown() && showDesktop)
                _yield = ShowDesktopYield.Parked;
            else if (!showDesktop)
                _yield = ShowDesktopYield.None;
        }
        ResumeFullScreenAfterShowDesktop();
        // 显示桌面时磁贴被抬到壁纸之上。窗口回来后沉回桌面带，避免停在主界面前面。
        if (_yield == ShowDesktopYield.None && AppCoversDesktop())
            StackDesktopCommand();
    }

    private void ResumeFullScreenAfterShowDesktop()
    {
        if (_yield != ShowDesktopYield.Parked || IsParkedDown()) return;
        _yield = ShowDesktopYield.None;
        _yieldedOutsideShellList = false;
        // 考试把原本的窗口模式拉进全屏；人在桌面时考试结束，回来应停在窗口模式。
        if (_parkedWasExamForced && !_examOverlayVisible)
        {
            _parkedWasExamForced = false;
            return;
        }
        _parkedWasExamForced = false;
        if (!_isFullScreen) SetFullScreen(true);
    }

    private void QueueRestoreAfterShellToggle()
    {
        DispatcherQueue.TryEnqueue(StartRestoreTimer);
    }

    private void StartRestoreTimer()
    {
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(RestoreAfterToggleDelayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RestoreYieldedFullScreen();
        };
        timer.Start();
    }

    /// <summary>
    /// 自己最小化的窗口不在壳的还原名单里，第二次 Win+D 壳不会把它带回来。
    /// 等壳先完成切换再还原，避免和它抢前台。
    /// </summary>
    private void RestoreYieldedFullScreen()
    {
        if (!_showDesktopInstalled || _yield != ShowDesktopYield.Parked || !_yieldedOutsideShellList) return;
        _yield = ShowDesktopYield.None;
        _yieldedOutsideShellList = false;
        bool examEnded = _parkedWasExamForced && !_examOverlayVisible;
        _parkedWasExamForced = false;
        nint hwnd = WindowHandle;
        if (hwnd != 0) SingleInstanceService.BringToForeground(hwnd);
        if (!examEnded && !_isFullScreen) SetFullScreen(true);
    }

    private bool IsRecentShowDesktop()
    {
        long now = Environment.TickCount64;
        return IsRecent(_winDAt, now) || IsRecent(_foreignMinimizeAt, now);
    }

    private static bool IsRecent(long stamp, long now)
        => stamp != 0 && unchecked(now - stamp) < ShowDesktopSignalWindowMs;

    private bool IsParkedDown()
    {
        nint hwnd = WindowHandle;
        if (hwnd == 0 || !IsWindowVisible(hwnd)) return true;
        return IsIconic(hwnd) || Minimized;
    }

    private bool IsWindowShowing(nint hwnd)
        => hwnd != 0 && IsWindowVisible(hwnd) && !IsIconic(hwnd);

    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private static bool CursorOnUncoveredMonitor(nint hwnd)
    {
        if (!GetCursorPos(out WinPoint cursor) || !GetWindowRect(hwnd, out WinRect window)) return false;
        nint cursorMonitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        nint windowMonitor = MonitorFromRect(ref window, MonitorDefaultToNearest);
        return cursorMonitor != 0 && windowMonitor != 0 && cursorMonitor != windowMonitor;
    }

    private static bool CursorOnShowDesktopButton()
    {
        if (!GetCursorPos(out WinPoint cursor)) return false;
        if (IsShowDesktopButtonHit(FindWindow("Shell_TrayWnd", null), cursor)) return true;
        nint tray = 0;
        while (true)
        {
            tray = FindWindowEx(0, tray, "Shell_SecondaryTrayWnd", null);
            if (tray == 0) return false;
            if (IsShowDesktopButtonHit(tray, cursor)) return true;
        }
    }

    private static bool IsShowDesktopButtonHit(nint tray, WinPoint cursor)
    {
        if (tray == 0 || !GetWindowRect(tray, out WinRect rect)) return false;
        bool horizontal = rect.Right - rect.Left >= rect.Bottom - rect.Top;
        if (horizontal)
            return cursor.X >= rect.Right - 16 && cursor.X < rect.Right + 2
                && cursor.Y >= rect.Top - 2 && cursor.Y < rect.Bottom + 2;
        return cursor.Y >= rect.Bottom - 16 && cursor.Y < rect.Bottom + 2
            && cursor.X >= rect.Left - 2 && cursor.X < rect.Right + 2;
    }

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int LlkhfUp = 0x80;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const int VkD = 0x44;
    private const int SwForceMinimize = 11;
    private const int ObjidWindow = 0;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint WinEventOutOfContext = 0;
    private const uint MonitorDefaultToNearest = 2;

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);
    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out WinRect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(WinPoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref WinRect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? cls, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);
}
