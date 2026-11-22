using System.Diagnostics;
using System.IO.Compression;
using WorkTiles.Services;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
void Reject(Action action)
{
    bool rejected = false;
    try { action(); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "危险或残缺更新包未被拒绝");
}
string root = Path.Combine(Path.GetTempPath(), "WorkTiles-update-test-" + Guid.NewGuid().ToString("N"));
// 构造标准 PKZIP 多磁盘包，区别于 7-Zip 的 .zip.001 二进制切片。
static Dictionary<string, byte[]> CreateStandardSplitZip(byte[] zip)
{
    const int size = 65536;
    byte[] data = new byte[zip.Length + 4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data, 0x08074b50);
    zip.CopyTo(data, 4);
    int end = data.Length - 22;
    int central = BitConverter.ToInt32(data, end + 16) + 4;
    int count = BitConverter.ToUInt16(data, end + 10);
    void U16(int offset, int value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), (ushort)value);
    void U32(int offset, int value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), (uint)value);
    int cursor = central;
    for (int i = 0; i < count; i++)
    {
        int local = BitConverter.ToInt32(data, cursor + 42) + 4;
        U16(cursor + 34, local / size); U32(cursor + 42, local % size);
        cursor += 46 + BitConverter.ToUInt16(data, cursor + 28) + BitConverter.ToUInt16(data, cursor + 30) + BitConverter.ToUInt16(data, cursor + 32);
    }
    U16(end + 4, end / size); U16(end + 6, central / size); U32(end + 16, central % size);
    Dictionary<string, byte[]> parts = [];
    for (int offset = 0, disk = 1; offset < data.Length; offset += size, disk++)
        parts["WorkTiles-standard." + (offset + size >= data.Length ? "zip" : "z" + disk.ToString("00"))] = data[offset..Math.Min(offset + size, data.Length)];
    return parts;
}
Directory.CreateDirectory(root);
string? linkedPath = null;
try
{
    string target = Path.Combine(root, "程序 ' & 空格");
    Directory.CreateDirectory(Path.Combine(target, "data"));
    File.WriteAllText(Path.Combine(target, "data", "projects.json"), "user-projects");
    File.WriteAllText(Path.Combine(target, "WorkTiles.dll"), "old-version");
    File.WriteAllText(Path.Combine(target, "z-locked.dll"), "old-locked");
    string Package(string name, params (string Name, string Content)[] extra)
    {
        string path = Path.Combine(root, name + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in new[] { ("WorkTiles.exe", "exe"), ("WorkTiles.dll", "new-version"), ("WorkTiles.pri", "resources"), ("WorkTiles.runtimeconfig.json", "{}"), ("coreclr.dll", "runtime") }.Concat(extra))
        {
            using StreamWriter writer = new(zip.CreateEntry(file.Item1).Open()); writer.Write(file.Item2);
        }
        return path;
    }
    int Apply(string configuration)
    {
        ProcessStartInfo start = new("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "ApplyUpdate.ps1"), "-Configuration", configuration, "-SkipRestart" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        // GitHub Windows runner 首次启动 Windows PowerShell 与杀毒扫描会明显慢于本机，
        // 同时异步排空输出，避免重定向管道写满后进程和测试互相等待。
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new Exception("更新脚本在 60 秒内未结束"); }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0 && !File.Exists(Path.Combine(Path.GetDirectoryName(configuration)!, "update.log")))
            throw new Exception("更新程序启动失败：" + error.Result);
        return process.ExitCode;
    }
    string valid = Package("valid", ("Assets/new.txt", "asset"));
    Check(!ReleaseUpdateService.IsNewer(new Version(2, 0, 3), new Version(2, 0, 3, 0)), "相同发布与程序集版本不能误报更新");
    Check(ReleaseUpdateService.IsNewer(new Version(2, 0, 4), new Version(2, 0, 3, 0)), "较新发布未识别");
    var githubRelease = new ReleaseSnapshot(new Version(2, 0, 4), "v2.0.4", null, null);
    Check(ReleaseUpdateService.RecommendGitHub(new(new Version(2, 0, 3), "v2.0.3", null, null), githubRelease), "Gitee 版本落后时必须建议切换 GitHub");
    Check(!ReleaseUpdateService.RecommendGitHub(githubRelease, githubRelease), "同版本不应误报 Gitee 延迟");
    Check(ReleaseUpdateService.RecommendGitHub(null, githubRelease), "Gitee 无发布时应提示已有 GitHub 版本");
    GitHubReleaseUpdate Parse(params string[] names)
    {
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            tag_name = "v2.0.4", assets = names.Select(name => new { name, browser_download_url = "https://example.test/" + name })
        }));
        return ReleaseUpdateService.ParseRelease(json.RootElement)!.Update!;
    }
    // 更新日志：Release 的 body 原样进入快照与更新对象；没有 body 字段时保持为空。
    using var notesJson = System.Text.Json.JsonDocument.Parse(
        "{\"tag_name\":\"v2.0.4\",\"body\":\"## 更新内容\\n- 新增启动参数\",\"assets\":[]}");
    var withNotes = ReleaseUpdateService.ParseRelease(notesJson.RootElement);
    Check(withNotes!.Notes == "## 更新内容\n- 新增启动参数", "Release 正文应解析为更新日志");
    Check(withNotes.Update is null && withNotes.Notes == "## 更新内容\n- 新增启动参数",
        "没有可安装资产时更新日志仍随快照返回");
    var splitUpdate = Parse("WorkTiles-win-x64.zip.003", "WorkTiles-win-x64.zip.001", "WorkTiles-win-x64.zip.002", "v2.0.4.zip");
    Check(splitUpdate.Parts.Count == 3 && splitUpdate.Parts[0].Name.EndsWith("001"), "分卷应按数字排序且排除源码包");
    Check(splitUpdate.Notes is null, "Release 没有 body 字段时更新日志为空");
    // 已发布的旧 Release 仍以 Pancake 命名，必须能发现；无产品名的源码包仍要排除。
    var pancakeUpdate = Parse("Pancake-win-x64-2.1.2.zip");
    Check(pancakeUpdate.AssetName == "Pancake-win-x64-2.1.2.zip", "历史 Pancake 命名资产应被识别为可安装更新");
    using var sourceOnlyJson = System.Text.Json.JsonDocument.Parse(
        "{\"tag_name\":\"v2.0.4\",\"assets\":[{\"name\":\"v2.0.4.zip\",\"browser_download_url\":\"https://example.test/v2.0.4.zip\"}]}");
    Check(ReleaseUpdateService.ParseRelease(sourceOnlyJson.RootElement)!.Update is null, "无产品名的源码包不应被当成安装包");
    Reject(() => Parse("WorkTiles-win-x64.zip.001", "WorkTiles-win-x64.zip.003"));
    Check(Parse("WorkTiles-win-x64.z02", "WorkTiles-win-x64.zip", "WorkTiles-win-x64.z01").Parts.Count == 3, "标准 ZIP 分卷未识别");
    byte[] zipBytes = File.ReadAllBytes(valid);
    var chunks = new Dictionary<string, byte[]>();
    for (int i = 0; i < 3; i++) chunks[splitUpdate.Parts[i].Name] = zipBytes[(zipBytes.Length * i / 3)..(zipBytes.Length * (i + 1) / 3)];
    var downloader = new ReleaseUpdateService(new AssetHandler(chunks));
    string joined = await downloader.DownloadAsync(splitUpdate, root);
    bool rejectedMissingDigest = false;
    try { await downloader.DownloadAsync(splitUpdate, root, default, requireDigest: true); }
    catch (InvalidDataException) { rejectedMissingDigest = true; }
    Check(rejectedMissingDigest, "要求校验摘要时，缺少 sha256 的更新必须被拒绝");
    Check(File.ReadAllBytes(joined).SequenceEqual(zipBytes), "下载合并的 ZIP 与原包不一致");
    string splitConfig = PortableUpdateService.Prepare(joined, target, root, int.MaxValue);
    Check(Apply(splitConfig) == 0 && File.ReadAllText(Path.Combine(target, "data", "projects.json")) == "user-projects", "分卷覆盖应保留配置");
    File.WriteAllText(Path.Combine(target, "WorkTiles.dll"), "old-version");
    string sevenSource = Path.Combine(root, "seven-source");
    ZipFile.ExtractToDirectory(valid, sevenSource);
    string seven = Path.Combine(root, "WorkTiles-win-x64.7z");
    string sevenTool = Path.Combine(AppContext.BaseDirectory, "Tools", "7zip", "x64", "7za.exe");
    await SplitUpdatePackage.RunAsync(sevenTool, ["a", "-t7z", seven, Path.Combine(sevenSource, "*")], default);
    string normalized = await SplitUpdatePackage.NormalizeAsync(seven);
    Check(Apply(PortableUpdateService.Prepare(normalized, target, root, int.MaxValue)) == 0, "7z 转换与覆盖失败");
    File.WriteAllText(Path.Combine(target, "WorkTiles.dll"), "old-version");
    byte[] sevenBytes = File.ReadAllBytes(seven);
    var sevenParts = Parse("WorkTiles-win-x64.7z.002", "WorkTiles-win-x64.7z.001");
    var sevenDownloads = new ReleaseUpdateService(new AssetHandler(new()
    {
        ["WorkTiles-win-x64.7z.001"] = sevenBytes[..(sevenBytes.Length / 2)],
        ["WorkTiles-win-x64.7z.002"] = sevenBytes[(sevenBytes.Length / 2)..]
    }));
    string sevenJoined = await sevenDownloads.DownloadAsync(sevenParts, root);
    Check(Apply(PortableUpdateService.Prepare(await SplitUpdatePackage.NormalizeAsync(sevenJoined), target, root, int.MaxValue)) == 0, "7z 分卷下载合并与覆盖失败");
    File.WriteAllText(Path.Combine(target, "WorkTiles.dll"), "old-version");
    string unsafeSource = Path.Combine(root, "unsafe-source"); Directory.CreateDirectory(Path.Combine(unsafeSource, "data"));
    File.WriteAllText(Path.Combine(unsafeSource, "data", "projects.json"), "forbidden");
    string unsafeSeven = Path.Combine(root, "unsafe.7z");
    await SplitUpdatePackage.RunAsync(sevenTool, ["a", "-t7z", unsafeSeven, Path.Combine(unsafeSource, "*")], default);
    Reject(() => SplitUpdatePackage.NormalizeAsync(unsafeSeven).GetAwaiter().GetResult());
    var standardParts = CreateStandardSplitZip(File.ReadAllBytes(Package("large", ("Assets/random.txt", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(100000))))));
    var standardUpdate = Parse(standardParts.Keys.ToArray());
    string standard = await new ReleaseUpdateService(new AssetHandler(standardParts)).DownloadAsync(standardUpdate, root);
    Check(Apply(PortableUpdateService.Prepare(await SplitUpdatePackage.NormalizeAsync(standard), target, root, int.MaxValue)) == 0, "标准 z01 ZIP 分卷下载解压与覆盖失败");
    File.WriteAllText(Path.Combine(target, "WorkTiles.dll"), "old-version");
    // 最后一卷缺失没有序号空洞，必须由解压校验拒绝。
    string truncated = Path.Combine(root, "truncated.zip"); File.WriteAllBytes(truncated, zipBytes[..(zipBytes.Length / 2)]);
    Reject(() => PortableUpdateService.Prepare(truncated, target, root, int.MaxValue));
    string config = PortableUpdateService.Prepare(valid, target, root, int.MaxValue);
    Check(File.ReadAllText(Path.Combine(target, "WorkTiles.dll")) == "old-version", "准备更新时不应覆盖正在运行的程序");
    string staging = Path.GetDirectoryName(config)!;
    HashSet<string> outsideBefore = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(file => !file.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !file.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    Check(Apply(config) == 0, "更新执行失败");
    HashSet<string> outsideAfter = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(file => !file.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !file.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    Check(outsideBefore.SetEquals(outsideAfter), "更新在安装目录和 staging 之外产生了新文件");
    Check(File.ReadAllText(Path.Combine(target, "WorkTiles.dll")) == "new-version", "旧文件未被覆盖");
    Check(File.ReadAllText(Path.Combine(target, "data", "projects.json")) == "user-projects", "更新改变了项目数据");
    Check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(config)!, "backup", "WorkTiles.dll")) == "old-version", "更新没有保留旧文件备份");
    string failure = PortableUpdateService.Prepare(Package("locked", ("z-locked.dll", "new-locked")), target, root, int.MaxValue);
    File.WriteAllText(Path.Combine(target, "WorkTiles.dll"), "before-failure");
    using (var locked = new FileStream(Path.Combine(target, "z-locked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(Apply(failure) != 0, "锁定文件应导致更新失败");
    Check(File.ReadAllText(Path.Combine(target, "WorkTiles.dll")) == "before-failure", "更新失败没有回滚之前覆盖的文件");
    // 回滚必须清掉本次新建的空目录，不能留下半套安装结构。
    File.WriteAllText(Path.Combine(target, "zz-locked.dll"), "old-locked");
    string newDirectoryFailure = PortableUpdateService.Prepare(Package("locked-newdir", ("newdir/sub/file.txt", "new"), ("zz-locked.dll", "new-locked")), target, root, int.MaxValue);
    using (var locked = new FileStream(Path.Combine(target, "zz-locked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(Apply(newDirectoryFailure) != 0, "锁定文件应导致更新失败");
    Check(!Directory.Exists(Path.Combine(target, "newdir", "sub")) && !Directory.Exists(Path.Combine(target, "newdir")),
        "回滚未按深度清理本次新建的空目录");
    // 目录联接必须被拒绝，更新不能穿过链接写到安装目录之外。
    string linkedReal = Path.Combine(root, "linked-real");
    Directory.CreateDirectory(linkedReal);
    string linked = Path.Combine(target, "linked");
    linkedPath = linked;
    ProcessStartInfo linkStart = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    linkStart.ArgumentList.Add("/c");
    linkStart.ArgumentList.Add("mklink");
    linkStart.ArgumentList.Add("/J");
    linkStart.ArgumentList.Add(linked);
    linkStart.ArgumentList.Add(linkedReal);
    using (var linkProcess = Process.Start(linkStart)!)
    {
        linkProcess.WaitForExit();
        Check(linkProcess.ExitCode == 0 && Directory.Exists(linked), "测试环境无法创建目录联接，链接拒绝测试未执行");
    }
    Reject(() => PortableUpdateService.Prepare(Package("via-link", ("linked/file.txt", "new")), target, root, int.MaxValue));
    Check(!File.Exists(Path.Combine(linkedReal, "file.txt")), "更新不能穿过目录联接写文件");
    ProcessStartInfo removeStart = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    removeStart.ArgumentList.Add("/c");
    removeStart.ArgumentList.Add("rmdir");
    removeStart.ArgumentList.Add(linked);
    using (var removeLink = Process.Start(removeStart)!) removeLink.WaitForExit();
    linkedPath = null;
    foreach (string bad in new[] { "../escape", "data/projects.json", "C:/escape", "Assets/../../escape", "Assets/test:stream", "Assets/CON", "Assets/a. ", "WorkTiles.DLL" })
        Reject(() => PortableUpdateService.Prepare(Package(Guid.NewGuid().ToString("N"), (bad, "bad")), target, root, int.MaxValue));
    string missing = Path.Combine(root, "missing.zip");
    using (ZipArchive zip = ZipFile.Open(missing, ZipArchiveMode.Create)) zip.CreateEntry("WorkTiles.exe");
    Reject(() => PortableUpdateService.Prepare(missing, target, root, int.MaxValue));
    Check(!File.Exists(Path.Combine(root, "escape")), "解压路径越界");
    Console.WriteLine($"PASS: {checks} update extraction, overwrite, data preservation, rollback and invalid-package checks.");
}

finally
{
    // 目录联接必须先移除链接本身，递归删除不会跟随它进入目标目录。
    if (linkedPath is not null && Directory.Exists(linkedPath))
    {
        try
        {
            ProcessStartInfo removeStart = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            removeStart.ArgumentList.Add("/c");
            removeStart.ArgumentList.Add("rmdir");
            removeStart.ArgumentList.Add(linkedPath);
            using var removeLink = Process.Start(removeStart);
            removeLink?.WaitForExit();
        }
        catch (Exception) { }
    }
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}

sealed class AssetHandler(Dictionary<string, byte[]> assets) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(assets[Path.GetFileName(request.RequestUri!.AbsolutePath)]) });
}
