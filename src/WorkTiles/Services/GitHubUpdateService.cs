using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WorkTiles.Services;

public sealed record ReleaseAsset(string Name, string Url, string? Digest);
public sealed record GitHubReleaseUpdate(Version Version, string Tag, string AssetName, string DownloadUrl, string? Digest)
{
    public IReadOnlyList<ReleaseAsset> Parts { get; init; } = [];

    /// <summary>Release 描述原文（GitHub/Gitee 的 body），用于应用内展示更新日志。</summary>
    public string? Notes { get; init; }
}

public sealed class GitHubUpdateService
{
    public static void LaunchInstaller(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    internal static bool IsInstallable(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}
