using System.Runtime.InteropServices;

namespace WorkTiles.Services;

/// <summary>
/// 微信输入法等传统候选窗依赖 WM_IME_SETCONTEXT 里的显示标志，也依赖窗口仍有重定向位图。
/// WinUI 岛会清掉这些标志，并带上 WS_EX_NOREDIRECTIONBITMAP。微软拼音仍能画，微信输入法则完全不出现。
/// 焦点落到新的输入窗口时再补一次，因为真正接收输入的子窗口是稍后才创建的。
/// </summary>
internal static class ImeCandidateFix
{
    private const int GwlpWndProc = -4;
    private const int GwlExStyle = -20;
    private const uint WmImeSetContext = 0x0281;
    private const int IscShowUiAll = unchecked((int)0xC000000F);
    private const int IaceChildren = 0x0001;
    private const int IaceDefault = 0x0010;
    private const int IaceIgnoreNoContext = 0x0020;
    private const nint WsExNoRedirectionBitmap = 0x00200000;
    private const uint EventObjectFocus = 0x8005;

    private static readonly Dictionary<nint, nint> Previous = new();
    private static readonly WndProcDelegate Proc = Hook;
    private static readonly WinEventDelegate FocusProc = OnFocus;
    private static nint _focusHook;

    public static void Attach(nint hwnd)
    {
        if (hwnd == 0) return;
        EnsureFocusHook();
        Prepare(hwnd);
        EnumChildWindows(hwnd, (child, _) =>
        {
            Prepare(child);
            return true;
        }, 0);
    }

    private static void EnsureFocusHook()
    {
        if (_focusHook != 0) return;
        _focusHook = SetWinEventHook(EventObjectFocus, EventObjectFocus, 0, FocusProc, (uint)Environment.ProcessId, 0, 0);
    }

    private static void OnFocus(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != 0) Prepare(hwnd);
    }

    private static void Prepare(nint hwnd)
    {
        if (hwnd == 0) return;
        try { ImmAssociateContextEx(hwnd, 0, IaceChildren | IaceDefault | IaceIgnoreNoContext); }
        catch (Exception) { }
        // 只改子窗口。顶层窗口靠这个标志画 WinUI，去掉之后整窗会黑。
        if (GetParent(hwnd) != 0)
        {
            nint style = GetWindowLongPtr(hwnd, GwlExStyle);
            if ((style & WsExNoRedirectionBitmap) != 0)
                SetWindowLongPtr(hwnd, GwlExStyle, style & ~WsExNoRedirectionBitmap);
        }
        if (Previous.TryAdd(hwnd, 0))
        {
            nint old = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(Proc));
            Previous[hwnd] = old;
        }
    }

    private static nint Hook(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmImeSetContext && wParam != 0)
            lParam |= IscShowUiAll;
        return Previous.TryGetValue(hwnd, out nint old) && old != 0
            ? CallWindowProc(old, hwnd, msg, wParam, lParam)
            : DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);
    private delegate bool EnumProc(nint hwnd, nint lParam);
    private delegate void WinEventDelegate(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint proc, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hwnd, EnumProc proc, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventDelegate proc, uint process, uint thread, uint flags);

    [DllImport("imm32.dll")]
    private static extern bool ImmAssociateContextEx(nint hwnd, nint himc, int flags);
}
