using System.Runtime.InteropServices;

namespace WorkTiles;

/// <summary>给无边框窗口补上圆角、工具窗口样式，避免弹出层自己占一个任务栏按钮。</summary>
internal static class WindowChrome
{
    public static void Round(nint hwnd)
    {
        int preference = 2;
        DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
    }

    public static void AsToolWindow(nint hwnd)
    {
        const int exStyle = -20;
        const nint tool = 0x00000080;
        const nint noActivate = 0x08000000;
        nint style = GetWindowLongPtr(hwnd, exStyle);
        SetWindowLongPtr(hwnd, exStyle, style | tool | noActivate);
    }

    public static void ShowWithoutActivating(nint hwnd) => ShowWindow(hwnd, 4);

    public static uint Dpi(nint hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96 : dpi;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
