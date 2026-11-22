using Microsoft.Win32;

namespace WorkTiles.Services;

/// <summary>把开机自启写进当前用户的 Run 项。静默启动只加在这条启动命令上，手动打开软件仍显示主窗口。</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WorkTiles";

    public static void Apply(bool enabled, bool silent)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WorkTiles.exe");
        if (!enabled)
        {
            // 只删除指向本安装目录的值；另一份安装的同名自启不能被我方关闭。
            if (key.GetValue(ValueName) is string current && current.Contains(executable, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        string command = $"\"{executable}\"";
        if (silent) command += " --silent";
        key.SetValue(ValueName, command);
    }
}
