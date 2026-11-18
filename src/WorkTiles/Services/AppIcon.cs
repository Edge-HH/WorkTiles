using Microsoft.UI.Windowing;

namespace WorkTiles.Services;

/// <summary>把随程序分发的 logo 设为窗口图标。可执行文件图标由项目文件嵌入，托盘从那里读取。</summary>
internal static class AppIcon
{
    public static void Apply(AppWindow? window)
    {
        if (window is null) return;
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(path)) window.SetIcon(path);
    }
}
