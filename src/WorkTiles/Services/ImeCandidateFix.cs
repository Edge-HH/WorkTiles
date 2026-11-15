using System.Runtime.InteropServices;

namespace WorkTiles.Services;

/// <summary>
/// 微信输入法等传统 IMM32 候选窗依赖窗口仍有重定向位图，以及 WM_IME_SETCONTEXT 里的显示标志。
/// WinUI 岛会给子窗口带上 WS_EX_NOREDIRECTIONBITMAP 并清掉显示标志，微软拼音（TSF）仍能画，微信输入法则完全不出现。
/// 这里只在受控时机（挂载、窗口激活）处理本窗口的子窗口：去掉重定向位图限制、补默认输入法上下文、补显示标志。
/// 不再安装全局焦点钩子——那会在每次焦点变化时子类化任意窗口（含对话框/桌面磁贴），曾导致设置页按钮和滚轮全面失效。
/// </summary>
internal static class ImeCandidateFix
{
    private const int GwlpWndProc = -4;
    private const int GwlExStyle = -20;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmImeSetContext = 0x0281;
    private const int IscShowUiAll = unchecked((int)0xC000000F);
    private const int IaceChildren = 0x0001;
    private const int IaceDefault = 0x0010;
    private const int IaceIgnoreNoContext = 0x0020;
    private const nint WsExNoRedirectionBitmap = 0x00200000;

    private static readonly Dictionary<nint, nint> Previous = new();
    private static readonly WndProcDelegate Proc = Hook;
    private static readonly object Gate = new();

    public static void Attach(nint hwnd)
    {
        if (hwnd == 0) return;
        Prepare(hwnd);
        Refresh(hwnd);
    }

    /// <summary>真正接收输入的子窗口是稍后才创建的；窗口激活时再扫一遍子窗口，只补样式与上下文，不碰别的窗口。</summary>
    public static void Refresh(nint hwnd)
    {
        if (hwnd == 0) return;
        EnumChildWindows(hwnd, (child, _) =>
        {
            Prepare(child);
            return true;
        }, 0);
    }

    private static void Prepare(nint hwnd)
    {
        if (hwnd == 0) return;
        lock (Gate)
        {
            try { ImmAssociateContextEx(hwnd, 0, IaceChildren | IaceDefault | IaceIgnoreNoContext); }
            catch (Exception) { }
            // 只改子窗口。顶层窗口靠这个标志画 WinUI，去掉之后整窗会黑。
            if (GetParent(hwnd) != 0)
            {
                nint style = GetWindowLongPtr(hwnd, GwlExStyle);
                if ((style & WsExNoRedirectionBitmap) != 0)
                    SetWindowLongPtr(hwnd, GwlExStyle, style & ~WsExNoRedirectionBitmap);
            }
            if (Previous.ContainsKey(hwnd)) return;
            nint current = GetWindowLongPtr(hwnd, GwlpWndProc);
            if (current == 0) return;
            nint old = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(Proc));
            // 只有真正拿到原子过程才记录；安装失败（返回 0）就把原过程装回去，绝不把窗口交给 DefWindowProc 而丢失输入。
            if (old != 0) Previous[hwnd] = old;
            else SetWindowLongPtr(hwnd, GwlpWndProc, current);
        }
    }

    private static nint Hook(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmImeSetContext && wParam != 0)
            lParam |= IscShowUiAll;
        nint old;
        lock (Gate) { Previous.TryGetValue(hwnd, out old); }
        nint result = old != 0 ? CallWindowProc(old, hwnd, msg, wParam, lParam) : DefWindowProc(hwnd, msg, wParam, lParam);
        // WM_NCDESTROY 才是窗口的最后一条消息。提前在 WM_DESTROY 摘掉子类化，后面的收尾会落到 DefWindowProc。
        if (msg == WmNcDestroy)
            lock (Gate) { Previous.Remove(hwnd); }
        return result;
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);
    private delegate bool EnumProc(nint hwnd, nint lParam);

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

    [DllImport("imm32.dll")]
    private static extern bool ImmAssociateContextEx(nint hwnd, nint himc, int flags);
}