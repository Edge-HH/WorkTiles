using Microsoft.UI.Xaml.Controls;
using WorkTiles.Services;

namespace WorkTiles;

public sealed partial class MainWindow
{
    private int _updateSourceRequest;

    /// <summary>用户放弃更新时删掉本次下载的 GUID 目录；只清理 updates 根下的目录，不碰其他数据。</summary>
    private static void CleanupDownloadedPackage(string package)
    {
        try
        {
            string? directory = Path.GetDirectoryName(package);
            string? root = directory is null ? null : Path.GetDirectoryName(directory);
            if (directory is not null && root is not null && Path.GetFileName(root).Equals("updates", StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
        catch (Exception) { }
    }

    /// <summary>启动时清理一周前的更新 staging；正在进行的更新目录时间很新，不会被误删。</summary>
    private void CleanupStaleUpdateDirectories()
    {
        try
        {
            string root = Path.Combine(_dataStore.DataDirectory, "updates");
            if (!Directory.Exists(root)) return;
            DateTime cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (string directory in Directory.GetDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff) Directory.Delete(directory, true);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    /// <summary>启动时把最近一次 UPDATE_FAILED 的日志显示出来，避免更新失败只在 data/updates 里躺着。</summary>
    private void ShowRecentUpdateFailure()
    {
        try
        {
            string root = Path.Combine(_dataStore.DataDirectory, "updates");
            if (!Directory.Exists(root)) return;
            string? log = Directory.GetDirectories(root)
                .Select(directory => Path.Combine(directory, "update.log"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (log is null) return;
            string text = File.ReadAllText(log);
            int index = text.IndexOf("UPDATE_FAILED", StringComparison.Ordinal);
            if (index < 0) return;
            string message = text[(index + "UPDATE_FAILED".Length)..].Trim();
            if (message.Length > 300) message = message[..300] + "…";
            UpdateStatusText.Text = "上次更新未完成。";
            StorageInfoBar.Title = "上次更新未完成";
            StorageInfoBar.Message = message.Length == 0 ? "请重试更新；详细日志在 data/updates 下。" : message;
            StorageInfoBar.Severity = InfoBarSeverity.Error;
            StorageInfoBar.IsOpen = true;
        }
        catch (Exception) { }
    }
    private async Task RefreshUpdateSourceHintAsync()
    {
        int request = ++_updateSourceRequest;
        if (_settings.UpdateSource != "Gitee") { UpdateSourceHint.Text = "从 GitHub 获取最新发布。"; return; }
        UpdateSourceHint.Text = "正在比较 Gitee 与 GitHub 的发布版本…";
        try
        {
            var gitee = _updateService.GetLatestAsync("Gitee");
            var github = _updateService.GetLatestAsync("GitHub");
            await Task.WhenAll(gitee, github);
            if (request != _updateSourceRequest || _settings.UpdateSource != "Gitee") return;
            string message = ReleaseUpdateService.RecommendGitHub(gitee.Result, github.Result)
                ? $"Gitee 尚未同步 GitHub 的 {github.Result!.Tag}，建议将更新源切换到 GitHub。"
                : "Gitee 同步可能延迟，当前未发现比 GitHub 更旧的版本。";
            UpdateSourceHint.Text = message;
            if (message.Contains("尚未同步"))
            {
                StorageInfoBar.Title = "更新源提醒"; StorageInfoBar.Message = message;
                StorageInfoBar.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning; StorageInfoBar.IsOpen = true;
            }
        }
        catch (Exception)
        {
            if (request == _updateSourceRequest) UpdateSourceHint.Text = "暂时无法比较两个更新源；仍可检查所选源，Gitee 可能有同步延迟。";
        }
    }
}
