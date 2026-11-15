using System.Runtime.InteropServices;

namespace WorkTiles.Services;

/// <summary>
/// 微信输入法等传统候选窗依赖 WM_IME_SETCONTEXT 里的显示标志。
/// WinUI 岛有时把这些标志清掉，微软拼音仍能画，第三方候选窗则完全不出现。
/// 这里在消息进系统处理前补上“显示全部输入法界面”。
/// </summary>
internal static class ImeCandidateFix
{
    private const int GwlpWndProc = -4;
    private const uint WmImeSetContext = 0x0281;
    private const int IscShowUiAll = unchecked((int)0xC000000F);

    private static readonly Dictionary<nint, nint> Previous = new();
    private static readonly WndProcDelegate Proc = Hook;

    public static void Attach(nint hwnd)
    {
        if (hwnd == 0 || !Previous.TryAdd(hwnd, 0)) return;
        nint old = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(Proc));
        Previous[hwnd] = old;
        EnumChildWindows(hwnd, (child, _) =>
        {
            Attach(child);
            return true;
        }, 0);
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

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint proc, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hwnd, EnumProc proc, nint lParam);
}
