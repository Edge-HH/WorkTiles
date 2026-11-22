using System.IO.Compression;
using System.Text.Json;
using WorkTiles.Services;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
void Reject(Action action, string message, params Type[] expected)
{
    bool rejected = false;
    try { action(); }
    catch (Exception ex) when (expected.Any(type => type.IsInstanceOfType(ex))) { rejected = true; }
    Check(rejected, message);
}
string root = Path.Combine(Path.GetTempPath(), "WorkTiles-project-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    ProjectStore store = new(Path.Combine(root, "local"));
    ProjectLibrary library = store.Load();
    Check(library.Projects.Count == 0, "新安装不应填入示例作业");
    ProjectDocument first = ProjectStore.Create(library, false);
    first.Subjects.Add(new SubjectState
    {
        Name = "语文", Width = 430, Height = 320, X = 48, Y = 96, InkCoordinateVersion = 1,
        AccentHex = "#123456", IsAccentExplicit = true,
        Entries = [new HomeworkState { Content = "第一段\n第二段", RtfContent = @"{\rtf1\ansi text}", Attachments = [], FontFallbacks = [new FontFallbackState { Start = 0, Length = 2, Family = "Unavailable Classroom Font" }] }],
        InkStrokes = [new InkStrokeState { Color = "#FF112233", Thickness = 5, Points = [new PointState { X = 2, Y = 3 }, new PointState { X = 200, Y = 150 }] }]
    });
    string picture = Path.Combine(root, "source.png"); File.WriteAllBytes(picture, [1, 2, 3, 4]);
    first.Subjects[0].Entries[0].Attachments.Add(new AttachmentState { Name = "图片", Kind = "图片", Path = store.CopyAttachment(first.Id, picture), Rotation = 90, Scale = 2, OffsetX = 7, PositionY = 35 });
    File.Delete(picture);
    // 仅时钟模式的整屏笔迹属于项目内容，和磁贴笔迹一起保存与分发。
    first.ClockInkStrokes = [new InkStrokeState { Color = "#FFE5B3B3", Thickness = 2, Points = [new PointState { X = 12, Y = 34 }, new PointState { X = 1200, Y = 800 }] }];
    store.Save(library);
    ProjectLibrary restored = store.Load();
    Check(restored.ActiveProjectId == first.Id && restored.Projects[0].Subjects[0].X == 48, "项目与布局未恢复");
    Check(File.Exists(restored.Projects[0].Subjects[0].Entries[0].Attachments[0].Path), "附件未独立收纳");
    string savedJson = File.ReadAllText(store.LibraryPath);
    Check(!savedJson.Contains(root.Replace("\\", "\\\\")) && !savedJson.Contains(root.Replace("\\", "/")) &&
        !savedJson.Contains("file:///"), "本地附件应使用相对路径");
    // 凭据落盘：设备令牌与 TOTP 密钥必须由 DPAPI 保护，换用户或换机器后解密失败即按需要重新配对处理。
    library.Settings.CloudSync.DeviceToken = "token-for-test";
    library.Settings.Lock.TotpSecret = "JBSWY3DPEHPK3PXP";
    store.Save(library);
    string credentialFile = File.ReadAllText(store.LibraryPath);
    Check(!credentialFile.Contains("token-for-test") && !credentialFile.Contains("JBSWY3DPEHPK3PXP"), "设备令牌与 TOTP 密钥不能明文落盘");
    ProjectLibrary credentialRoundTrip = store.Load();
    Check(credentialRoundTrip.Settings.CloudSync.DeviceToken == "token-for-test" &&
        credentialRoundTrip.Settings.Lock.TotpSecret == "JBSWY3DPEHPK3PXP", "凭据应能在本机解密还原");
    Check(restored.Projects[0].ClockInkStrokes.Count == 1 && restored.Projects[0].ClockInkStrokes[0].Points[1].X == 1200, "仅时钟模式的整屏笔迹未随项目文件往返");
    ProjectDocument reset = ProjectStore.Create(library, true);
    Check(reset.Name != first.Name && reset.Subjects.Count == 1, "同日项目名或模板错误");
    Check(reset.Subjects[0].Entries.Count == 0 && reset.Subjects[0].InkStrokes.Count == 0 && reset.Subjects[0].AccentHex == "#123456", "重置作业误删配色或未清内容");
    Check(reset.ClockInkStrokes.Count == 0, "重置作业未清空仅时钟模式的整屏笔迹");
    Check(first.Subjects[0].Entries.Count == 1, "新项目污染原项目");
    Check(ProjectStore.Create(library, false).Subjects.Count == 0, "清空布局不应保留磁贴");
    string package = Path.Combine(root, "test.wth");
    WthPackageService service = new(store); service.Save(first, package);
    ProjectStore secondStore = new(Path.Combine(root, "other"));
    ProjectDocument imported = new WthPackageService(secondStore).Import(package);
    Check(imported.Id != first.Id && imported.LinkedFile == package, "导入应产生本地 ID 并关联原文件");
    AttachmentState image = imported.Subjects[0].Entries[0].Attachments[0];
    Check(File.ReadAllBytes(image.Path).SequenceEqual(new byte[] { 1, 2, 3, 4 }) && image.Rotation == 90 && image.OffsetX == 7, "图片或变换未往返");
    Check(imported.Subjects[0].Entries[0].RtfContent == first.Subjects[0].Entries[0].RtfContent && imported.Subjects[0].InkStrokes[0].Points[1].Y == 150, "RTF 或笔迹未往返");
    Check(imported.ClockInkStrokes.Count == 1 && imported.ClockInkStrokes[0].Points[1].Y == 800 && imported.ClockInkStrokes[0].Color == "#FFE5B3B3", "仅时钟模式的整屏笔迹未随作业包往返");
    Check(imported.Subjects[0].Entries[0].FontFallbacks.Single().Family == "Unavailable Classroom Font", "缺失字体原始名称未随作业包往返");
    imported.Name = "重命名"; new WthPackageService(secondStore).Save(imported, package);
    Check(service.Import(package).Name == "重命名", "重复保存未更新包");
    // 兼容更名前的 .pch 作业包：格式相同，旧扩展名仍可导入。
    string legacyPackage = Path.Combine(root, "legacy.pch");
    File.Copy(package, legacyPackage);
    Check(new WthPackageService(secondStore).Import(legacyPackage).Name == "重命名", "旧 .pch 作业包无法导入");
    byte[] before = File.ReadAllBytes(package);
    File.Delete(image.Path);
    Reject(() => new WthPackageService(secondStore).Save(imported, package), "缺失附件应阻止保存", typeof(FileNotFoundException));
    Check(File.ReadAllBytes(package).SequenceEqual(before), "失败保存损坏原包");
    Reject(() => service.Save(first, store.DirectoryPath), "不可写目标应报告失败", typeof(IOException), typeof(UnauthorizedAccessException));
    Check(File.ReadAllBytes(package).SequenceEqual(before), "失败写入污染其他文件");
    foreach (string bad in new[] { "../escape", "assets/../../escape", "assets/.. ", "assets/x:stream", "assets/abc/def", "assets/CON", "assets/x\\..\\z" })
        Check(!WthPackageService.IsAssetPath(bad), "危险资源路径被接受：" + bad);
    string malicious = Path.Combine(root, "bad.wth");
    using (ZipArchive zip = ZipFile.Open(malicious, ZipArchiveMode.Create)) zip.CreateEntry("../escape");
    Reject(() => service.Import(malicious), "ZIP 越界路径被接受", typeof(InvalidDataException));
    string broken = Path.Combine(root, "broken.wth"); File.WriteAllText(broken, "broken");
    Reject(() => service.Import(broken), "损坏包被接受", typeof(JsonException), typeof(InvalidDataException));
    string incomplete = Path.Combine(root, "missing.wth");
    var noAssets = ProjectStore.Clone(first); noAssets.Subjects[0].Entries[0].Attachments[0].Path = "assets/" + new string('a', 32) + ".png";
    using (ZipArchive zip = ZipFile.Open(incomplete, ZipArchiveMode.Create))
    using (Stream s = zip.CreateEntry("project.json").Open()) JsonSerializer.Serialize(s, noAssets);
    Reject(() => service.Import(incomplete), "包内缺少附件仍然导入", typeof(InvalidDataException));
    noAssets.Version = 2;
    Reject(() => ProjectValidation.Validate(noAssets, true), "未知版本被接受", typeof(InvalidDataException));
    ProjectDocument badClockInk = ProjectStore.Clone(first);
    badClockInk.ClockInkStrokes[0].Thickness = 0;
    Reject(() => ProjectValidation.Validate(badClockInk, false), "无效的整屏笔迹被接受", typeof(InvalidDataException));
    ProjectDocument missingClockInk = ProjectStore.Clone(first);
    missingClockInk.ClockInkStrokes = null!;
    ProjectValidation.Validate(missingClockInk, false);
    ProjectDocument customPalette = ProjectStore.Clone(first);
    customPalette.Palette = "自定义1";
    customPalette.CustomPalettes = [new NamedPalette { Name = "自定义1", Colors = ["#111111", "#222222", "#333333", "#444444", "#555555", "#666666", "#777777", "#888888", "#999999"] }];
    customPalette.CustomPalette = [..customPalette.CustomPalettes[0].Colors];
    ProjectValidation.Validate(customPalette, false);
    customPalette.Palette = "不存在的色系";
    Reject(() => ProjectValidation.Validate(customPalette, false), "未登记的自定义色系被接受", typeof(InvalidDataException));
    (double cropX, double cropY) = ImageCropGeometry.RotateOffset(40, 10, 90);
    Check(cropX == -10 && cropY == 40, "顺时针旋转后裁切偏移应跟着画面转");
    (cropX, cropY) = ImageCropGeometry.RotateOffset(40, 10, -90);
    Check(cropX == 10 && cropY == -40, "逆时针旋转后裁切偏移应转到另一侧");
    Check(missingClockInk.ClockInkStrokes.Count == 0, "缺少整屏笔迹字段的旧项目应补成空列表");
    SubjectState oldInk = new() { Width = 430, Height = 326, InkStrokes = [new InkStrokeState { Thickness = 5, Points = [new PointState { X = 1000, Y = 600 }] }] };
    ProjectStore.MigrateInk(oldInk);
    Check(oldInk.InkStrokes[0].Points[0].X == 417 && oldInk.InkStrokes[0].Points[0].Y == 313 && oldInk.InkStrokes[0].TipScaleX == .4, "旧笔迹转换错误");
    ProjectStore.MigrateInk(oldInk);
    Check(oldInk.InkStrokes[0].Points[0].X == 417, "笔迹被重复迁移");
    Check(InkGeometry.HitTest([(0, 0), (100, 0)], 50, 3, 5) && !InkGeometry.HitTest([(0, 0), (100, 0)], 50, 30, 5), "橡皮擦线段距离错误");
    foreach (var size in new[] { (1920d, 1080d), (1080d, 1920d), (1600d, 1200d), (1200d, 1600d), (1080d, 1080d), (700d, 1400d) })
    {
        var layout = ExportLayout.Arrange([(430, 2000), (300, 500), (600, 360), (400, 750)], size.Item1, size.Item2, 100);
        Check(layout.All(p => ExportLayout.InBounds(p, size.Item1, size.Item2, 100)), "自动排版越界或遮住标题");
        Check(!layout.SelectMany((p, i) => layout.Skip(i + 1).Select(q => ExportLayout.Overlap(p, q))).Any(v => v), "自动排版重叠");
    }
    Check(ExportLayout.Arrange([], 1000, 1000, 100).Count == 0, "空导出错误");
    var defaults = JsonSerializer.Deserialize<BoardSettingsState>("{}")!;
    Check(defaults.AutoLayoutAlign && defaults.AutoLayoutResize && defaults.AutoLayoutGap == 0, "旧设置应启用自动对齐和内容裁切");
    foreach (bool align in new[] { false, true })
    foreach (double gap in new[] { 0d, 17d, 60d })
    {
        var layout = BoardLayout.Arrange([(300, 160), (280, 96), (420, 240), (300, 140)], 1100, 900, gap, align);
        Check(layout.Any(p => p.X == 0 && p.Y == 0), "自动布局应从左上角开始");
        Check(layout[0].Width == 300 && layout[0].Height == 160, "排版不能自行缩小磁贴");
        Check(!layout.SelectMany((p, i) => layout.Skip(i + 1).Select(q =>
            p.X < q.X + q.Width + gap - .001 && p.X + p.Width + gap > q.X + .001 &&
            p.Y < q.Y + q.Height + gap - .001 && p.Y + p.Height + gap > q.Y + .001)).Any(v => v), "所有磁贴必须保留最小间隔");
    }
    var alignedSizes = BoardLayout.AlignSizes([(300, 100), (310, 112), (500, 300)]);
    Check(alignedSizes[0] == (310, 112) && alignedSizes[1] == (310, 112) && alignedSizes[2] == (500, 300), "相近尺寸只向上对齐且不扩张无关尺寸");
    Reject(() => BoardLayout.Arrange([(500, 400)], 300, 200), "不能为自动排列裁掉内容", typeof(InvalidOperationException));
    var exportPacked = ExportLayout.Arrange([(300, 100), (300, 100)], 1000, 500, 80, 20);
    Check(exportPacked[0].X == 0 && exportPacked[0].Y == 80 && exportPacked[1].X == 320 && exportPacked[1].Scale == 1, "导出继承间隔并向左上排紧");
    var packed = BoardLayout.Arrange([(432, 336), (432, 336), (432, 336)], 1100, 780, 0, true, 48);
    Check(packed.All(p => p.X % 48 == 0 && p.Y % 48 == 0), "网格自动排列必须对齐位置");
    Check(packed.All(p => p.Width == 432 && p.Height == 336), "自动排列必须保留传入尺寸");
    Check(packed[1].X == packed[0].X + packed[0].Width && packed[2].Y == packed[0].Height, "网格排列不应留下无意义间隔");
    for (int count = 1; count <= 12; count++)
    {
        var sizes = Enumerable.Range(0, count).Select(i => (400d + i * 8, 180d + i * 6)).ToList();
        var compact = BoardLayout.Arrange(sizes, 1200, 1600);
        Check(compact.Select((p, i) => p.Width == sizes[i].Item1 && p.Height == sizes[i].Item2).All(v => v), "紧密排列必须保留磁贴尺寸");
        Check(compact.All(p => p.X >= 0 && p.Y >= 0 && p.X + p.Width <= 1200 && p.Y + p.Height <= 1600), "紧密排列不得越界");
        Check(!compact.SelectMany((p, i) => compact.Skip(i + 1).Select(q => p.X < q.X + q.Width && p.X + p.Width > q.X && p.Y < q.Y + q.Height && p.Y + p.Height > q.Y)).Any(v => v), "紧密排列不得重叠");
    }
    Reject(() => BoardLayout.Arrange([(400, 300)], 250, 90), "空间不足应明确报错", typeof(InvalidOperationException));
    var centered = BoardLayout.Snap(new(347, 249, 300, 200), [], 1000, 700);
    Check(centered.X == 350 && centered.Y == 250 && centered.GuideX == 500 && centered.GuideY == 350, "看板中心吸附错误");
    var adjacent = BoardLayout.Snap(new(397, 104, 300, 200), [new(100, 100, 300, 200)], 1200, 800);
    Check(adjacent.X == 400 && adjacent.Y == 100, "磁贴边缘吸附错误");
    var free = BoardLayout.Snap(new(77, 73, 300, 200), [], 1000, 700);
    Check(free.X == 77 && free.Y == 73 && free.GuideX is null && free.GuideY is null, "远离参考线时不应吸附");
    var exportSnap = BoardLayout.Snap(new(347, 299, 300, 200), [], 1000, 700, 100);
    Check(exportSnap.Y == 300 && exportSnap.GuideY == 400, "导出吸附应按标题以下的作业区域居中");
    LayoutRect[] keptTiles = [new(0, 0, 430, 320), new(900, 80, 430, 320)];
    foreach ((double Width, double Height) viewport in new[] { (1600d, 900d), (640d, 480d), (2d, 2d), (1600d, 900d) })
    {
        (double boardWidth, double boardHeight) = BoardLayout.FixedBoardSize(viewport.Width, viewport.Height, 1, keptTiles);
        Check(keptTiles[0] == new LayoutRect(0, 0, 430, 320) && keptTiles[1] == new LayoutRect(900, 80, 430, 320),
            "视口变化不能改写磁贴矩形");
        Check(boardWidth >= 1330 && boardHeight >= 400, $"画板必须装得下磁贴（{viewport.Width}x{viewport.Height} -> {boardWidth}x{boardHeight}）");
        Check(boardWidth >= viewport.Width && boardHeight >= viewport.Height, "画板不能小于视口");
    }
    Check(BoardLayout.FixedBoardSize(1600, 900, 1, keptTiles) == (1600, 900), "磁贴已经落在视口内时画板等于视口");
    Check(BoardLayout.FixedBoardSize(640, 480, 1, keptTiles) == (1330, 480), "视口变窄时画板仍保留磁贴右缘");
    Check(BoardLayout.FixedBoardSize(2, 2, 1, keptTiles) == (1330, 400), "短暂的极小视口不能把画板收成磁贴叠在一起的尺寸");
    (double zoomedOutWidth, double zoomedOutHeight) = BoardLayout.FixedBoardSize(1600, 900, 0.8, keptTiles);
    Check(Math.Abs(zoomedOutWidth - 2000) < .001 && Math.Abs(zoomedOutHeight - 1125) < .001,
        $"缩小后画板应按可见范围铺开（{zoomedOutWidth}x{zoomedOutHeight}）");
    Check(BoardLayout.FixedBoardSize(1600, 900, 1.4, keptTiles) == (1600, 900), "放大不把已经落在视口内的磁贴所在画板裁小");
    Check(BoardLayout.FixedBoardPanLimit(800, 800, 1.4) == 400, "放大到 140% 且内容未超出视口时，平移停在半个视口");
    Check(BoardLayout.FixedBoardPanLimit(800, 1760, 1) == 960, "磁贴超出视口时可以滚到内容尽头");
    ProjectStore emptyAttachmentStore = new(Path.Combine(root, "legacy-empty-attachment"));
    Directory.CreateDirectory(emptyAttachmentStore.DirectoryPath);
    AppState legacyWithEmptyAttachment = new()
    {
        Subjects =
        [
            new SubjectState
            {
                Name = "旧科目", Width = 430, Height = 320,
                Entries = [new HomeworkState { Attachments = [new AttachmentState { Name = "未完成图片", Kind = "图片", Path = "" }] }]
            }
        ]
    };
    File.WriteAllText(Path.Combine(emptyAttachmentStore.DirectoryPath, "pancake.json"), JsonSerializer.Serialize(legacyWithEmptyAttachment));
    ProjectLibrary migratedWithEmptyAttachment = emptyAttachmentStore.Load();
    Check(migratedWithEmptyAttachment.Projects.Single().Subjects.Single().Entries.Single().Attachments.Single().Path == "", "空附件占位不应阻止旧看板迁移");
    Check(emptyAttachmentStore.Load().Projects.Single().Subjects.Single().Entries.Single().Attachments.Single().Path == "", "空附件占位不应阻止新项目清单再次读取");
    ProjectStore legacyStore = new(Path.Combine(root, "legacy")); Directory.CreateDirectory(legacyStore.DirectoryPath);
    AppState legacy = new() { Subjects = [new SubjectState { Name = "旧科目", Width = 430, Height = 320 }] };
    File.WriteAllText(Path.Combine(legacyStore.DirectoryPath, "pancake.json"), JsonSerializer.Serialize(legacy));
    ProjectLibrary migrated = legacyStore.Load();
    Check(migrated.Projects.Count == 1 && migrated.Projects[0].Subjects[0].InkCoordinateVersion == 1, "旧看板迁移失败");
    Check(File.Exists(Path.Combine(legacyStore.DirectoryPath, "pancake.json.before-projects.bak")), "未保留旧备份");
    migrated.Projects.Clear(); migrated.ActiveProjectId = null; legacyStore.Save(migrated);
    Check(legacyStore.Load().Projects.Count == 0, "删除最后项目后重新迁入旧数据");

    // 桌面磁贴自动排序：拖拽落点判定与重排语义（复刻 UpdateDesktopInsertionLine + FinishDesktopDrag）。
    var deskWork = new DesktopWorkArea(0, 0, 1920, 1080);
    const double deskGap = 12;
    var deskSizes = new (double W, double H)[] { (400, 300), (400, 300), (400, 300), (400, 300) };
    int DeskGuide((double W, double H)[] sizes, int from, double x, double y, double w, double h)
    {
        var slots = sizes.Select(s => new DesktopTileSlot(0, 0, Math.Max(1, s.W), Math.Max(1, s.H))).ToList();
        return DesktopTileLayout.InsertionGuideForTile(slots, from, x, y, w, h, deskWork, deskGap)?.Index ?? -1;
    }
    List<int> DeskReorder(int count, int from, int insertAt)
    {
        var order = Enumerable.Range(0, count).ToList();
        if (insertAt >= 0 && insertAt != from)
        {
            order.RemoveAt(from);
            order.Insert(Math.Clamp(insertAt, 0, order.Count), from);
        }
        return order;
    }
    var deskArranged = DesktopTileLayout.Arrange(deskSizes, deskWork, deskGap);
    // 完整盖住目标槽：唯一命名下每组交换都应换位，且等大磁贴精确落到目标原槽。
    for (int from = 0; from < deskSizes.Length; from++)
    for (int target = 0; target < deskSizes.Length; target++)
    {
        if (from == target) continue;
        var t = deskArranged[target];
        int insertAt = DeskGuide(deskSizes, from, t.X, t.Y, t.Width, t.Height);
        Check(insertAt == target, $"完整盖住目标槽应返回目标下标（from={from} target={target} got={insertAt}）");
        var order = DeskReorder(deskSizes.Length, from, insertAt);
        Check(order.IndexOf(from) == target, $"重排后被拖磁贴应占住目标槽（from={from} target={target}）");
        var moved = DesktopTileLayout.Arrange(order.Select(i => deskSizes[i]).ToArray(), deskWork, deskGap)[order.IndexOf(from)];
        Check(Math.Abs(moved.X - t.X) < 1 && Math.Abs(moved.Y - t.Y) < 1, "等大磁贴交换后应精确落到目标原槽");
    }
    // 部分覆盖：窗口压住目标槽的比例超过原槽残留时换位，刚离开原槽则保持原序号。
    var own = deskArranged[0];
    Check(DeskGuide(deskSizes, 0, own.X, own.Y + 180, own.Width, own.Height) == 1, "盖住目标槽过半应换位");
    Check(DeskGuide(deskSizes, 0, own.X, own.Y + 20, own.Width, own.Height) == 0, "刚离开原槽不应换位");
    Check(DeskGuide(deskSizes, 0, own.X, own.Y, own.Width, own.Height) == 0, "停在原槽不应换位");
    // 光标引导兜底仍按最近槽判定。
    var cursorSlots = deskSizes.Select(s => new DesktopTileSlot(0, 0, s.W, s.H)).ToList();
    var center = deskArranged[2];
    var cursorGuide = DesktopTileLayout.InsertionGuide(cursorSlots, 0, center.X + center.Width / 2, center.Y + center.Height / 2, deskWork, deskGap);
    Check(cursorGuide is { Index: 2 }, "光标落在目标槽中心应返回该槽下标");

    // 阶段1：同名科目按稳定 Id 排序，拖动重排不再被名字合并抹平（复刻 OrderDesktopSubjects 的 MergeOrderIds）。
    Guid idA = Guid.NewGuid(), idB = Guid.NewGuid(), idC = Guid.NewGuid(), idD = Guid.NewGuid();
    var sameNameCurrent = new (Guid Id, string Name)[] { (idA, "新科目"), (idB, "新科目"), (idC, "英语") };
    // 把第一块同名磁贴拖到末尾：保存的 Id 顺序应原样套回，名字相同也能区分。
    var draggedIds = new[] { idB, idC, idA };
    Check(DesktopTileLayout.MergeOrderIds(draggedIds, null, sameNameCurrent).SequenceEqual(draggedIds), "同名科目按 Id 重排后顺序应保留（拖动不再弹回）");
    // 旧的名字顺序迁移：同名按出现次数一一对应到不同 Id。
    var migratedIds = DesktopTileLayout.MergeOrderIds(null, new[] { "英语", "新科目", "新科目" }, sameNameCurrent);
    Check(migratedIds.SequenceEqual(new[] { idC, idA, idB }), "旧名字顺序应按出现次数迁移到稳定 Id");
    Check(migratedIds.Distinct().Count() == 3, "同名科目迁移后 Id 不能重复");
    // Id 顺序优先于过期名字；新科目接末尾，已删科目丢掉，重复保存项去重。
    var partialCurrent = new (Guid Id, string Name)[] { (idA, "甲"), (idC, "丙"), (idD, "丁") };
    Check(DesktopTileLayout.MergeOrderIds(new[] { idA, idC, idB, idA }, new[] { "丙", "甲" }, partialCurrent).SequenceEqual(new[] { idA, idC, idD }), "Id 优先、去重、丢弃已删并把新科目接末尾");
    // 没有任何保存顺序时按当前顺序原样返回。
    Check(DesktopTileLayout.MergeOrderIds(null, null, sameNameCurrent).SequenceEqual(new[] { idA, idB, idC }), "无保存顺序时应保持当前顺序");

    Console.WriteLine($"PASS: {checks} project/package/migration/ink/layout checks.");
}
finally
{
    string tempRoot = Path.GetFullPath(Path.GetTempPath());
    if (Path.GetFullPath(root).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}
