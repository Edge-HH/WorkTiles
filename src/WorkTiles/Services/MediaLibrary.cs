using System.Security.Cryptography;
using System.Text.Json;

namespace WorkTiles.Services;

/// <summary>最近媒体的使用面；磁贴与背景板各记各的历史，互不串扰。</summary>
public enum MediaScope
{
    /// <summary>图片入口：作业附件与导出背景，只显示图片。</summary>
    Images,
    /// <summary>磁贴背景。</summary>
    Tile,
    /// <summary>背景板：跨区、时钟与作业板共用一份。</summary>
    Background
}

/// <summary>最近媒体与背景共用自有资源副本，原文件或项目删除后仍可再次选择。</summary>
public sealed class MediaLibrary(string dataDirectory)
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"];
    public static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".webm"];
    private readonly string _directory = Path.Combine(dataDirectory, "backgrounds");
    private string HistoryPath(MediaScope scope) => Path.Combine(_directory, scope switch
    {
        MediaScope.Tile => "recent-media-tile.json",
        MediaScope.Background => "recent-media-background.json",
        _ => "recent-images.json"
    });
    // 同一数据目录会被多个 MediaLibrary 实例（设置页、导出、备份）同时读写同一批历史文件，
    // 锁必须静态共享，实例级锁挡不住跨实例并发。
    private static readonly SemaphoreSlim _gate = new(1);
    /// <summary>拆分使用面之前的合并历史，只作为新历史缺失时的起步来源。</summary>
    private string LegacyMediaHistoryPath => Path.Combine(_directory, "recent-media.json");
    public event Action? Changed;
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsWeb(string path) => Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase);
    public static bool IsSupported(string path) => IsImage(path) || IsVideo(path) || IsWeb(path);
    public static string DisplayName(string path)
    {
        string name = Path.GetFileName(path);
        return name.Length > 17 && name[16] == '_' && name.Take(16).All(Uri.IsHexDigit) ? name[17..] : name;
    }

    /// <summary>浏览时显示壁纸名。没有单独记下名字时退回文件名。</summary>
    public string DisplayTitle(string path)
    {
        Dictionary<string, MediaLabel> labels = ReadLabels();
        if (!labels.TryGetValue(path, out MediaLabel? label) || string.IsNullOrWhiteSpace(label.Title))
        {
            string full = Path.GetFullPath(path);
            label = labels.FirstOrDefault(pair =>
                string.Equals(pair.Key, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, full, StringComparison.OrdinalIgnoreCase)).Value;
        }
        return string.IsNullOrWhiteSpace(label?.Title) ? DisplayName(path) : label.Title;
    }

    /// <summary>动态壁纸的静态预览图。毛玻璃和最近列表都用它，避免去采样网页或视频合成层。</summary>
    public static string? PreviewFile(string path)
    {
        string beside = path + ".preview.png";
        return File.Exists(beside) ? beside : null;
    }

    public IReadOnlyList<string> RecentImages() => RecentImages(false);

    private IReadOnlyList<string> RecentImages(bool strict)
    {
        string history = HistoryPath(MediaScope.Images);
        if (!File.Exists(history)) return [];
        try { return (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(history)) ?? [])
            .Where(path => IsImage(path) && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray(); }
        catch (Exception ex) when (!strict && (ex is IOException or UnauthorizedAccessException or JsonException)) { return []; }
    }

    /// <summary>按使用面读取最近媒体；磁贴与背景板各用各的，视频和网页不会串到另一侧。</summary>
    public IReadOnlyList<string> RecentMedia(MediaScope scope) => RecentMedia(scope, false);

    private IReadOnlyList<string> RecentMedia(MediaScope scope, bool strict)
    {
        if (scope == MediaScope.Images) return RecentImages(strict);
        string history = HistoryPath(scope);
        // 本使用面首次记录前从合并历史或图片历史起步，升级后既有条目仍可见。
        if (!File.Exists(history)) history = File.Exists(LegacyMediaHistoryPath) ? LegacyMediaHistoryPath : HistoryPath(MediaScope.Images);
        if (!File.Exists(history)) return [];
        try { return (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(history)) ?? [])
            .Where(path => IsSupported(path) && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray(); }
        catch (Exception ex) when (!strict && (ex is IOException or UnauthorizedAccessException or JsonException)) { return []; }
    }

    /// <summary>全部使用面的最近媒体合并去重；备份用它收齐要带走的资源文件。</summary>
    public IReadOnlyList<string> RecentMedia() => RecentMedia(MediaScope.Images)
        .Concat(RecentMedia(MediaScope.Tile)).Concat(RecentMedia(MediaScope.Background))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>清理专用：任一历史无法读取时抛出，调用方应放弃本轮清理而不是把空历史当成无引用。</summary>
    public IReadOnlyList<string> RecentMediaStrict() => RecentMedia(MediaScope.Images, true)
        .Concat(RecentMedia(MediaScope.Tile, true)).Concat(RecentMedia(MediaScope.Background, true))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// 保守清理自有媒体副本：只删 backgrounds 下超过期限、未被项目/设置/最近历史引用的图片或视频；
    /// 预览图跟随其视频一起判断；正在被占用的文件删除失败时保留。
    /// </summary>
    public int CleanupUnreferenced(DateTime cutoffUtc, IReadOnlySet<string> referenced)
    {
        if (!Directory.Exists(_directory)) return 0;
        // 导入副本名是 hash16_原名，基本唯一；便携目录被移动后旧绝对路径失效，用文件名兜底避免整库误删。
        HashSet<string> referencedNames = new(referenced.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        bool IsReferenced(string full)
        {
            if (referenced.Contains(full) || referencedNames.Contains(Path.GetFileName(full))) return true;
            const string previewSuffix = ".preview.png";
            return full.EndsWith(previewSuffix, StringComparison.OrdinalIgnoreCase) &&
                referenced.Contains(full[..^previewSuffix.Length]);
        }
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(_directory, "*", options))
        {
            if ((!IsImage(file) && !IsVideo(file)) || IsReferenced(Path.GetFullPath(file))) continue;
            if (File.GetLastWriteTimeUtc(file) >= cutoffUtc) continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // 只移除清理后变空的子目录，不碰 backgrounds 根目录和仍装着历史/媒体的目录。
        foreach (string directory in Directory.EnumerateDirectories(_directory, "*", options)
            .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    private async Task RecordRecentAsync(string owned, MediaScope scope)
    {
        Directory.CreateDirectory(_directory);
        await Save(HistoryPath(scope), RecentMedia(scope), owned);
        // 任一入口选中的图片也进图片历史，附件与导出的“最近使用的图像”保持覆盖全部图片。
        if (scope != MediaScope.Images && IsImage(owned)) await Save(HistoryPath(MediaScope.Images), RecentImages(), owned);
    }

    private static async Task Save(string path, IEnumerable<string> history, string owned)
    {
        // 唯一临时名避免跨实例并发写同一目标时互相覆盖；Flush(true) 落盘后再原子替换，断电不留半份历史。
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(history
            .Where(item => !string.Equals(item, owned, StringComparison.OrdinalIgnoreCase)).Prepend(owned).Take(8));
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                await stream.WriteAsync(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>还原备份后整体替换各使用面的历史；空列表表示备份里有意清空。</summary>
    public void ReplaceHistory(IReadOnlyList<string> images, IReadOnlyList<string> tile, IReadOnlyList<string> background)
    {
        Directory.CreateDirectory(_directory);
        void Write(string path, IEnumerable<string> history)
        {
            // 还原是同步批量操作，不能同步等待异步导入持有的锁（会卡死 UI 线程续体）；
            // 改用唯一临时名 + 落盘 + 原子替换，保证单次写入不撕裂。
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    JsonSerializer.Serialize(stream, history.Take(8));
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Write(HistoryPath(MediaScope.Images), images);
        Write(HistoryPath(MediaScope.Tile), tile);
        Write(HistoryPath(MediaScope.Background), background);
    }

    /// <summary>旧版备份只有一份合并的最近媒体；磁贴与背景板都从它起步。</summary>
    public void ReplaceHistory(IReadOnlyList<string> images, IReadOnlyList<string> media) => ReplaceHistory(images, media, media);

    public async Task<string> UseRecentAsync(string path, MediaScope scope)
    {
        await _gate.WaitAsync();
        try
        {
            if (!RecentMedia(scope).Contains(path, StringComparer.OrdinalIgnoreCase))
                throw new FileNotFoundException("最近媒体已不存在，请重新导入。", path);
            // 已导入网页保留整个资源目录；复选不重新复制项目或计算大视频哈希。
            await RecordRecentAsync(path, scope);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        return path;
    }

    public async Task<string> ImportAsync(string source, MediaScope scope = MediaScope.Images)
    {
        if (IsWeb(source)) throw new InvalidDataException("请从 Wallpaper Engine 导入完整网页壁纸项目。");
        if (!IsSupported(source)) throw new InvalidDataException("不支持此媒体格式。");
        if (scope == MediaScope.Images && !IsImage(source)) throw new InvalidDataException("图片入口只支持图片格式。");
        await _gate.WaitAsync();
        string owned;
        try
        {
            // 哈希和复制在后台执行，大视频不阻塞设置页；同内容复用一份资源。
            owned = await Task.Run(() =>
            {
                Directory.CreateDirectory(_directory);
                using var input = File.OpenRead(source);
                string hash = Convert.ToHexString(SHA256.HashData(input));
                string destination = Path.Combine(_directory, hash[..16] + "_" + DisplayName(source));
                if (!File.Exists(destination))
                {
                    string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        input.Position = 0;
                        using (var output = File.Create(temporary)) input.CopyTo(output);
                        File.Move(temporary, destination, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                else
                {
                    // 复用旧副本时刷新时间，避免它被“超过 90 天且未被快照引用”的清理误删。
                    File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
                }
                return destination;
            });
            await RecordRecentAsync(owned, scope);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        return owned;
    }

    public async Task<string> ImportWallpaperAsync(WallpaperProject project, MediaScope scope)
    {
        if (scope == MediaScope.Images) throw new InvalidDataException("图片入口不能导入动态壁纸。");
        if (project.Kind != "web")
        {
            string video = await ImportAsync(project.Path, scope);
            await RememberWallpaperAsync(video, project);
            return video;
        }
        // 网页需要完整相对目录；先在临时目录复制完成，再发布入口，避免队列读到半个项目。
        string directory = Path.Combine(dataDirectory, "web-wallpapers");
        string destination = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        string temporary = destination + ".tmp";
        await _gate.WaitAsync();
        string owned;
        try
        {
        owned = await Task.Run(() =>
        {
            Directory.CreateDirectory(directory);
            try
            {
                CopyDirectory(project.Directory, temporary);
                string relative = Path.GetRelativePath(project.Directory, project.Path);
                string entry = Path.GetFullPath(Path.Combine(temporary, relative));
                if (!entry.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
                    throw new InvalidDataException("网页壁纸入口不在项目目录内。");
                Directory.Move(temporary, destination);
                return Path.Combine(destination, relative);
            }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        });
        await RecordRecentAsync(owned, scope);
        }
        finally { _gate.Release(); }
        await RememberWallpaperAsync(owned, project);
        Changed?.Invoke();
        return owned;
    }

    private async Task RememberWallpaperAsync(string owned, WallpaperProject project)
    {
        string preview = "";
        if (IsImage(project.PreviewPath) && File.Exists(project.PreviewPath))
        {
            preview = owned + ".preview.png";
            if (!File.Exists(preview))
            {
                string source = project.PreviewPath;
                await Task.Run(() => File.Copy(source, preview, true));
            }
        }
        await _gate.WaitAsync();
        try
        {
            Dictionary<string, MediaLabel> labels = ReadLabels();
            labels[owned] = new MediaLabel { Title = project.Title, Preview = preview };
            Directory.CreateDirectory(_directory);
            string temporary = LabelsPath() + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(labels);
            try
            {
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    await stream.WriteAsync(bytes);
                    stream.Flush(true);
                }
                File.Move(temporary, LabelsPath(), true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    private string LabelsPath() => Path.Combine(_directory, "media-labels.json");

    private Dictionary<string, MediaLabel> ReadLabels()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, MediaLabel>>(File.ReadAllText(LabelsPath())) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private sealed class MediaLabel
    {
        public string Title { get; set; } = "";
        public string Preview { get; set; } = "";
    }

    private static void CopyDirectory(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("网页壁纸项目包含目录链接，无法完整导入。");
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("网页壁纸项目包含文件链接，无法完整导入。");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (string child in Directory.EnumerateDirectories(source)) CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
    }
}
