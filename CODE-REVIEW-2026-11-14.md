# WorkWidgets 代码审查报告（2026-11-14）

- 审查对象：`D:\Files\Codes\Projects\Pancake\Pancake`，分支 `main`，HEAD `c6d1df4`，产品版本 `2.1.1`（`src/WorkWidgets/WorkWidgets.csproj`）
- 技术栈：WinUI 3 / Windows App SDK 1.8（`Microsoft.WindowsAppSDK.WinUI 1.8.260803003`）、.NET 8（`net8.0-windows10.0.19041.0`）、x64、Win2D、NAudio、QRCoder
- 审查性质：**只读审查**。除本报告外未修改仓库任何文件，未执行任何 git 写操作，未安装或升级任何依赖。
- 对照基线：仓库根目录旧报告 `CODE-REVIEW.md`（针对更名前 2.0.6）。本报告所有结论均以当前代码为准，旧报告仅作线索，逐项复核结果见第八节。

---

## 一、审查范围与方法

### 1.1 已通读的活跃代码

| 区域 | 范围 |
| --- | --- |
| `src/WorkWidgets.Shared` | `Contracts.cs`、`SettingsSchema.cs`、`RichText/RtfCodec.cs` |
| `src/WorkWidgets/Services` | 全部 42 个服务文件 |
| `src/WorkWidgets/Models`、`ViewModels` | `BoardModels.cs`、`InkToolSettings.cs`、`MainViewModel.cs` |
| `src/WorkWidgets` 根 | `App.xaml.cs`、`MainWindow.xaml.cs` 与 17 个 partial、`MainWindow.xaml`、`Themes/ThemeResources.xaml`、`ApplyUpdate.ps1` |
| `src/WorkWidgets/Controls` | 全部 22 个控件文件 |
| `tests` | 4 套无头逻辑测试 + 5 个 UI 验证源文件 + 8 个 `verify-*.ps1`（脚本仅静态审查） |
| `installer`、CI | `installer/Optimize-Publish.ps1`、`.github/workflows/release.yml`、`WorkWidgets.slnx`、2 个产品 `.csproj` 与 4 个测试 `.csproj` |

活跃源码约 25,200 行（含测试）。

### 1.2 判定为非活跃、未深入审查的目录

- `src/Pancake.Core`、`src/Pancake.Platforms.Abstractions`、`src/Pancake.Platforms.Shared`、`src/platforms`：**空目录**，无任何 git 跟踪文件，属更名/分层重构留下的骨架。
- `legacy/Pancake.WinUI`：只剩 `bin/`、`obj/` 构建残留，无源码，不参与 `WorkWidgets.slnx`。
- `artifacts/`、`src/WorkWidgets.Shared/artifacts`：被 `.gitignore` 忽略的构建/验证产物目录，非源码。
- `tests/RichTextLogic`、`tests/ServerLogic`、`tests/UiHeadless`：**空目录**，连 `bin/`、`obj/` 都没有，无 git 跟踪文件。名字暗示曾计划做富文本、服务端与无头 UI 测试，实际从未落地（见 P0-4）。

建议：空目录与 `legacy` 残留应清理或补 README 说明用途，避免后续审查与新人误判（见 P3）。

### 1.3 已执行的验证

| 项目 | 命令 | 结果 |
| --- | --- | --- |
| 构建 | `dotnet build WorkWidgets.slnx -c Release` | **0 警告 0 错误** |
| 项目逻辑 | `dotnet run --project tests/ProjectLogic` | 121 条断言全部通过 |
| 设置逻辑 | `dotnet run --project tests/SettingsLogic` | 9 组全部通过 |
| 更新逻辑 | `dotnet run --project tests/UpdateLogic` | 35 条断言全部通过 |
| 同步逻辑 | `dotnet run --project tests/SyncLogic -p:WorkWidgetsServerDir=<CI 快照>` | 311 条断言全部通过 |

`SyncLogic` 默认指向 `../WorkWidgetsServer`，本机不存在；本地 `../PancakeServer` 的 `main` 也不含 CI 固定的服务端提交。为复现 CI 行为，用 `git archive 244a1350b6ca32a16df989d7b3a82fd28264cafd`（只读导出，未改动任何工作树）取快照到临时目录后运行，结果与 CI 一致。

### 1.4 明确未执行的验证（按要求改为静态审查）

以下脚本会启动真实窗口、需要交互或依赖发布产物，**本次一律未运行**，仅阅读源码与脚本逻辑：

- `tests/verify-ui.ps1`、`verify-interaction-contract.ps1`、`verify-requested-ui-contract.ps1`、`verify-layout-persistence.ps1`、`verify-rich-text-roundtrip.ps1`、`verify-color-formatting-contract.ps1`、`verify-icon-font-contract.ps1`、`verify-release-startup.ps1`（其中 `verify-release-startup.ps1` 确实在 CI 中执行，见 `release.yml:88-92`；本次未运行）
- 条件编译（`EnableUiVerification=true` → `WORKWIDGETS_UI_TESTS`）下的 `tests/UiVerification.cs`、`GridCoverageVerification.cs`、`ImageDragVerification.cs`、`MediaUiVerification.cs`、`MediaPerformanceVerification.cs`
- `installer/Optimize-Publish.ps1`、`src/WorkWidgets/ApplyUpdate.ps1`（会改动发布目录/替换程序文件）

对 4 个纯源码正则断言脚本（`verify-interaction-contract.ps1`、`verify-requested-ui-contract.ps1`、`verify-color-formatting-contract.ps1`、`verify-rich-text-roundtrip.ps1`），本次改为**逐条 grep 核实其断言在当前源码上是否仍然成立**，不启动进程；结论见 P0-4。

因此：**UI 行为类结论均为静态推断，已在条目中标注"需人工确认"；构建与纯逻辑结论为实测。**

---

## 二、结论摘要

**总体判断：产品功能完成度高、局部实现相当扎实，但"质量保障体系"与"资源生命周期管理"两条线明显落后于功能开发速度。当前状态可以发布，但不具备安全地持续迭代的基础。**

本次通读了全部活跃源码（约 25,200 行，含测试）、构建与 CI 配置，实测构建与 4 套无头逻辑测试全部通过（0 警告 0 错误；121 + 9 组 + 35 + 311 条断言）。共记录 **79 条问题：5 条 P0、23 条 P1、36 条 P2、15 条 P3**。

三个最重要的结论：

**1. 存在一条会造成用户数据不可逆丢失的路径（P0-3）。**
备份还原时按扩展名过滤，跳过所有 `.json` 条目；而网页壁纸的清单文件恰好叫 `project.json`。用户备份再还原后，网页壁纸必然损坏且无法恢复。现有备份往返测试只覆盖作业、设置、图片与背景媒体，没有网页壁纸用例，所以测试全绿也发现不了。

**2. "验证通过"这个信号大面积不可信（P0-5，本次最重要的新发现）。**
旧报告只指出"CI 不跑测试"。本次进一步核实：即使跑了，结论也不成立——
- `verify-interaction-contract.ps1` 断言的 `ManipulationModes.TranslateX` 在当前源码里**零匹配**，该脚本**现在必然 exit 1**；因为不在 CI，这个红灯已存在了不知多少个提交。
- 4 个 `verify-*-contract.ps1` 全部是对源码做正则文本断言，**不执行任何被测代码**；其中甚至断言示例诗句和完整代码表达式。
- `UiVerification.cs` 的字体审计**全程无断言**，无条件写 `FONT_AUDIT_OK`；输入法诊断丢前台时整块断言被跳过，照样写 OK。
- `MediaPerformanceVerification.cs` 在 `finally` 里 `Environment.Exit(0)`，**失败也返回 0**；其核心的"帧拷贝面按显示尺寸封顶"断言在集合为空或 `SurfaceWidth=0` 时永不触发；差分断言在基线 <0.01ms/帧时被兜底成恒等 1。
- 3 处 `check(true, …)` 被计入 PASS 总数，虚增通过量。

配合 P0-4（`main` 与 PR **完全没有 CI**，只在打标签时跑一次同步测试），意味着团队目前既没有 continuous integration，也没有可信的 verification。

**3. 内存与 UI 线程开销随使用时长累积（P1-17、P1-2、P1-23、P1-19、P1-21）。**
最严重的是 P1-17：磁贴控件订阅了模型的 `CollectionChanged`，全库没有任何一处解绑，`BuildTiles` 只 `Children.Clear()` 而不 `Detach`。切换项目、进出编辑模式、云同步、加附件都会重建看板，每次都把整棵旧视觉树连同事件闭包永久留在内存里。这是一个会随使用时长单调增长的泄漏。

**值得肯定的实现**（本次复核确认，不是客套）：
- `tests/SyncLogic` 是**真实端到端测试**：起真实服务端、两台模拟大屏 + 网页管理员，覆盖三方合并、冲突副本、编辑占用、按键级布局合并、定向同步、考试校时，311 条断言全部有效，且正确使用了 `WaitAsync` 超时与 `RunContinuationsAsynchronously`。这是仓库里质量最高的代码之一。
- `ProjectStore.AtomicWrite`（随机 tmp + `Flush(true)` + `File.Move`）、`LockService`（PBKDF2-SHA256 100k + `FixedTimeEquals` + RFC6238 TOTP）、`SettingsCopy.Into`（反射原地复制，保持 UI 闭包与磁贴引用有效）都是正确且考虑周到的实现。
- `WebWallpaperView` 的加固很到位：DevTools/HostObjects 关闭、权限一律 Deny、新窗口与下载拦截、SHA-256 虚拟主机 + DenyCors、顶层导航 HTTPS 白名单（缺口见 P2-32）。
- `VideoFrameImage` 的单帧闸门（`Interlocked.Exchange`）、入队失败复位、拷贝面按显示像素封顶并 16 对齐；`AutofillPopup` 的主题字典递归取色；`AutofillInputText.ApplyGuard` 对抗中文输入法回写拼音——这些都是解决过真实难题的代码。
- `verify-icon-font-contract.ps1` 对照**真实字体二进制**的 `CharacterToGlyphMap` 校验图标映射，是唯一一条真正有效的契约测试，应作为其余脚本的改造范本。

**建议的最小行动**：先做第九节"第一批"的 7 项（预计 0.5–1 个工作日，改动都在 1–10 行量级），即可消除数据丢失与进程崩溃风险、恢复门禁信号；随后把 CI 扩展到 `main`/PR 并跑齐 4 套逻辑测试。**在验证断言修好之前，不要启动性能优化专项**——否则无法判断优化是否生效。

---

## 三、严重程度统计

| 级别 | 定义 | 条数 | 编号范围 |
| --- | --- | --- | --- |
| **P0** | 崩溃、数据丢失或质量门禁失效 | **5** | P0-1 ～ P0-5 |
| **P1** | 明确缺陷或显著风险 | **23** | P1-1 ～ P1-23 |
| **P2** | 设计债务与健壮性隐患 | **36** | P2-1 ～ P2-36 |
| **P3** | 整洁度与微优化 | **15** | P3-1 ～ P3-15（P3-15 内含 11 项沿用旧结论） |
| 合计 | | **79** | |

按主题分布（每条问题按主要归属计一次，不重复统计）：

| 主题 | P0 | P1 | P2 | P3 | 小计 |
| --- | --- | --- | --- | --- | --- |
| 测试与质量门禁 | 2 | 0 | 2 | 5 | 9 |
| 数据持久化 / 备份 / 迁移 | 1 | 4 | 3 | 0 | 8 |
| 内存与资源生命周期 | 0 | 4 | 6 | 0 | 10 |
| UI 线程性能与渲染 | 0 | 6 | 8 | 2 | 16 |
| 异常处理与崩溃路径 | 2 | 3 | 1 | 0 | 6 |
| 网络 / 云同步 / 更新 | 0 | 3 | 6 | 1 | 10 |
| 安全与输入校验 | 0 | 2 | 5 | 1 | 8 |
| 结构 / 重复 / 死代码 / 命名 | 0 | 0 | 4 | 5 | 9 |
| 交互缺陷 | 0 | 1 | 0 | 0 | 1 |
| 主题与视觉一致性 | 0 | 0 | 1 | 1 | 2 |
| **合计** | **5** | **23** | **36** | **15** | **79** |

问题最集中的三个方向：**UI 线程性能与渲染（16 条）**、**内存与资源生命周期（10 条）**、**网络/云同步/更新（10 条）**。而**测试与质量门禁虽然只有 9 条，却包含 2 条 P0**，且是其余所有问题得以长期累积的原因。

按核实程度：

| 类别 | 条数 | 说明 |
| --- | --- | --- |
| 已核实 | 74 | 读代码确认路径存在；构建与逻辑测试结论为实测 |
| 已核实缺陷，但可达性/手感需人工确认 | 2 | P1-7（畸形颜色是否有两道防线挡住）、P1-22（选区折叠的实际手感） |
| 疑似 / 需人工确认 | 3 | P2-16（HTTP 是否为有意的内网取舍）、P2-20（同名媒体丢弃，已从 P0 降级）、P2-36（性能阈值的机器基线） |

与旧报告对照：**已修复 4 项、部分修复 3 项、仍然存在 34 项**（旧报告 41 项全部复核完毕，详见第八节）。

---

## 四、P0：崩溃、数据丢失或质量门禁失效

### P0-1 全局异常处理只写日志，UI 线程异常仍直接终止进程

- 位置：[src/WorkWidgets/App.xaml.cs:13](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/App.xaml.cs#L13)
- 状态：**已核实**（旧报告 P0-1 仅部分修复）

```csharp
UnhandledException += (_, args) => WriteCrashLog(new Exception(args.Message, args.Exception));
AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
```

- 问题：`Microsoft.UI.Xaml.Application.UnhandledException` 事件参数没有设置 `args.Handled = true`。WinUI 的默认行为是：未标记已处理 → 异常继续上抛 → 进程终止。相比 2.0.6 新增了 `data/crash.log`（进步），但**"不崩溃"这一半根本没实现**。
- 触发条件与影响：任何 UI 线程未捕获异常都会让大屏当场退出。教室场景下这意味着上课中途看板消失，且需要人工重启。本报告中 P0-2、P1-5、P1-6、P1-7 都是能真实走到这里的路径，因此该项不是理论风险。
- 修复建议：
  1. 在处理器中 `args.Handled = true`，并把异常写入日志后向用户显示一条非阻断提示（`InfoBar`），而不是静默吞掉。
  2. 对"数据可能已损坏"的异常（`InvalidDataException`、`JsonException`、`IOException`）单独分类：停止写入并提示恢复，避免带着半份状态继续运行。
  3. `crash.log` 需要轮转与上限（见 P2-16），否则长期运行会无限增长。
  4. 补一条逻辑测试或 UI 验证：故意抛异常后进程仍存活且日志有记录。

### P0-2 `ClearInk_Click` 是 async void，既无异常保护也无对话框互斥

- 位置：[src/WorkWidgets/MainWindow.Projects.cs:376](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Projects.cs#L376)（关键行 387）
- 状态：**已核实**（旧报告 P0-2 原样存在）

```csharp
private async void ClearInk_Click(object sender, RoutedEventArgs e)
{
    ...
    ContentDialogResult result = await dialog.ShowAsync();   // 387：无 try/catch，无 _dialogOpen 守卫
```

- 问题：仓库其它对话框入口（`ShowConfirmAsync`/`ShowMessageAsync`/`UnlockAsync`/`BindTotpAsync`）都统一用 `_dialogOpen` 做互斥并包 try/finally，唯独这里没有。WinUI 同一 `XamlRoot` 只允许一个 `ContentDialog`，重复 `ShowAsync` 抛 `InvalidOperationException`；`RootShell.XamlRoot` 在窗口关闭过程中为 null 也会抛。async void 中的异常无法被调用方捕获，直接落到 P0-1 → 进程退出。
- 触发条件与影响：清空笔迹对话框与其它对话框生命周期重叠（例如自动备份失败提示、集控错误提示、锁定验证弹窗正在显示时点击工具栏"清空笔迹"），或用户在关窗瞬间点击。结果是**崩溃**，且此时笔迹尚未落盘，可能连带丢失本轮编辑。
- 修复建议：把整段包进 `try/catch`，并复用 `_dialogOpen` 守卫；更彻底的做法是抽一个 `ShowDialogAsync(Func<ContentDialog>)` 统一入口，让所有对话框共用互斥与异常处理，消灭这类"漏网"入口（同类问题见 P1-6）。

### P0-3 备份还原跳过所有 `.json` 条目，网页壁纸还原后必然损坏

- 位置：[src/WorkWidgets/Services/BackupService.cs:164](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/BackupService.cs#L164)
- 状态：**已核实**（数据/功能丢失）

```csharp
if (name.EndsWith('/') || name is ManifestEntry or JobsEntry or SettingsEntry
    || name.EndsWith(".json") || name.StartsWith("projects/")) continue;
```

- 问题：打包时网页壁纸是**整个项目目录**收进 `web/<目录>/…`（`CreateDirectoryEntry`，第 289-298 行），其中包含 Wallpaper Engine 的 `project.json`。还原时这条 `name.EndsWith(".json")` 把它一并跳过。而运行期加载网页壁纸必须读 `project.json`：
  - [src/WorkWidgets/Services/WebWallpaperPackage.cs:17](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/WebWallpaperPackage.cs#L17) 读不到就抛 `InvalidDataException("网页壁纸项目已损坏，请从 Wallpaper Engine 重新导入。")`
  - [src/WorkWidgets/Services/WallpaperEngineLibrary.cs:69](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/WallpaperEngineLibrary.cs#L69) 同样依赖该文件
- 触发条件与影响：任何含网页壁纸的 `.wwb` 备份，还原后网页壁纸 100% 不可用；用户看到的是"壁纸损坏，请重新导入"，而原壁纸可能早已从 Wallpaper Engine 取消订阅，等于**不可恢复的数据丢失**。备份的价值恰恰在灾难恢复，这条缺陷直接击穿该承诺。
- 修复建议：跳过条件改为**只跳过根级三个清单与 `backgrounds/` 下的两个历史索引**，不要按扩展名一刀切：

```csharp
bool isManifest = name is ManifestEntry or JobsEntry or SettingsEntry
    or "backgrounds/recent-images.json" or "backgrounds/recent-media.json";
if (name.EndsWith('/') || isManifest || name.StartsWith("projects/")) continue;
```

  同时给 `BackupService` 补一条无头测试：打包一个含 `project.json` 的假网页壁纸目录 → 还原 → 断言文件存在且 `WebWallpaperPackage.Load` 成功。这类往返测试当前完全缺失（见 P0-4）。

### P0-4 CI 只在打标签时触发，且逻辑门禁只覆盖同步；三套测试与解决方案入口都在门禁之外

- 位置：[.github/workflows/release.yml:3-12](file:///D:/Files/Codes/Projects/Pancake/Pancake/.github/workflows/release.yml#L3)、[:56-60](file:///D:/Files/Codes/Projects/Pancake/Pancake/.github/workflows/release.yml#L56)、[WorkWidgets.slnx](file:///D:/Files/Codes/Projects/Pancake/Pancake/WorkWidgets.slnx)
- 状态：**已核实**（旧报告 P0-3 部分修复）

- 问题：
  1. **触发时机**：`on:` 只有 `push.tags: v*.*.*` 与 `workflow_dispatch`（`release.yml:3-12`）。`main` 分支的日常提交与 Pull Request **没有任何 CI**。也就是说这不是持续集成，而是"发版时才检查一次"，问题只能在准备发布时暴露。
  2. **逻辑覆盖**：`release.yml:56-60` 只跑 `tests/SyncLogic`（固定服务端提交 `244a1350…`），这是相对 2.0.6 的实质进步；但 `tests/ProjectLogic`（121 断言）、`tests/SettingsLogic`（9 组）、`tests/UpdateLogic`（35 断言）**仍不在 CI**。这三套恰好覆盖本报告 P0-3、P1-8、P1-9、P1-10、P1-11 所在的持久化/更新/设置路径。
  3. **解决方案入口**：`WorkWidgets.slnx` 只含 `WorkWidgets.csproj` 与 `WorkWidgets.Shared.csproj`，**没有任何测试项目**。IDE 里"生成解决方案"不会构建测试，测试项目也不会随解决方案一起被重构工具改名/移动，长期必然腐化（旧报告 P1-9 原样存在）。
  4. **UI 契约**：8 个 `verify-*.ps1` 中只有 `verify-release-startup.ps1` 在 CI（`release.yml:88-92`），其余 7 个与 5 个 UI 验证源文件全靠人工本地执行；`EnableUiVerification` 在发布时显式关闭（`release.yml:71`），所以发布产物里不含任何自检能力。
  5. **覆盖空洞**：`tests/RichTextLogic`、`tests/ServerLogic`、`tests/UiHeadless` 是空目录（见 1.2）。5 个 UI 验证源文件对 `CloudSyncService`、`BackupService`、`ExamSchedule`、`LockService`、`ReleaseUpdateService` **零引用**；`UiVerification.cs:1457-1468` 对考试/集控/数据/锁定四个设置页只断言"页面可见 + 标题正确 + 被选中"，不触碰任何业务行为。
  6. **P0-3 正好落在空洞里**：`SettingsLogic/Program.cs:503-530` 有完整的备份打包/还原往返测试，但只断言作业内容、软件设置、图片附件与背景媒体，**没有网页壁纸目录**，因此 `.json` 被跳过这件事测试全绿也发现不了。
- 触发条件与影响：任何一次改动只要不触碰同步逻辑，就能在"CI 全绿"的情况下发布出破坏备份、更新或设置校验的版本；而由于 `main` 无 CI，破坏甚至可以在仓库里存在数个提交才被发现。
- 修复建议：
  1. 新增 `pull_request` / `push.branches: [main]` 触发的 workflow，跑 `dotnet build` + 4 套逻辑测试；`release.yml` 复用同一组步骤，任一失败即阻断发布。
  2. 把 4 个测试项目加入 `WorkWidgets.slnx`；测试用的 `<Compile Include>` 链接方式可保留，但要有解决方案级入口。
  3. 给 `BackupService` 补网页壁纸往返断言（见 P0-3），给 `ReleaseUpdateService` 补分卷号溢出与畸形颜色表断言（见 P1-8、P1-9）。
  4. 清理三个空测试目录，或补 README 说明计划；避免"看起来有测试"的错觉。
  5. 服务端提交固定值集中到一处（变量/文件），并在 README 说明如何更新，避免 CI 与本地长期不一致（本次审查就必须手工 `git archive` 才能跑通 `SyncLogic`）。

### P0-5 验证体系大面积"假绿"：契约脚本当前必红、UI 验证真失败仍写成功标记

- 位置：[tests/verify-interaction-contract.ps1:98-99](file:///D:/Files/Codes/Projects/Pancake/Pancake/tests/verify-interaction-contract.ps1#L98)、[tests/UiVerification.cs:73](file:///D:/Files/Codes/Projects/Pancake/Pancake/tests/UiVerification.cs#L73)、[tests/MediaPerformanceVerification.cs:158](file:///D:/Files/Codes/Projects/Pancake/Pancake/tests/MediaPerformanceVerification.cs#L158)
- 状态：**已核实**（逐条 grep 比对当前源码 + 阅读断言逻辑；未运行脚本）

这是本次审查最严重的系统性问题：**仓库里"测试通过"的信号大面积不可信**。分四类：

**(1) 契约脚本断言的是源码文本，不是行为；其中一个当前必然失败**

`verify-interaction-contract.ps1`、`verify-requested-ui-contract.ps1`、`verify-color-formatting-contract.ps1`、`verify-rich-text-roundtrip.ps1` 全部是 `[System.IO.File]::ReadAllText(...)` + `-match` 正则，**不启动程序、不执行任何被测代码**。它们只能证明"某段字符串还在源码里"，不能证明功能正确。

其中 `verify-interaction-contract.ps1:98-99` 要求：

```powershell
if ($attachmentImageCode -notmatch 'ManipulationModes\.TranslateX' -or
    $attachmentImageCode -notmatch 'Microsoft\.UI\.Input\.PointerDeviceType\.Mouse') {
    $failures.Add('磁贴图片缺少中央拖拽移动交互。')
}
```

而 [Controls/AttachmentImageControl.cs:69](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/AttachmentImageControl.cs#L69) 实际用的是 `ManipulationMode = ManipulationModes.None` + 指针捕获，全 `src` 对 `ManipulationModes.TranslateX` 与 `PointerDeviceType.Mouse` **零匹配**。→ **该脚本当前必然 `exit 1`**。因为它不在 CI（P0-4），这个红灯已经存在了不知道多少个提交。

值得注意：**功能本身没坏**——`tests/ImageDragVerification.cs` 的拖动与八点缩放断言对当前实现仍然成立。坏的是断言方式：实现手段换了，文本断言就失效，而真实行为从未被这个脚本验证过。

同类脆弱断言在 `verify-requested-ui-contract.ps1` 里比比皆是（本次逐条核实**当前仍全部通过**）：断言示例诗句 `当有一天你不再纠结于答案 当我们又重逢于天涯或沧海`（:110）、断言完整代码表达式 `smallestTop > largestTop && ReferenceEquals(EditIsland, RichTextIsland)) return false`（:155）。改一个变量名、换一句文案，测试就红；而真正的行为回归它一条也抓不到。

**(2) UI 验证在真失败时仍然写成功标记**

- `UiVerification.cs:45-79` `ScheduleFontAuditVerification`：遍历系统字体做 RTF 往返，把被改写的家族收进 `broken`，**全程没有任何断言**，:73 无条件 `evidence.Add("FONT_AUDIT_OK")`。`verify-ui.ps1:48` 把这个标记当成功。即使 100 个字体全部往返失败，结果仍是 OK。
- 输入法诊断丢前台时 `UiVerification.cs:604-608` 直接 `return false`，导致 :652/:682/:695/:734 整块断言被跳过、:748 的条件包住 :753，而 :762 照样写 OK；:747 的 `!named || IsOpen` 是恒真兜底。**环境不满足 → 断言静默消失 → 报成功**。
- `MediaPerformanceVerification.cs:154-158`：`finally` 里 `Environment.Exit(0)`，**无论成功失败退出码都是 0**。`verify-ui.ps1:54` 的 `ExitCode` 判据对这个模式完全无效，只剩 :60 的末行标记；同时跳过 `IDisposable` 释放（注释自认媒体管线析构会挂起）。

**(3) 恒真断言虚增通过量**

`UiVerification.cs:1367`、`:1385`、`GridCoverageVerification.cs:132` 都是 `check(true, $"...")`——只打印测量值，永远通过，却被 `verify-ui.ps1:61` 计入 PASS 总数。读日志的人会以为这些几何量被验证过了。

**(4) 空集合 / 零值让性能断言永不触发**

- `MediaPerformanceVerification.cs:107-117`：`foreach (VideoFrameImage frame in FindVisuals<VideoFrameImage>(...))`，集合为空时循环体不执行，:117 无条件 `PASS: frame copy surfaces are capped`。而 `SurfaceWidth`/`SurfaceHeight` 来自 [Controls/VideoFrameImage.cs:22-23](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/VideoFrameImage.cs#L22) 的 `_frame?.… ?? 0`，未出帧时为 0 → `surfacePixels = 0` → :113 的超标判断永不成立。**"拷贝面按显示尺寸封顶"这条核心性能保证，在最常见的失败模式下恰好不被检查。**
- `MediaPerformanceVerification.cs:171-176` `RatioAgainst`：`baseline.WorkPerFrame < .01` 时直接返回 `1`，于是 :129 的 `ratio > 2.5` 永不失败。基线测量偏快（小预览完全可能）就等于关掉了差分断言。
- :79-95 / :139-149 未断言 `hidden.Count > 0`，隐藏集合为空时 `hiddenGained = 0`、`busyHidden.Count = 0` 恒过——"隐藏管线停止工作"在根本没有隐藏管线时也报通过。

**其他已核实的验证缺陷**

- `UiVerification.cs:2135-2141` `NextLayoutAsync`：`DispatcherQueue.TryEnqueue` 返回值未检查，`TaskCompletionSource` 未设 `RunContinuationsAsynchronously`，`await ready.Task` **无超时**。入队失败即挂死到 `verify-ui.ps1:39` 的 180s 上限；:2140 还固定 `Task.Delay(150)`，被调用 179 次≈27s 纯等待。
- `tests/ImageDragVerification.cs` 整个文件只能由 `--verify-image-drag` 触发（`App.xaml.cs:87`、`WorkWidgets.csproj:48`），`verify-ui.ps1` 没有对应开关、README 也无记载 → **从不执行**。
- 大量"验证"其实是白盒钩子而非真实交互：`UiVerification.cs:221/233/240` 调用 `Tag` 里的 `Action`，:1972/:1998/:2019 直接 `ChooseHighlighted()`，:1265 `VerificationDrawStroke`，:306 `VerificationSetCanvas`。这些绕过了真实键鼠/笔输入路径，也就绕过了 P1-21、P1-22 这类只在真实输入下暴露的问题。
- 导出 PNG 只渲染不校验内容（`UiVerification.cs:298/307/1156`）；橡皮只验工具互斥不验是否真擦掉（:1342-1345）；`MediaUiVerification.cs:45-56` 毛玻璃探针 `Task.Delay(45000)` 后零断言。
- `UiVerification.cs:106` 用 `StackTrace.Contains(nameof(PersistentMicaBackdrop))` 做字符串匹配——方法被内联或改名即静默恒过，与 ps1 的文本断言同病。

- 触发条件与影响：团队据"验证通过"做发版决策，而上述路径在真实回归发生时依然报绿；反过来，`verify-interaction-contract.ps1` 长期报红却无人处理，会让红灯彻底失去警示意义（"狼来了"）。
- 修复建议（按性价比排序）：
  1. **立刻**把 `verify-interaction-contract.ps1:98-99` 改成对行为的断言，或改为匹配当前实现（`ManipulationModes.None` + 指针捕获），并把它纳入 CI，让红灯重新有意义。
  2. `MediaPerformanceVerification.cs:158` 改为按 `evidence` 是否含 FAILED 决定 `Environment.Exit(1/0)`；`ScheduleFontAuditVerification` 要么加断言（`broken.Count == 0`），要么改名为 `FontAuditDiagnostic` 并从成功判据里移除。
  3. 所有 `check(true, ...)` 改为 `Log(...)`，不计入 PASS 数；所有遍历集合的断言前加 `if (items.Count == 0) throw`；`RatioAgainst` 的 `< .01` 兜底改为抛"测量无效"。
  4. `NextLayoutAsync` 检查 `TryEnqueue` 返回值并加 `WaitAsync(TimeSpan.FromSeconds(5))`。
  5. 中长期：把 4 个源码正则脚本替换为真实行为测试（无头逻辑测试已证明这条路可行——`SyncLogic` 用真实服务端跑 311 条断言），正则脚本只保留 `verify-icon-font-contract.ps1` 这类**对照真实字体二进制**的有效契约。

---

## 五、P1：明确缺陷或显著风险

### P1-1 备份、还原、自动备份全部在 UI 线程同步压缩/解压

- 位置：[src/WorkWidgets/MainWindow.Data.cs:77](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Data.cs#L77)、[:101](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Data.cs#L101)、[:330](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Data.cs#L330)
- 状态：**已核实**

```csharp
new BackupService(_dataStore.DataDirectory).Create(file.Path, scope, _library);   // 77，async Click 内同步
service.Restore(file.Path);                                                       // 101，同步
new BackupService(...).Create(Path.Combine(directory, ...), auto.Scopes.Sanitize(), _library);  // 330
```

- 问题：`BackupService.Create` 会把全部背景媒体、附件、网页壁纸目录逐个 `CreateEntryFromFile` 压缩（上限 4 GB），`Restore` 会逐个 `ExtractToFile`。这些调用都在 UI 线程，`async` 方法里也没有 `await Task.Run(...)`。第 330 行的自动备份由 30 分钟一次的 `_autoBackupTimer.Tick` 与启动流程触发，**用户完全无法预期**。
- 触发条件与影响：媒体库上百 MB 时，界面冻结数秒到数十秒；4 GB 上限下可达分钟级。表现为大屏"卡死"、时钟停走、噪音监测停摆、触摸无响应； Windows 可能标记"未响应"。自动备份命中上课时间时尤其糟糕。
- 修复建议：`Create`/`Restore`/`PruneAutoBackups` 全部放 `Task.Run`，UI 侧显示进度与"正在备份"状态并禁用重复触发；自动备份改为空闲时执行（参考已有的 `ToolbarAutoHidePolicy` 空闲判定），并在考试模式期间推迟。还原期间必须阻断编辑与保存，避免半份状态。

### P1-2 点阵网格为每个交点创建一个 `Ellipse`，4K 下数万个 XAML 元素

- 位置：[src/WorkWidgets/MainWindow.xaml.cs:826](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.xaml.cs#L826)（`DrawGrid`，826-864 行）
- 状态：**已核实**

- 问题：`GridStyle=Dots` 时按 `GridSize` 步长遍历画布，每个交点 `new Ellipse()` 加进 `Canvas`。`GridSize` 最小 16（[MainWindow.xaml.cs:25](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.xaml.cs#L25)），1920×1080 约 8,000 个元素，3840×2160 约 32,000 个。第 742 行的缓存键按 `/4` 取整，滚动或缩放即失效重建。
- 触发条件与影响：小网格 + 大画布 + 点阵样式时，每次视图变化都要销毁/重建上万个 `FrameworkElement`，触发完整 measure/arrange，产生明显卡顿与 GC 压力；这也是设置页里拖动网格滑块必须走"只改数据不重画"特殊路径（`GridSizeChanged` 注释）的根因。
- 修复建议：点阵改用单个 `Canvas` + Win2D `CanvasDrawingSession` 绘制，或用 `Rectangle` + `ImageBrush`/`Viewport2DVisual3D` 平铺；最简方案是一个覆盖全画布的 `Path`/`GeometryGroup`（一次绘制所有点）。网格线样式同理可合并为单个 `Path`。缓存键应按实际绘制窗口精确计算，避免 `/4` 取整带来的无谓重建。

### P1-3 导出排版最多做 100 次 O(n³) 回退尝试

- 位置：[src/WorkWidgets/Services/ExportLayout.cs:22](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ExportLayout.cs#L22)、[src/WorkWidgets/Services/BoardLayout.cs](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/BoardLayout.cs)
- 状态：**已核实**

```csharp
for (int percent = 100; percent >= 1; percent--)
{
    try { var layout = BoardLayout.Arrange(...); return ...; }
    catch (InvalidOperationException) { }
}
```

- 问题：`BoardLayout.Arrange` 的回退分支是 O(n³)（`placed.Select(...).Distinct().Order()` 双层枚举 + `placed.Any(...)` 线性查重），而 `ExportLayout` 用"缩小 1% 再试"的暴力搜索，最多调 100 次；每次还 `tiles.Select(...).ToList()` 重新分配。用异常做控制流本身也昂贵。
- 触发条件与影响：磁贴数量多、画布偏小或间隔偏大时，导出图片会长时间卡住 UI（导出路径同样在 UI 线程触发，见 `ExportImage_Click`）。n=30 时单次回退已是数万次操作，×100 次足以让界面冻结数十秒。
- 修复建议：
  1. 先按面积/最长边解析估算可行缩放比，再在该值附近做少量（≤5 次）验证，或直接二分。
  2. `BoardLayout.Arrange` 用 `HashSet`/有序结构替代 `Distinct().Order()` + `Any()`，把回退降到 O(n log n)。
  3. 提供 `TryArrange` 返回 bool，不要用异常表达"放不下"。
  4. 整个搜索放 `Task.Run`，UI 显示进度。

### P1-4 云端考试计划的瞬时 404/204 会清空本地考试缓存

- 位置：[src/WorkWidgets/Services/CloudSyncService.cs:665](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/CloudSyncService.cs#L665)（`FetchExamAsync`，665-679 行）
- 状态：**已核实**

```csharp
if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
{ ApplyExam(null, cloud.ClassId); return; }
```

- 问题：把 404 与 204 一律当成"服务器没有启用的考试计划"，随即 `ExamStore.Clear()`。但 404 也可能来自反向代理路径错误、服务端滚动发布、班级被临时解绑等**瞬时**状况；204 与 404 语义并不等价。
- 触发条件与影响：考试进行中一次网络抖动或网关 404，就会清掉本地考试安排，考试画面（倒计时、科目、考场）当场消失或退回本机安排。这是教学场景里最不能中断的画面。
- 修复建议：区分"明确的无计划"（204 或 200 + 空 body）与"资源不存在"（404）：404 应保留本地缓存并记录告警，连续 N 次（或超过一个宽限期）才清除；清除前若正处于考试时段，应延迟到本场考试结束。给 `ExamScheduleStore` 增加"最后成功同步时间"，UI 上显示"计划可能已过期"。

### P1-5 编辑锁续期与屏幕上报的计时器回调是 async void 且无异常保护

- 位置：[src/WorkWidgets/MainWindow.CloudEditLock.cs:24](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.CloudEditLock.cs#L24) 与 [:63](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.CloudEditLock.cs#L63)、[src/WorkWidgets/MainWindow.Cloud.cs:68](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Cloud.cs#L68) 与 [src/WorkWidgets/MainWindow.Layout.cs:26](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Layout.cs#L26)
- 状态：**已核实**

```csharp
_editLockHeartbeat.Tick += async (_, _) => await RenewEditLockAsync();   // RenewEditLockAsync 63-69 无 try/catch
_screenReportDebounce.Tick += async (_, _) => await ReportScreenAsync(); // ReportScreenAsync 26-30 无 try/catch
```

- 问题：`DispatcherTimer.Tick` 上的 `async` lambda 编译为 async void。`RenewEditLockAsync` 内的网络/序列化异常、`ReportScreenAsync` → `CurrentScreen()` 里的 `TransformToVisual`（元素不在同一可视树时抛 `ArgumentException`）都会直接逃逸到 UI 线程 → P0-1 → 崩溃。同文件里 `DispatcherQueue.TryEnqueue(async () => { try … finally … })`（CloudEditLock.cs:37-49）也只有 finally 没有 catch，同样漏异常。
- 触发条件与影响：切换布局/最小化/窗口关闭过程中触发屏幕上报，或集控续期请求返回非预期内容时崩溃。编辑锁续期失败还会让服务端 45 秒后释放占用，其它端抢走编辑权。
- 修复建议：所有计时器与 `TryEnqueue` 的 async 回调统一包 `try/catch`（catch 内写日志 + 状态提示，不抛）；抽一个 `SafeFireAndForget(Task, string context)` 辅助方法，禁止裸 `async (_, _) => await …`。`CurrentScreen()` 在元素未连接可视树时应返回 null 而不是抛。

### P1-6 天气城市选择：搜索回调是无保护的 async void，对话框也没有互斥

- 位置：[src/WorkWidgets/MainWindow.xaml.cs:1166](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.xaml.cs#L1166) 与 [:1176](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.xaml.cs#L1176)、[src/WorkWidgets/Services/WeatherCityCatalog.cs:26](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/WeatherCityCatalog.cs#L26)
- 状态：**已核实**

```csharp
searchBox.TextChanged += async (_, _) => await RefreshResultsAsync();   // 1166，无 try/catch
...
await dialog.ShowAsync();                                                // 1176，无 _dialogOpen 守卫
```

`WeatherCityCatalog.LoadAsync` 用 `File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "xiaomi_weather_cities.json"))`，文件缺失/被占用/JSON 损坏都会抛，且 `_cities ??= await LoadAsync()` 失败后不缓存，每次输入都重试再抛。

- 触发条件与影响：发布产物漏掉该资源（`.csproj` 里它是单独一条 `Content`，与 `Assets\Settings\**` 等通配项并列，容易在打包脚本调整时被遗漏）、文件被杀毒软件锁定、或用户输入触发反序列化异常时，**每敲一个字就崩一次**。对话框无互斥则与 P0-2 同理。
- 修复建议：`RefreshResultsAsync` 包 try/catch，失败时在列表区显示"城市数据不可用"并禁用搜索；`WeatherCityCatalog` 加载失败应缓存失败态并给出可诊断信息；对话框统一走带 `_dialogOpen` 的入口。另外每次 `TextChanged` 都全量 `Where(...).Take(100)`，应加防抖（仓库已有多个防抖计时器可复用）。

### P1-7 `SafeColorHex` 只校验长度与 `#`，畸形颜色会在构建学科行时抛 `FormatException`

- 位置：[src/WorkWidgets/MainWindow.Settings.cs:842](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Settings.cs#L842) 与 [:353](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Settings.cs#L353)、[src/WorkWidgets/ViewModels/MainViewModel.cs:101](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/ViewModels/MainViewModel.cs#L101)
- 状态：**已核实代码缺陷；触发依赖数据来源，标注"需人工确认可达性"**

```csharp
private static string SafeColorHex(string value) =>
    value.Length is 7 or 9 && value[0] == '#' ? value : "#818CF8";        // 842-843：不校验十六进制字符
...
swatch.Background = item.RandomColor ? RandomSubjectColorBrush()
    : MainViewModel.BrushFromHex(SafeColorHex(ColorPalette.Resolve(SafeColorHex(item.Color))));  // 353：无 try/catch
```

`MainViewModel.BrushFromHex`（101-109 行）直接 `value[..2]` + `Convert.ToByte(..., 16)`，无任何校验；`value` 为 null 或长度不足还会抛 `NullReferenceException`/`ArgumentOutOfRangeException`。

- 对比：同文件 845-859 行的 `SafeColor` **有** `catch (FormatException/OverflowException)`，`Controls/BackgroundVisual.cs:261` 的 `SafeColor` 也有 try/catch，`GridAppearance.ParseColor` 同样有防御。也就是说团队知道这个风险，只是 353 行这条路径漏了。
- 触发条件与影响：`"#GGGGGG"`、`"#12345"`（长度 6 被拦）、`"#1234567"`（长度 8 被拦）中，**长度为 7/9 但含非十六进制字符**的值会通过 `SafeColorHex` 然后在 `BrushFromHex` 抛出。来源可能是：手工编辑 `projects.json`、旧版本遗留数据、第三方工具生成的 `.wwh`。
  - 已核实的防线：`ProjectValidation.Color`（[ProjectStore.cs:246](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ProjectStore.cs#L246)）会用 `Uri.IsHexDigit` 校验，`ProjectStore.Load` 第 65 行对每个项目调用它；`SettingsSchema.IsColor`（[SettingsSchema.cs:303](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets.Shared/SettingsSchema.cs#L303)）也校验十六进制，集控下发走 `SanitizeValue`。因此**正常读盘与云端下发路径是安全的**。
  - 仍存在的缺口：`Settings.cs:353` 的 `item` 来自自动补全/学科候选列表，其 `Color` 不一定经过上述两道校验（例如 `AutofillService` 内置条目、内存中尚未落盘的对象、`ColorPalette.Resolve` 的返回值）。一旦有一条漏网，构建学科行就崩，且发生在 UI 线程。
- 修复建议：把 `SafeColorHex` 改成与 `SettingsSchema.IsColor` 同一套判定（`value[1..].All(Uri.IsHexDigit)`），或直接复用 `SettingsSchema.IsColor`；`BrushFromHex` 内部改为 `TryParse` 语义并提供 fallback；353 行改用已有的 `SafeColor`。这样"校验"只有一处定义，不会再出现某个入口漏掉。

### P1-8 更新分卷号用 `int.Parse`，超长数字直接 `OverflowException`

- 位置：[src/WorkWidgets/Services/ReleaseUpdateService.cs:63](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ReleaseUpdateService.cs#L63) 与 [:71](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ReleaseUpdateService.cs#L71)
- 状态：**已核实**

- 问题：分卷匹配正则允许 `^\d{3,}$`（3 位及以上数字，无上限），随后 `int.Parse` 解析序号。GitHub/Gitee  release 资产名完全由远端控制，`WorkWidgets-2.1.1.999999999999.001` 这类名字会让 `int.Parse` 抛 `OverflowException`。
- 触发条件与影响：检查更新时崩溃或更新流程中断（取决于调用方 catch 范围；`CheckForUpdatesAsync` 有 try/catch，会表现为"检查更新失败"，但错误信息对用户无意义）。同类问题：`GetProperty("tag_name")`/`GetProperty("assets")` 在字段缺失时抛 `KeyNotFoundException` 而非给出可读错误。
- 修复建议：正则限制为 `\d{3,4}`，解析改 `int.TryParse` 并在失败时跳过该资产；JSON 字段一律 `TryGetProperty` + 明确错误消息。`PortableUpdateService` 里的 `checked` 溢出同理应转成 `InvalidDataException`（当前抛 `OverflowException`，与"包损坏"的语义不符）。

### P1-9 RTF 颜色表解析：`int.Parse` 无上限，且 >255 的分量会让十六进制错位

- 位置：[src/WorkWidgets/Services/ColorPalette.cs:37](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ColorPalette.cs#L37)、[src/WorkWidgets/Services/DefaultContentColors.cs:17](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/DefaultContentColors.cs#L17)
- 状态：**已核实**

- 问题：`\red(\d+)` 等匹配后直接 `int.Parse(match.Groups[1].Value)`：
  1. 位数不限 → `\red99999999999` 抛 `OverflowException`；
  2. 值 >255 时不钳制，`$"{r:X2}"` 会产出 3 位十六进制，后续 `mapped.Substring(1,2)/(3,2)/(5,2)` 按固定偏移切分 → **颜色通道错位**，得到完全错误的颜色。
- 触发条件与影响：粘贴来自 Word/其它编辑器的 RTF（`\red256` 并不罕见，某些生成器会写 0-65535 范围），轻则颜色错乱，重则解析异常导致粘贴失败/崩溃。`RtfCodec.Parse` 同样不钳制 `\fs`（字号），`\uN` 对 >65535 直接截断，嵌套组 `Stack` 无深度限制。
- 修复建议：统一用 `int.TryParse` + `Math.Clamp(value, 0, 255)`；`\fs` 钳制到合理字号范围；`\uN` 用 `ushort` 范围校验或按 RTF 规范处理负值；组嵌套设深度上限（如 64）。这些都在 `WorkWidgets.Shared`，服务端/网页共用，修一处三端受益，且非常适合补进 `SettingsLogic`/`ProjectLogic` 的纯逻辑测试。

### P1-10 备份还原没有事务与回滚，还会把旧 `SyncQueue` 一起复活

- 位置：[src/WorkWidgets/Services/BackupService.cs:150](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/BackupService.cs#L150)（`Restore`，150-207 行）
- 状态：**已核实**

- 问题：
  1. 先把包内媒体解压到本地资源目录（161-170 行），**再**读 `jobs.json` 并 `store.Save(library)`（192 行）。中途任何一步失败（缺图、JSON 损坏、磁盘满），本地已经写入了一部分媒体与新清单，没有还原前快照，也没有回滚。
  2. `jobs.json` 是 `ProjectLibrary` 的完整克隆，包含 `SyncQueue`（[ProjectStore.cs:35](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ProjectStore.cs#L35)）。还原后这些**历史待上传操作会重新进入同步队列**，可能把旧内容上传覆盖云端较新的修订。
  3. `store.Save(library)` 整体替换清单：备份里没有的项目会被删除，而 UI 只提示"还原备份"，没有说明这是全量替换。
  4. 第 179 行 `library.Settings = settings ?? store.Load().Settings;`：`store.Load()` 在本地数据已损坏时会抛，且带迁移副作用。
- 触发条件与影响：还原失败 → 数据半新半旧且无法回退；还原成功 → 云端被旧数据覆盖。两者都是数据丢失级别。
- 修复建议：还原前自动做一次当前数据的安全快照（可复用 `.wwb`），失败时回滚；`SyncQueue` 在还原时清空（或按修订号过滤）；媒体解压与清单写入放在同一事务里（先写临时目录，全部成功后再原子替换）；UI 明确告知"将替换全部项目"。

### P1-11 单个项目数据非法会让整个项目库不可读并停止写入

- 位置：[src/WorkWidgets/Services/ProjectStore.cs:63](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ProjectStore.cs#L63)、[src/WorkWidgets/MainWindow.Projects.cs:113](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Projects.cs#L113)、[src/WorkWidgets/MainWindow.xaml.cs:1365](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.xaml.cs#L1365)
- 状态：**已核实**

- 问题：`Load()` 对 `library.Projects` 逐个 `ProjectValidation.Validate`，**任一项目非法就整体抛异常**。`LoadPersistentState` 捕获后置 `_storageReady = false`，随后 `PersistProjects` 直接 `throw new IOException("原项目数据未能读取，已停止写入。请先恢复数据并重启软件。")`。
- 触发条件与影响：一个项目的某条笔迹坐标为 NaN、某个颜色非法、某个附件路径越界，就会让**全部项目**（包括完全健康的那些）都无法显示与保存。用户被迫手工编辑 JSON 或还原备份。停写是正确的保护，但"一颗老鼠屎坏一锅汤"的粒度太粗。
- 修复建议：按项目隔离失败：非法项目跳过并记录到"受损项目"列表，UI 提供"查看/导出原始 JSON/删除"入口，健康项目照常加载与保存；整库级错误（版本不符、清单 JSON 损坏、项目 Id 重复）才停写。同时把损坏项目的原始文件保留一份，不要覆盖。

### P1-12 更新执行仍走 `powershell.exe -ExecutionPolicy Bypass`

- 位置：[src/WorkWidgets/Services/PortableUpdateService.cs:53](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/PortableUpdateService.cs#L53)（参数在 55 行）
- 状态：**已核实**（旧报告 P1-10 原样存在）

- 问题：用 Windows PowerShell 5.1 + `-ExecutionPolicy Bypass` 执行 `ApplyUpdate.ps1`。脚本本身质量不错（等待父进程 60 秒、路径校验、逆序回滚、`UPDATE_OK/FAILED` 日志），但执行方式绕过了机器上的脚本策略，且依赖老旧的 PowerShell 引擎。
- 触发条件与影响：在启用了 AppLocker/WDAC/Constrained Language Mode 的教学机房里，`Bypass` 可能被策略拦截导致更新失败；安全审计上这是一个明确的红旗。脚本路径若被本地低权限用户篡改（`data` 目录可写），会以当前用户权限执行任意脚本。
- 修复建议：优先把替换/回滚逻辑改写成 C#（在独立的小工具进程里完成），彻底去掉脚本依赖；若必须保留脚本，则对 `ApplyUpdate.ps1` 做 Authenticode 签名并校验哈希，使用 `-ExecutionPolicy RemoteSigned` 或按机器策略执行，并把脚本放在只读的程序目录而非可写数据目录。

### P1-13 窗口 `Closed` 只停了部分计时器，保存与上报仍可能在关窗后触发

- 位置：[src/WorkWidgets/MainWindow.xaml.cs:150](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.xaml.cs#L150)（150-161 行）
- 状态：**已核实**

- 问题：`Closed` 里停了 `_clockTimer`、`_weatherTimer`、`_presentationTimer`、`_toolbarAnimation`、`_autoBackupTimer`；`MainWindow.Cloud.cs:74-80` 停了 8 个云计时器与 `_examRefreshDebounce` 并释放 `_cloudRealtime`；`CloudEditLock.cs:25` 停了续期表。但 **`_saveTimer`、`_fullScreenLabelTimer`、`_screenReportDebounce` 没有任何地方停止**。
- 触发条件与影响：关窗后防抖保存仍可能触发一次写盘（与 `RestartAfterRestore` 的 `_skipSaveOnClose` 竞争），屏幕上报仍可能访问已卸载的可视树（→ P1-5 的 `ArgumentException`）。`MainWindow.Cloud.cs:69` 在 `RootShell.Loaded` 内订阅 `root.Changed`、72 行 `RegisterPropertyChangedCallback` 也都不解绑，窗口多次创建时（`--smoke-test`、还原后重启）会累积。
- 修复建议：集中一个 `StopAllTimers()`，把所有 `DispatcherTimer` 登记进一个列表统一停止；`Closed` 里统一解绑 `ActualThemeChanged`/`Changed`/属性回调；`_saveTimer` 停止前若仍有脏数据，先同步落盘再停。

### P1-14 `MediaLibrary` 多实例共用固定 `.tmp` 文件名，且写历史不 `Flush`

- 位置：[src/WorkWidgets/Services/MediaLibrary.cs:75](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/MediaLibrary.cs#L75)（`Save`）、[:29](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/MediaLibrary.cs#L29)（实例级 `_gate`）、[:84](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/MediaLibrary.cs#L84)（`ReplaceHistory` 无锁）
- 状态：**已核实**

- 问题：
  1. `_gate` 是**实例级** `SemaphoreSlim`，但仓库里同时存在多个 `MediaLibrary` 实例：`MainWindow.Media.cs:12`（`MediaLibraryStore`）、`BackupService.cs:82/83/205`、`ExportImageView.cs:65`。它们写的是**同一批历史文件**。
  2. `Save` 用固定的 `path + ".tmp"`，两个实例并发导入 → 同一临时文件被互相覆盖 → `File.Move` 抛 `IOException` 或写入错乱历史。
  3. `File.WriteAllTextAsync` 后直接 `File.Move`，没有 `Flush(true)`；断电/崩溃可能留下 0 字节历史文件（对比 `ProjectStore.AtomicWrite` 用随机 tmp + `Flush(true)`，是正确范式）。
  4. `ReplaceHistory` 是同步方法且完全不走 `_gate`，还原备份时与后台导入并发会互相踩。
- 触发条件与影响：导出图片时选图 + 设置页导入背景同时进行，或还原备份时恰好有导入在跑 → 历史文件损坏/丢失，"最近使用"列表清空。
- 修复建议：把历史写入收敛到**单一实例**（依赖注入或静态共享），或把 `_gate` 改为按路径的全局锁；临时文件名用 `Guid`；写入后 `Flush(true)`；`ReplaceHistory` 也走同一把锁。直接复用 `ProjectStore.AtomicWrite` 最省事。

### P1-15 `ErrorText` 只捕 `JsonException`，非字符串 JSON 值会抛 `InvalidOperationException`

- 位置：[src/WorkWidgets/Services/CloudSyncService.cs:829](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/CloudSyncService.cs#L829)（829-842 行）
- 状态：**已核实**

- 问题：`detail.GetString()` / `error.GetString()` 在 JSON 值不是字符串（数字、对象、null 以外的类型）时抛 `InvalidOperationException`，而 catch 只覆盖 `JsonException`。异常最终被 `StepAsync` 的分类捕获兜住，表现为"同步步骤失败"，但**真实的服务器错误信息被吞掉**，用户与日志都看不到原因。
- 触发条件与影响：服务端返回 `{"error": {"code": 409}}` 这类结构化错误时，客户端只显示通用失败，排障困难；编辑锁冲突（409）尤其需要准确提示。
- 修复建议：改用 `TryGetProperty` + `ValueKind == JsonValueKind.String` 判定，非字符串时 `GetRawText()` 兜底；catch 补 `InvalidOperationException`。

### P1-16 每次设置改动都全量重建工具栏内容并刷新所有磁贴

- 位置：[src/WorkWidgets/MainWindow.Settings.cs:1364](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Settings.cs#L1364)（`ApplyExtendedSettings`，1364-1404 行）、[:1406](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Settings.cs#L1406)（`ApplyToolbarSettings`，1406-1492 行）
- 状态：**已核实**

- 问题：`SettingChanged()` → `ApplyExtendedSettings()` 会遍历全部 `_refreshSettingAvailability`、`ApplyDisplayLayout()`、对**每个磁贴**调 `ApplyAppearance`、`UpdateBoardBounds()`、`ApplyToolbarSettings()`、`RefreshAppearancePreviews()`。而 `ApplyToolbarSettings` 每次都 `content.Children.Clear()` 重新创建全部按钮内容。
- 触发条件与影响：拖动任意滑块（圆角、边框粗细、透明度、大小…）时，每个 `ValueChanged` 都触发这一整套。磁贴多时明显掉帧。团队已经为网格大小、自动排版间隔、标题字号、正文样式写了"只触碰必要部分"的专用路径（`GridSizeChanged`/`AutoLayoutGapChanged`/`TileTitleSizeChanged`/`TileBodyStyleChanged` 的注释说得很清楚），但工具栏外观类滑块没有同等待遇。
- 修复建议：把 `ApplyToolbarSettings` 拆成"结构重建"（位置/无字模式变化时）与"样式更新"（颜色/圆角/粗细/缩放，只改依赖属性）；滑块类改动走 `QueueSurfaceRefresh` 那样的低优先级合帧；`ApplyExtendedSettings` 接受一个"变更范围"参数，避免无关刷新。

### P1-17 磁贴控件被模型事件永久持有，每次重建看板都泄漏一整棵视觉树

- 位置：[src/WorkWidgets/Controls/SubjectTileControl.cs:214](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/SubjectTileControl.cs#L214)、[src/WorkWidgets/MainWindow.xaml.cs:456](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.xaml.cs#L456)（`BuildTiles`，456-467 行）
- 状态：**已核实**

- 问题：磁贴构造时订阅 `_subject.Entries.CollectionChanged += Entries_CollectionChanged`，但**全库没有任何一处 `CollectionChanged -=` 或 `PropertyChanged -=`**（已全量检索确认）。`SubjectBoard` 是长寿命模型对象，存活于 `_library.Projects[].Subjects`；委托实例持有旧磁贴，旧磁贴又持有全部 `RichEditBox`、`AttachmentImageControl`（含已解码的 `BitmapImage`）、`Polyline` 笔迹与 `_renderedStrokes` 字典。
- 触发条件与影响：`BuildTiles()` 会 `BoardCanvas.Children.Clear()` 后为每个科目新建磁贴。调用它的场景非常频繁——添加/删除图片（`AddAttachmentAsync`，xaml.cs:711）、切换编辑模式、切换项目、云端同步替换项目、主题切换（`ApplyActualTheme`）。每次调用都把上一代磁贴整棵树变成不可回收垃圾。一节课反复编辑几十次后，内存里会堆积几十份完整看板视觉树与图片解码缓冲，最终表现为内存持续上涨、GC 压力增大、切换项目越来越慢。这是本次审查中**唯一一处模型→视图的强引用泄漏**，也是最容易被长时间运行放大的问题（教室大屏通常整天不关）。
- 修复建议：给 `SubjectTileControl` 增加 `Unloaded` 处理（或实现 `IDisposable` 由 `BuildTiles` 在 `Children.Clear()` 前显式调用），解绑 `_subject.Entries.CollectionChanged`；同时检查 `FormattingToolbarChanged`、`InkActivated` 等对外事件的订阅方是否也需要对称释放。更稳妥的做法是把订阅集中在一个 `Detach()` 方法里，`BuildTiles` 先对旧磁贴逐个 `Detach()` 再清空。

### P1-18 导出预览的外观刷新没有异常保护，拖动背景色/色系可致 UI 线程崩溃

- 位置：[src/WorkWidgets/Controls/ExportTileVisual.cs:149](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/ExportTileVisual.cs#L149)（`SetAppearance`，149-192 行，关键行 156、162-163）、[src/WorkWidgets/Controls/ExportImageView.cs:240](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/ExportImageView.cs#L240)（`RefreshAppearance`，240-248 行）
- 状态：**已核实**（崩溃需异常数据触发，路径本身无保护是确定的）

- 问题：`SetAppearance` 内部有三处可抛异常且**没有任何 try/catch**：`MainViewModel.BrushFromHex(accent)`（156 行，颜色字符串非法时抛 `FormatException`）、`editor.Document.SetText(TextSetOptions.FormatRtf, …)`（162 行，RTF 非法时 RichEdit 抛 COM 异常）、`DefaultContentColors.AdaptRtf(...)`（163 行，见 P1-9：颜色表分量 `int.Parse` 无上限、>255 时十六进制错位）。首次调用发生在 `ExportImageView.InitializeAsync` 的 try 内（181 行有兜底），但**后续调用全部裸奔**：`RefreshAppearance` 被 `picker.ColorChanged`（102 行）、`_palette.SelectionChanged`（152 行）、`clearImage.Click`（120 行）、`SetDimensions`（223 行）直接触发，这些调用点都没有 try/catch。
- 触发条件与影响：打开导出预览后拖动"背景颜色"取色器或切换"色系"，只要项目里有一条作业的 RTF 颜色表异常（旧版本写入、手工编辑、云端下发），就会在 UI 线程抛出未捕获异常；叠加 P0-1（`UnhandledException` 未设 `Handled`）直接终止进程，用户丢失当前编辑。取色器拖动是连续事件，崩溃概率被放大。
- 修复建议：`RefreshAppearance` 整体包 try/catch，失败时写 `_status.Text` 并禁用导出按钮（与 `RefreshBackdropAsync` 的 271 行处理方式一致）；`SetAppearance` 内对单条作业的 `SetText`/`AdaptRtf` 单独兜底，失败时退回纯文本 `Content`，不要让一条坏作业毁掉整张导出。

### P1-19 导出画布最大 8192×8192，像素缓冲在 UI 线程同步分配并拷贝多份

- 位置：[src/WorkWidgets/Controls/ExportBackdrop.cs:13](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/ExportBackdrop.cs#L13)（`RenderAsync`）、[:39](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/ExportBackdrop.cs#L39)（`ToBitmap`，39-47 行）、[src/WorkWidgets/Controls/ExportImageView.cs:403](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/ExportImageView.cs#L403)（`RenderToFileAsync`，关键行 420、423、431）、[:40](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/ExportImageView.cs#L40)（宽高 `Maximum = 8192`）
- 状态：**已核实**

- 问题：导出尺寸上限是 8192×8192（约 6700 万像素）。这条路径上同时在 UI 线程存在的整幅像素缓冲有：`CanvasRenderTarget source`（GPU+可读回）、`ToBitmap` 里 `image.GetPixelBytes()` 的 `byte[]`（256 MB）+ `WriteableBitmap.PixelBuffer`（256 MB）、开启模糊时再来一份 `blurred` 的同样两份、`RenderToFileAsync` 里 `RenderTargetBitmap` + `(await bitmap.GetPixelsAsync()).ToArray()`（又一份 256 MB 托管数组，且 `ToArray()` 是二次拷贝）。这些数组都超过 LOH 阈值，`GetPixelBytes`/`buffer.Write`/`input.CopyTo` 全是同步调用。
- 触发条件与影响：用户在导出页把宽高调到接近上限（或选 1:1 比例后把宽度拉到 8192），预览刷新与导出会连续分配数百 MB 到 1 GB 级缓冲。教室一体机常见 8 GB 内存且同时跑着视频壁纸，极易触发 `OutOfMemoryException` 或长时间 UI 冻结；即使不崩溃，`RefreshBackdropAsync` 只有 100 ms 防抖，拖动取色器时会反复生成整幅模糊层。
- 修复建议：(1) 按设备可用内存与 `CanvasDevice` 能力限制最大画布尺寸，或把上限降到 4096×4096 并在超限时给出明确提示；(2) `ExportBackdrop.RenderAsync` 与 `ToBitmap` 整体放进 `Task.Run`，只把最终的 `WriteableBitmap` 交回 UI 线程（`WriteableBitmap` 需在同一线程创建，可改为在后台生成像素数组、UI 线程只做一次写入）；(3) 复用 `CanvasRenderTarget`/`WriteableBitmap` 而不是每次刷新新建；(4) `RenderToFileAsync` 避免 `.ToArray()` 二次拷贝，直接用 `IBuffer` 交给编码器。

### P1-20 WebView2 环境任务缓存了失败结果，一次初始化失败后所有网页壁纸永久不可用

- 位置：[src/WorkWidgets/Controls/WebWallpaperView.cs:13](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/WebWallpaperView.cs#L13)、[:32](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/WebWallpaperView.cs#L32)、[:72](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/WebWallpaperView.cs#L72)（`CreateEnvironmentAsync`，72-77 行）
- 状态：**已核实**

- 问题：`private static Task<CoreWebView2Environment>? _environment;` 用 `_environment ??= CreateEnvironmentAsync()` 缓存。`CreateEnvironmentAsync` 会 `Directory.CreateDirectory(AppContext.BaseDirectory/data/webview-cache)` 并调用 `CoreWebView2Environment.CreateWithOptionsAsync`。任何一次失败（WebView2 Runtime 未安装、目录不可写、被安全软件拦截、首次初始化时磁盘满）都会让**已 faulted 的 Task 被永久缓存**；此后每个 `WebWallpaperView` 实例 `await` 同一个失败 Task，立刻走 `Failed()`，直到进程重启都没有重试机会。
- 触发条件与影响：教师机上 WebView2 Runtime 缺失或被临时占用时，第一次导入网页壁纸失败；即使随后装好 Runtime，应用内所有网页壁纸仍然一直报"背景媒体无法播放"，用户只能重启应用，且错误提示完全没有指向真实原因。缓存目录放在 `AppContext.BaseDirectory` 下，若程序被安装到 `Program Files` 一类只读位置则**必然**失败。
- 修复建议：缓存成功结果而不是 Task 本身——`await` 失败时把 `_environment` 置回 `null` 以便下次重试（用 `try/catch` 或 `ContinueWith` 判定 `IsFaulted`）；缓存目录改到 `AppDataStore.DataDirectory`（与 `crash.log`、媒体库同一可写根）；`Failed()` 时把异常消息透出到背景错误提示，便于区分"Runtime 缺失"与"壁纸本身损坏"。

### P1-21 笔迹擦除与书写热路径：每个指针事件对每条笔迹分配列表并做全量命中测试

- 位置：[src/WorkWidgets/Controls/SubjectTileControl.cs:892](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/SubjectTileControl.cs#L892)（`EraseStrokeAt`，892-903 行）、[:858](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/SubjectTileControl.cs#L858)（`InkCanvas_PointerMoved`，858-878 行）、[src/WorkWidgets/Controls/ClockInkLayer.cs:155](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/ClockInkLayer.cs#L155)（`EraseStrokeAt`，155-165 行）、[:127](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/ClockInkLayer.cs#L127)（`Layer_PointerMoved`）
- 状态：**已核实**

- 问题：两处橡皮擦实现完全相同：
  `_renderedStrokes.LastOrDefault(pair => InkGeometry.HitTest(pair.Value.Points.Select(p => (p.X, p.Y)).ToList(), …))`。
  每次指针移动都要为**每一条**笔迹新建一个 `List<(double,double)>`（逐点装箱成值元组再拷贝），然后逐点做距离判定；`LastOrDefault` 还会遍历完整个字典而不能提前退出。书写侧同样没有距离/频率过滤：`PointerMoved` 每个事件都往 `InkStrokeData.Points` 和 `Polyline.Points` 各加一个点。
- 触发条件与影响：触控笔/触屏的指针事件可达 120 Hz 以上。一块写满板书的磁贴（50 条笔迹 × 200 点）在擦除时每秒要分配约 6000 个列表、拷贝 120 万个元组并做同等数量的距离计算，全部在 UI 线程；表现为橡皮擦明显迟滞、笔迹跟手性差，同时给 GC 造成持续压力。书写侧无过滤则让笔迹点数随时间线性膨胀，直接放大 `pancake.json`/`projects.json` 体积、云端同步 payload 与 `MeasureContentSize` 的遍历成本（该方法每次自动排列都要遍历全部笔迹点）。
- 修复建议：(1) 擦除命中测试改为先用笔迹包围盒粗筛，再对候选笔迹直接遍历 `Points`（`InkGeometry.HitTest` 增加接受 `IReadOnlyList<Point>` 的重载），避免 `Select().ToList()`；命中后立即 `break`。(2) 为 `InkStrokeData` 缓存包围盒，落笔时增量更新。(3) 书写侧加入最小距离阈值（例如 0.75 DIP）或按 `PointerPoint.Timestamp` 做时间节流，并考虑用 `GetIntermediatePoints` 合批。(4) 两处橡皮擦逻辑抽到 `InkGeometry` 共用，消除重复实现。

### P1-22 作业补全为定位浮层而改写 `Selection`，会折叠用户刚选中的文字

- 位置：[src/WorkWidgets/Controls/AutofillInputs.cs:556](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/AutofillInputs.cs#L556)（`CaretRect`）、触发链 [:397-400](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/AutofillInputs.cs#L397) → [:432](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/AutofillInputs.cs#L432) → [:457](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/AutofillInputs.cs#L457)
- 状态：**已核实**（代码路径确定；实际手感需人工确认）

```csharp
private Rect? CaretRect(int caret)
{
    try
    {
        _editor.Document.Selection.SetRange(caret, caret);   // ← 改的是控件的当前选区
        _editor.Document.Selection.GetRect(PointOptions.ClientCoordinates, out Rect rect, out _);
```

- 问题：`HomeworkAutofillInput` 订阅了 `editor.SelectionChanged`（:397），任何选区变化都会走 `Refresh()`；`Refresh()` 在 :457 把 `CaretRect(caret)` 传给浮层，而 `CaretRect` 为了取光标矩形**直接改写了活动选区**，把 `(start, end)` 折叠成 `(caret, caret)`。`caret` 取自 `CurrentInput()` 的 `Selection.EndPosition`（:545），即选区末端。
- 触发条件与影响：开启"作业补全"后，用户在作业正文里**选中一段文字**（准备用富文本工具栏改颜色/字号/加粗），只要选区末端所在的词能匹配到候选，选区就会在 `SelectionChanged` 回调里被立刻折叠掉。工具栏的 `FormatEffect.Toggle`、`ForegroundColor`、`CharacterFormat.Size` 全部作用于选区，选区没了就只影响插入点 → 表现为"选中文字后格式按钮不起作用/选区自己消失"。折叠又会再次触发 `SelectionChanged`，形成一次多余的重算。
  对照：学科补全走 `TextBox` 路径（:284）不传 `caretRect`，因此不受影响；问题只在作业正文的 `RichEditBox`。
- 修复建议：用**游离文本区间**取矩形，不要碰活动选区：

```csharp
ITextRange probe = _editor.Document.GetRange(caret, caret);
probe.GetRect(PointOptions.ClientCoordinates, out Rect rect, out _);
```

  `GetRange` 返回的区间与 `Document.Selection` 无关，`GetRect` 同样可用。另建议在 `Refresh()` 开头判断 `Selection.StartPosition != Selection.EndPosition` 时直接 `Hide()` 返回——有选区时本来就不该弹补全。

### P1-23 磁贴图片与背景板图片全尺寸解码，没有 `DecodePixelWidth` 上限

- 位置：[src/WorkWidgets/Controls/AttachmentImageControl.cs:156-157](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/AttachmentImageControl.cs#L156)、[src/WorkWidgets/Controls/BackgroundVisual.cs:202](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/BackgroundVisual.cs#L202)
- 状态：**已核实**

```csharp
// AttachmentImageControl.LoadImageAsync
BitmapImage bitmap = new();
await bitmap.SetSourceAsync(stream);      // 无 DecodePixelWidth / DecodePixelHeight
_image.Source = bitmap;

// BackgroundVisual.ShowCurrent
current.Source = new BitmapImage(new Uri(path));   // 同样无解码尺寸限制
```

- 问题：全 `src` 只有两处设置了解码尺寸——`MainWindow.Media.cs:171` 的媒体选择模板（`DecodePixelWidth="360"`）与 `RecentImagesView.cs:39` 的缩略图（`176`）。**真正长期驻留在看板上的两条图片路径都没有上限**：磁贴附件图与背景板图。`BitmapImage` 默认按原生分辨率解码并常驻位图。
- 触发条件与影响：手机照片常见 4000×3000（1200 万像素 ≈ 48 MB RGBA），一张 8000×6000 的全景图 ≈ 192 MB。磁贴显示区域可能只有 300×200，却为此常驻 48 MB；多块磁贴各带几张图，或背景板轮播（`BackgroundPlaylist`）切换时新旧位图短暂并存，很容易把 32 位/低内存一体机推到数百 MB 乃至 `OutOfMemoryException`。背景板还叠加了 `BlurBackdropBrush` 与导出画布（P1-19），峰值更高。
- 修复建议：(1) 磁贴附件按显示框尺寸 × `RasterizationScale` 设 `DecodePixelWidth`（例如 `Math.Max(256, (int)(_imageFrame.Width * scale))`），注意 :161 的宽高比要用解码后的 `PixelWidth/PixelHeight` 计算——等比解码不改变比值，逻辑仍成立；用户放大图片时再按需重新解码原图。(2) 背景板按窗口工作区尺寸设上限，轮播切换时先释放上一张。(3) 把"解码尺寸上限"抽成一个共用助手，避免第三处再漏。

---

## 六、P2：设计债务与健壮性隐患

共 36 条，按主题分组。除标注"疑似"外均为**已核实**（读代码确认路径存在；部分影响程度需人工确认）。

### 6.1 结构与重复实现

#### P2-1 `MainWindow` 是 18 个 partial 的上帝对象，`BoardSettingsState` 是 ~90 属性的字符串枚举集合

- 位置：`src/WorkWidgets/MainWindow.*.cs`（18 个文件）、[src/WorkWidgets/Services/ProjectState.cs](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ProjectState.cs)
- 状态：**已核实**（旧报告 P1-8 原样存在）
- 问题：`MainWindow` 承担窗口、看板、磁贴、笔迹、设置页、云同步、考试、锁定、更新、媒体、导出、悬浮岛全部职责，partial 只是把同一个类切成了 18 个文件，字段与状态仍然全局共享。`BoardSettingsState` 用字符串表达枚举：`LayoutMode`（Split/Board/Clock/Free）、`Theme`、`Palette`、`ImageMode`、`UpdateSource`、`ToolbarHideAnimation`、`MatchLevel`、`RecordLevel`、`Isolation`……取值散落在各处的 `switch` 与 `is "X" or "Y"` 模式里，没有单一真值来源。
- 影响：任何一处拼错字符串都静默走 default 分支；新增模式要改十几处；无法用编译器检查完整性。`SettingsSchema` 已经为服务端/网页维护了一份带 `Options` 的清单，但桌面端内部并未用它做类型约束，两套语义靠人工对齐。
- 建议：(1) 把字符串枚举收敛为 `enum` + `JsonStringEnumConverter`，或至少集中成 `static class LayoutModes { public const string Split = ...; }` 并让 `SettingsSchema.Options` 引用同一组常量。(2) 按职责把云同步、考试、锁定、更新拆成独立服务类，`MainWindow` 只保留编排与视觉树；这是长期工程，建议随功能改动逐步迁移而非一次性重写。

#### P2-2 `MainViewModel` 构造即生成示例数据，且自带一份与 `ColorPalette` 重复的色板

- 位置：[src/WorkWidgets/ViewModels/MainViewModel.cs](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/ViewModels/MainViewModel.cs)（构造函数 `CreateSampleSubjects()`、`_accentPalette`）
- 问题：每次创建视图模型都生成 4 个示例科目与作业，随后立刻被持久化数据覆盖；`_accentPalette` 与 `ColorPalette.Vivid` 是同一组颜色的第二份定义。
- 影响：无谓分配与初始化耗时；两处色板一旦不同步，预览与真实磁贴颜色就会分叉（`ColorPalette` 已有马卡龙转换逻辑，重复定义绕过了它）。
- 建议：示例数据改为 `CreateSample()` 静态工厂，只在确实无数据时调用；删除 `_accentPalette`，统一走 `ColorPalette`。

#### P2-3 `AppDataStore` 手写模型↔状态映射，与 `ProjectStore` 职责重叠

- 位置：[src/WorkWidgets/Services/AppDataStore.cs](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/AppDataStore.cs)（`CaptureSubjects`/`RestoreSubjects`）
- 状态：**已核实**（旧报告 P2-3 原样存在）
- 问题：逐字段手写 `Width = subject.TileWidth` 之类的映射，新增字段极易漏掉一侧；同文件还有一套 `Load`/`Save` 已无调用方（见 P2-4）。
- 建议：映射集中到一处并用往返测试覆盖（`ProjectLogic` 已有类似断言可扩展）；确认 `AppDataStore` 是否只该保留 `DataDirectory` 与旧数据迁移职责。

#### P2-4 死代码清单（均已 grep 确认无生产调用方）

| 位置 | 死代码 | 说明 |
| --- | --- | --- |
| `Services/GitHubUpdateService.cs:24-45`、`:47-69` | `CheckAsync`、`DownloadAsync` | 更新链路已整体迁到 `ReleaseUpdateService`；本类只有 `IsInstallable` 仍被使用（`ReleaseUpdateService.cs:51`、`SettingsLogic/Program.cs:97`） |
| `Services/AppDataStore.cs` | `Load`、`Save` | 持久化统一走 `ProjectStore` |
| `Controls/SubjectTileControl.cs:772`、`:921` | `CreateIconToggle`、`UndoLastStroke` | 工具栏改用 `FluentIcon`；撤销未接入 |
| `MainWindow.Lock.cs:84`、`:94` | `AskPasswordAsync(requireCurrent: true)` 分支 | 两个调用点都传 `false`，整个"校验当前密码"分支不可达 |
| `Shared/SettingsSchema.cs:277` | `Sanitize(JsonObject)` | **只有 `tests/SyncLogic/Program.cs:34` 调用**；生产路径走 `CloudPayload.cs:134` 的 `SanitizeValue`。测试验证的是生产不用的代码（见 P2-32） |
| `Models/BoardModels.cs:85` | `HomeworkEntry.Id` | 全 `src` 无任何读取点，持久化类型 `HomeworkState` 也没有该字段；`Clone()` 不复制它因此无害 |

- 建议：直接删除。保留死代码会让后续审查与重构工具反复误判"这条路径还在用"。

#### P2-5 两处完全重复的橡皮擦实现

- 位置：`Controls/SubjectTileControl.cs:892-903`、`Controls/ClockInkLayer.cs:155-165`
- 问题：逐字符相同的 `EraseStrokeAt`；两处的背景命中 alpha 还不一致（磁贴 1、时钟层 0），差异没有注释说明。
- 建议：抽到 `InkGeometry`，alpha 作为参数传入（性能修复见 P1-21）。

### 6.2 资源生命周期与事件订阅

#### P2-6 多个 `HttpClient` 从不释放

- 位置：`Services/XiaomiWeatherService.cs`（`_httpClient`）、`Services/CloudSyncService.cs`（两个实例）、`Services/ReleaseUpdateService.cs:13-19`
- 问题：这些类都不实现 `IDisposable`，内部 `HttpClient` 随实例一起被丢弃。`CloudSyncService` 在 `SyncLogic` 测试里就被创建了 4 个以上。
- 影响：socket 句柄与 `SocketsHttpHandler` 连接池延迟释放；桌面端实例数有限所以不是紧急问题，但天气服务若被反复 new 就会累积。
- 建议：要么实现 `IDisposable` 并在 `MainWindow.Closed` 释放，要么改用共享的静态 `HttpClient`（推荐，`HttpClient` 本就设计为长生命周期复用）。

#### P2-7 `VideoFrameImage.ReleaseSurfaces` 漏释放 `CanvasImageSource`

- 位置：[src/WorkWidgets/Controls/VideoFrameImage.cs:80-86](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/VideoFrameImage.cs#L80)
- 状态：**已核实**

```csharp
private void ReleaseSurfaces()
{
    _image.Source = null;
    _source = null;        // ← CanvasImageSource 也是 IDisposable，这里只丢引用
    _frame?.Dispose();
    _frame = null;
}
```

- 影响：`_source`（`CanvasImageSource`，:14）持有对 `CanvasDevice`/会话的引用，只置 null 会让它等到 GC 终结器才释放。背景板轮播频繁切换时，GPU 侧资源峰值高于必要值。
- 建议：`_source?.Dispose(); _source = null;`。该文件其余部分质量很高（:38 `Interlocked.Exchange` 单帧闸门、:77 入队失败复位、:52-57 拷贝面按显示像素封顶并 16 对齐、:88-94 `Dispose` 解绑 `VideoFrameAvailable`），这一处是明显遗漏。

#### P2-8 `BackgroundVisual` 每实例一个 250ms 常驻定时器，每 tick 走父链判可见

- 位置：[src/WorkWidgets/Controls/BackgroundVisual.cs:24](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Controls/BackgroundVisual.cs#L24)、`:91-96`（`ShownThroughTree`）
- 问题：`_activityTimer` 在 `Loaded` 启动、`Unloaded` 停止（对称，✓），但只要控件可见就每 250ms 沿视觉树父链逐级检查 `Visibility`。设置页预览、看板、导出预览可能同时存在多个实例。
- 影响：持续的 UI 线程小额开销 + 父链遍历分配；与 `MediaPerformanceVerification` 想保证的"隐藏管线安静"目标相抵（定时器本身仍在跑）。
- 建议：改为事件驱动——监听 `Visibility`/`IsVisible` 变化或 `XamlRoot.Changed`，只在状态翻转时启停；或把间隔放宽到 1s 并在不可见时彻底停表。

#### P2-9 `MainWindow.Presentation` 的 150ms 常驻轮询与每次新建的动画

- 位置：`src/WorkWidgets/MainWindow.Presentation.cs`（150ms 定时器、`:90-107` 显隐动画、`:39` `_appWindow!.Changed`）
- 问题：(1) 每 150ms 调 `GetOpenPopupsForXamlRoot` 并分配集合，只为判断"是否有浮层打开"；(2) 每次控制窗显隐都 `new Storyboard` + 6 个 `EnableDependentAnimation = true` 的动画；(3) `_appWindow.Changed` 订阅后不解绑。
- 影响：`EnableDependentAnimation` 会让动画在 UI 线程逐帧求值而非合成线程，控制窗频繁显隐时开销明显；常驻轮询在整机空闲时也不停。
- 建议：浮层状态改为在打开/关闭处维护一个计数或布尔；`Storyboard` 建一次复用（或改用 `CompositionAnimation`/`Visual.State`）；`Closed` 里解绑 `_appWindow.Changed`。

#### P2-10 事件订阅不解绑清单

| 位置 | 订阅 | 后果 |
| --- | --- | --- |
| `MainWindow.Cloud.cs:69` | `RootShell.Loaded` 内订阅 `root.Changed` | 每次 Loaded 都追加一次；不卸载 |
| `MainWindow.Cloud.cs:72` | `RegisterPropertyChangedCallback` | 无对应 `UnregisterPropertyChangedCallback` |
| `MainWindow.Presentation.cs:39` | `_appWindow!.Changed` | 见 P2-9 |
| `Services/FontService.cs` | `CreateFontPicker`/`CreateFontSizePicker` 订阅 `editor.SelectionChanged` | 编辑器销毁后仍被字体选择器持有 |
| `MainWindow.Islands.cs:447-475` | `picker.SuggestionChosen` / `QuerySubmitted` | `ApplyFontPickerAppearance` 只在 `!ReferenceEquals(_fontPickerHost, picker)` 时调用，A→B→A 切换会在**同一个 picker** 上累积处理器，一次选择触发多次 `flyout.Hide()` |

- 说明：磁贴侧的同类问题更严重，已单列为 P1-17。
- 建议：统一在 `Unloaded`/`Closed` 解绑；`Islands` 的字体按钮改为每次新建 picker 或先 `-=` 再 `+=`。

#### P2-11 `SingleInstanceService.Dispose` 在监听线程仍可能等待时释放句柄

- 位置：`src/WorkWidgets/Services/SingleInstanceService.cs`（`Dispose` 中 `Join(2000)`、`:136`）
- 问题：`Join(2s)` 超时后直接释放 `Mutex`/`EventWaitHandle`，而监听线程可能仍阻塞在 `WaitAny` → `ObjectDisposedException` 从后台线程逃逸（配合 P0-1 会终止进程）。另 `:136` `ShowWindow(windowHandle, IsIconic(windowHandle) ? 9 : 9)` 两个分支相同，是死三元。
- 建议：先置退出标志并 `Set` 唤醒事件，再 `Join`；超时则记录日志并放弃释放（进程即将退出，句柄由 OS 回收），不要在仍有等待者时释放。死三元直接写 `9`（`SW_RESTORE`）。

### 6.3 网络与云端健壮性

#### P2-12 `CloudSyncService` 的一组边界与资源问题

- 位置：`src/WorkWidgets/Services/CloudSyncService.cs`
- 问题（逐条已核实）：
  1. `MaterializeAsync`（:305 附近）直接遍历 `Subjects`，服务端返回 `null` 时 NRE。
  2. `PayloadJson` 无大小限制，超大项目会整块读进内存并多次序列化。
  3. 背景上传失败 → 撤销引用 → 下一轮再试，**没有退避**，服务端持续 5xx 时形成密集重试。
  4. `CleanupRemovedAssets` 只捕 `IOException`，`UnauthorizedAccessException` 会逃逸。
  5. `:659` 为了判断"内容是否变化"做了两次完整 `Serialize`，大项目下是明显的 CPU/分配热点。
  6. `DownloadAssetAsync`（:801-820）无体积上限，`finally` 里的 `File.Delete` 可能再抛并覆盖原始异常。
  7. `:822-827` 不强制 HTTPS（与 `ReleaseUpdateService.cs:48` 的严格校验不一致）。
- 建议：补空值与体积上限；失败退避复用 `CloudRealtimeClient` 的 1→60s 策略；变化检测改为比较哈希而非两次序列化；`finally` 内 `try/catch` 包裹删除；资源 URL 统一要求 HTTPS（内网 HTTP 场景显式开关，见 P2-15）。

#### P2-13 `CloudRealtimeClient` 的 URL 处理、消息缓冲与停止竞态

- 位置：`src/WorkWidgets/Services/CloudRealtimeClient.cs:27`、消息读取循环、`Stop()`
- 问题：(1) 用 `Replace` 全局替换 scheme，路径里含 `http` 字样会被误改；无 scheme 时 `Uri` 构造直接抛。(2) 接收消息用 `MemoryStream` 无上限，恶意/异常服务端可撑爆内存。(3) `Stop()` 释放 `CancellationTokenSource` 与运行中的循环存在竞态，可能 `ObjectDisposedException`。
- 建议：用 `UriBuilder` 改 scheme；消息设上限（例如 1 MB）并在超限时断开重连；`Stop()` 先 `Cancel()` 再等循环退出，最后释放 CTS。

#### P2-14 `XiaomiWeatherService.ParseSnapshot` 缺字段即抛，且凭据硬编码

- 位置：`src/WorkWidgets/Services/XiaomiWeatherService.cs`
- 问题：`ParseSnapshot` 用索引器取字段，上游改字段名即 `KeyNotFoundException`；`Signature = "zUFJoAR2ZVrDy1vF3D07"`、`appKey = weather20151024` 硬编码在源码里。
- 影响：`SettingsLogic/Program.cs:46-58` 只测了"无预警"和"有预警"两种完整响应，**没有缺字段用例**，所以这条路径无测试保护。硬编码凭据属公开接口签名，风险有限但应集中管理。
- 建议：改用 `TryGetProperty` + 默认值，缺字段时返回"天气不可用"而非抛；凭据集中到一处常量或配置。

#### P2-15 Gitee 更新源完全没有完整性校验

- 位置：[src/WorkWidgets/Services/ReleaseUpdateService.cs:49](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ReleaseUpdateService.cs#L49)、[:97-102](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/ReleaseUpdateService.cs#L97)
- 状态：**已核实**
- 问题：SHA-256 校验是**条件性**的——`if (part.Digest?.StartsWith("sha256:") == true)`。`digest` 来自 GitHub API 的资产字段；Gitee v5 API 不提供该字段，因此 `Digest` 为 null，**Gitee 路径下载后不做任何校验**就交给 `PortableUpdateService` 解压执行（配合 P1-12 的 `-ExecutionPolicy Bypass`）。
- 影响：HTTPS 已提供传输完整性与主机认证，所以这不是"任意代码执行"级漏洞；但一旦镜像/资产被替换或下载被截断，客户端无法发现。
- 建议：发布时同时上传 `.sha256` 清单资产，两个源都读取并强制校验；校验缺失时至少提示用户"此来源未提供校验和"。

#### P2-16 集控允许 HTTP，`DeviceToken` 以明文 Bearer 传输

- 位置：`src/WorkWidgets/MainWindow.Data.cs:213`（`uri.Scheme is not ("http" or "https")`）
- 状态：**疑似／需人工确认是否为有意的内网取舍**
- 问题：服务器地址校验放行 `http`，`DeviceToken` 随之明文过网。校内集控通常是内网部署，这可能是刻意设计。
- 建议：若确为内网取舍，在设置页明确提示"HTTP 连接不加密，凭据可能被同网段设备截获"，并默认要求 HTTPS；否则收紧校验。

### 6.4 解析、编解码与更新包处理

#### P2-17 `RtfCodec` 的输入边界与 O(n²) 归一化

- 位置：`src/WorkWidgets.Shared/RichText/RtfCodec.cs`
- 问题：`Parse` 不钳制 `\fs`（字号可为负或极大）；`\uN` 超过 65535 时静默截断；嵌套组用 `Stack` 但无深度限制（恶意 RTF 可致栈/内存膨胀）；`Normalize` 用字符串拼接，长文档 O(n²)。
- 影响：RTF 来自本机编辑与云端同步 payload，云端内容不可完全信任。
- 建议：`\fs` 钳到合理区间（如 2..400半磅）；`\uN` 用 `rune`/代理对处理或显式拒绝；组深度设上限（如 64）；`Normalize` 改 `StringBuilder`。

#### P2-18 `SettingsSchema` 的克隆与比较缺少边界

- 位置：`src/WorkWidgets.Shared/SettingsSchema.cs`（`Internal` 分支 `value?.DeepClone()`、`Same()`、`Enum` 分支 `descriptor.Options!`）
- 问题：`Internal` 类型（如 `Widgets`、`TileBackground`）整体 `DeepClone` 无大小限制；`Same()` 用 `double.Parse` 比较数字，非数字字符串会抛；`Enum` 分支对 `Options` 用 `!` 抑制可空警告，描述符漏配 `Options` 即 NRE。
- 建议：克隆前检查节点大小；`Same()` 用 `TryParse`；`Options` 缺失时抛带键名的明确异常而非 NRE。

#### P2-19 更新包的临时目录不清理，溢出异常类型不一致

- 位置：`src/WorkWidgets/Services/SplitUpdatePackage.cs:29`（`normalized-<guid>`）、`Services/PortableUpdateService.cs`（stage 目录）
- 问题：归一化与暂存目录用完后不删除，每次更新尝试都在数据目录留一份完整程序副本（数十至数百 MB）；`checked` 溢出抛 `OverflowException` 而非调用方预期的 `InvalidDataException`。
- 建议：`try/finally` 清理临时目录（失败时保留供诊断并记录路径）；溢出统一包装成 `InvalidDataException`。

### 6.5 数据、备份与锁定

#### P2-20 `BackupService` 的同名媒体处理与 O(n²) 查找

- 位置：`src/WorkWidgets/Services/BackupService.cs:52`、`:99`
- 状态：**疑似（已从 P0 降级）**
- 问题：`:52` 打包时按文件名去重，不同目录的同名媒体会被静默丢弃。但正常路径下媒体文件名由 `MediaLibrary.ImportAsync:131` 的哈希前缀与 `ProjectStore.OwnAttachments:169` 的 Guid 命名保证唯一，**只有旧数据或直接引用外部路径时才可能触发**。`:99` 的 `files.Any(...)` 在循环内，整体 O(n²)。
- 建议：去重键改为相对路径而非文件名，冲突时保留两份并记录；查找改 `HashSet`。

#### P2-21 `AutofillService` 的容量上限只在 UI 层校验

- 位置：`src/WorkWidgets/Services/AutofillService.cs`（`RecordHomework`、`EnsureBuiltIns`）；UI 校验在 `MainWindow.Settings.cs:273`、`:505`
- 状态：**已核实**（旧报告 P2-10 原样存在）
- 问题：服务层不检查 `MaxManualItems`/`MaxSubjectEntries`，任何绕过设置页的写入（云端同步、旧数据、未来的脚本）都能让词库无限增长。
- 建议：上限下沉到服务层，UI 只负责提示。

#### P2-22 锁定：凭据明文存储，解锁无失败节流

- 位置：`src/WorkWidgets/Services/ProjectState.cs`（`DeviceToken`、`TotpSecret`）、`src/WorkWidgets/MainWindow.Lock.cs:178`、`:275-293`（`UnlockAsync`）、`:253-272`
- 问题：`TotpSecret` 与 `DeviceToken` 明文存于 `projects.json`（密码本身已正确用 PBKDF2-SHA256 100k + `FixedTimeEquals`，✓）；`UnlockAsync` 没有失败次数限制或退避，可无限次尝试；`:253-272` 的 `TryEnqueue(async …)` 无 `catch`。
- 影响：能读到数据目录的人即可绕过锁定（拿到 TOTP 密钥就能生成验证码）。这属于"本地文件访问已等于完全控制"的威胁模型，但两步验证的意义正是防住这一点。
- 建议：`TotpSecret` 用 DPAPI（`ProtectedData`，当前用户范围）加密后存储；解锁失败加指数退避与次数上限；`TryEnqueue` 内补 `try/catch`。

### 6.6 UI 分配与布局热点

#### P2-23 导出预览：每次指针移动都新建变换与画笔，并做 O(n²) 重叠检测

- 位置：`src/WorkWidgets/Controls/ExportImageView.cs:357-377`（`:364` `new ScaleTransform`、`:369` 重叠检测、`:372` `new SolidColorBrush`）
- 建议：变换与画笔建一次复用，只更新数值；重叠检测用扫描线或空间划分。

#### P2-24 导出磁贴视觉：外观变化即全量重设 RTF 与重建全部笔迹

- 位置：`src/WorkWidgets/Controls/ExportTileVisual.cs:149-192`（`SetAppearance`）、`:138-147`（`SetSurface`）
- 问题：任何外观值变化都对**全部** editor 重设 RTF、重建**全部** ink `Polyline`；`SetSurface` 每磁贴每次布局都 `new ImageBrush` + `new TranslateTransform`。另 `:85-96` `PrepareAsync` 的 `await _loaded.Task` / `await image.ImageReady` **无超时**（图片失败会抛 `IOException`，✓，但从未 `Loaded` 时会永久等待，见 P2-30）。
- 建议：按字段差分更新；画刷/变换复用；等待加超时。异常保护缺失已单列为 P1-18。

#### P2-25 `SubjectTileControl` 的三条高频重算路径

- 位置：`src/WorkWidgets/Controls/SubjectTileControl.cs:383-449`（`RebuildEntries`）、`:589-604`（`CaptureRichText`）、`:229-259`（`MeasureContentSize`）
- 问题：(1) 任何一条作业变化或 `SetEditing` 都清空并重建**全部** `RichEditBox`；(2) 每次 `TextChanged` 都 `GetText` 两次 + `Normalize` + `Adapt`；(3) `MeasureContentSize` 两次 `Measure` + `InvalidateMeasure` 并遍历全部笔迹点。
- 影响：编辑长作业时逐字卡顿；自动排列时 O(磁贴数 × 笔迹点数)。
- 建议：条目增删改差分更新；`CaptureRichText` 加防抖（已有 `ScheduleSave` 可复用）；尺寸测量缓存笔迹包围盒（与 P1-21 同一份缓存）。

#### P2-26 `BoardTheme` 静态可变状态 + 每次访问新建画笔；`BoardLayout` 的 O(n³) 回退

- 位置：`src/WorkWidgets/Services/BoardTheme.cs`（`IsLight`、`TextBrush => new(TextColor)`）、`Services/BoardLayout.cs`（`Arrange`、`Snap`）
- 问题：`IsLight` 是静态可变字段，多窗口/预览与主看板会互相影响；`TextBrush` 等属性每次访问都 `new SolidColorBrush`，在绑定与布局路径上被反复调用。`Arrange` 的回退是 O(n³)（已单列 P1-3），`Snap` 每次拖动都分配 `List`。
- 建议：画笔缓存为静态只读实例并在主题切换时整体替换；`IsLight` 改为实例上下文；`Snap` 复用缓冲。

#### P2-27 若干 O(n²) 与索引器误用

| 位置 | 问题 | 建议 |
| --- | --- | --- |
| `MainWindow.xaml.cs:562` + `:689-691` | `Select(subject => FindTile(subject)!)`，`FindTile` 线性扫描 → O(n²) | 建 `Dictionary<SubjectBoard, SubjectTileControl>` |
| `MainWindow.Projects.cs:25` | `CurrentProject` 每次 O(n) 查找 | 缓存当前项目引用 |
| `MainWindow.Layout.cs:276` | `_settings.Widgets[key]` 索引器，键不存在即抛 | 改 `TryGetValue` |
| `MainWindow.Exam.cs:117-146` | 每秒新建 DTO + LINQ | 只在安排变化时重建 |
| `MainWindow.Exam.cs:242` | `_settings.Autofill.Subject.Subjects` 未判空 | 补空值保护 |

#### P2-28 背景板预览缓存键含 `SplitRatio`，导致缓存永不命中、视频进度丢失

- 位置：[src/WorkWidgets/MainWindow.SettingsPreviews.cs:253](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.SettingsPreviews.cs#L253)、`:263` 注释、`:367-372` 与 `:377-384` 已注册的增量刷新器
- 状态：**已核实**

```csharp
"Background" => $"{BoardTheme.IsLight}|{_settings.LayoutMode}|{_settings.SharedBackgroundEnabled}|{_settings.SplitRatio:0.####}",
...
// 外观值更新只修改已有控件；重建整棵预览树会卸载播放器并丢失播放进度。
```

- 问题：`SplitRatio` 已经由 :367-372 的刷新器增量处理（直接改 `ColumnDefinitions[i].Width`），`BackgroundVisual` 也由 `AddPreviewBackground` 注册了 `Refresh`。把它放进缓存键，等于**每次拖动分隔条都让缓存失效**，整棵预览树重建、`BackgroundVisual` 重新创建、播放器卸载 → 播放进度归零。这与 :263 的注释自相矛盾。
- 建议：从 `"Background"` 键里移除 `SplitRatio`（其余三项保留）。这是一行修复，收益明显。

#### P2-29 悬浮岛每次刷新强制 `Measure` 5-6 次

- 位置：`src/WorkWidgets/MainWindow.Islands.cs:616-625`（`MeasureIsland`）
- 问题：一次刷新流程里被调用 5-6 次，每次都强制 `Measure`，布局成本翻倍。
- 建议：一次刷新内缓存测量结果，或合并为单次测量后统一排布。

#### P2-30 补全取文本是 O(文档长度)，且图片就绪信号可能永不完成

- 位置：`src/WorkWidgets/Controls/AutofillInputs.cs:534-549`（`DocumentText`/`CurrentInput`）、`src/WorkWidgets/Controls/AttachmentImageControl.cs:46-47`（`_imageReady`）
- 问题：(1) 每次按键都 `GetRange(0, int.MaxValue)` + `GetRange(0, caret).GetText`，取整篇文档文本；`ApplyGuard` 守护期每 150ms 再调一次。(2) `_imageReady` 是 `TaskCompletionSource`，只在 `Loaded` 触发的 `LoadImageAsync` 里完成；若控件从未 `Loaded`（例如导出预览直接测量），`await ImageReady` 永久挂起。
- 建议：(1) 只取光标前的有限窗口（例如 64 字符）用于切词，整篇文本仅在 `Flush` 结算时取。(2) `_imageReady` 加超时或在构造时即完成为 `false`。

### 6.7 主题取色与安全

#### P2-31 代码创建的控件直接索引 `Application.Current.Resources`，浅色主题下取到深色值

- 位置：`src/WorkWidgets/MainWindow.ExamSettings.cs:28`、`:91`、`:142`、`:143`；`Controls/ColorSwatchButton.cs:44`；`Controls/InkWidthPresetButton.cs:60`
- 状态：**已核实**
- 问题：`Board*` 画笔定义在 `Themes/ThemeResources.xaml` 的 `ThemeDictionaries`（Dark/Light）里。`Application.Current.Resources[key]` 直接索引**只会命中默认（深色）字典**，不会跟随当前主题。`MainWindow.CloudControl.cs:38-41` 已有注释说明这一点，`Controls/AutofillPopup.cs:385-408` 更给出了正确实现（递归 `MergedDictionaries` → `ThemeDictionaries["Light"/"Dark"]`）。
- 影响：浅色主题下考试日卡片仍是深色底、色卡与笔宽按钮的强调色不跟随主题。`verify-interaction-contract.ps1` 已经为补全浮层断言过这条规则（要求 `ApplyTheme`/`ThemeDictionaries`），但其余控件没有同等约束。
- 建议：把 `AutofillPopup.LookupThemedBrush` 提取为共用助手（例如 `ThemeResources.Lookup(key, isLight)`），全部代码创建的控件统一走它；或改用 `FrameworkElement.Resources` + `{ThemeResource}` 等价的 `SetResourceReference`。

#### P2-32 网页壁纸：子资源网络请求未限制，`PropertiesJson` 直接拼进脚本

- 位置：`src/WorkWidgets/Controls/WebWallpaperView.cs:61`、`:47-52`
- 状态：**已核实**（安全整体做得好，这两处是缺口）
- 说明：该控件已正确关闭 `IsMuted`/DevTools/`AreHostObjectsAllowed`，`PermissionRequested` 一律 Deny，`NewWindowRequested` 置 Handled，`DownloadStarting` 取消，用 SHA-256 虚拟主机 `w-<16hex>.workwidgets.invalid` + `DenyCors`，顶层导航只允许 `https://host`（:49-52）——这些都很到位。
- 问题：(1) **只限制了顶层导航，子资源（脚本/图片/XHR/fetch）仍可访问任意外部地址**，壁纸包里的 JS 可以外联。(2) `:61` 把 `package.PropertiesJson` 直接插入 `ExecuteScriptAsync` 的脚本字符串，未做 JSON 转义校验；恶意/损坏的 `project.json` 可注入脚本。
- 建议：(1) 用 `CoreWebView2.AddWebResourceRequestedFilter("*", ...)` 拦截并按虚拟主机白名单放行，或注入 CSP。(2) `PropertiesJson` 先 `JsonNode.Parse` 校验再 `ToJsonString()` 规范化后插入，不要拼接原文。

#### P2-33 `FontService` 硬编码 LOGFONT 偏移，字体缓存无上限

- 位置：`src/WorkWidgets/Services/FontService.cs`（`Marshal.PtrToStringUni(font + 28)`、`InstalledCache`）
- 问题：`+28` 是 `LOGFONTW.lfFaceName` 在当前 Windows SDK 下的偏移，靠魔数而非结构体 marshalling；`InstalledCache` 无上限（系统字体数百个，每个都缓存字符串与判定结果）。
- 建议：定义 `[StructLayout] LOGFONT` 并 `Marshal.PtrToStructure`；缓存加容量上限或改为一次性构建的只读集合。

### 6.8 其他

#### P2-34 字符串驱动的业务分支与名实不符的异步方法

| 位置 | 问题 | 建议 |
| --- | --- | --- |
| `MainWindow.Updates.cs:23` | `message.Contains("尚未同步")` 决定分支 | 改为结果类型/错误码 |
| `MainWindow.Projects.cs:209-225` | `ActivateProjectAsync` 全同步却返回 `Task.CompletedTask` | 改为同步方法，避免调用方误以为可 await |
| `Services/NoiseMonitorService.cs` | `Stop()` 中裸 `catch { }` | 至少记录日志 |
| `MainWindow.Media.cs:147`、`:192` | 两处 `await dialog.ShowAsync()` 无 `_dialogOpen` 互斥（外层 try/catch 已覆盖异常） | 统一走对话框守卫 |
| `Controls/RecentImagesView.cs:46` | `button.Click += async … await _select(path)` 无 try/catch（三个调用方目前都已保护） | 就地补保护，避免将来新增调用方漏掉 |
| `Controls/AttachmentImageControl.cs:391-401` | `ResetImage` 不复位 `Rotation`/`AspectRatio`，名实不符 | 补齐复位或改名 |

#### P2-35 测试自身的质量问题

- 位置：`tests/SyncLogic/Program.cs`、`tests/SettingsLogic/Program.cs`
- 问题：
  1. `SyncLogic` 里大量 `await admin.PutAsJsonAsync(...)` / `PostAsJsonAsync(...)` **不检查返回状态**（:115、:174、:181、:219、:233-234、:253、:264-265、:277、:284、:387、:410、:538），只有部分调用检查了（:376、:437、:450、:465、:471、:519、:524）。前置请求静默失败时，后续断言可能以别的原因失败，也可能恰好通过 → 降低 311 条断言的可信度。
  2. `SettingsLogic/Program.cs:580-581` 的失败消息与断言方向相反：断言的是"旧 `.pbk` 已一起清理"，消息却写"旧 .pbk 自动备份未与新 .wwb 一起清理"。
  3. `SettingsLogic/Program.cs:34` 验证的 `SettingsSchema.Sanitize(JsonObject)` 生产从不调用（见 P2-4），而生产真正走的 `CloudPayload.SanitizeValue` 只在 `SyncLogic` 的端到端里被间接覆盖。
  4. `SettingsLogic` 的 `MediaLibrary` 用例（:161、:166、:250）都是**顺序**创建多实例，没有并发用例，因此 P1-14 的固定 `.tmp` 竞态完全无覆盖。
  5. `SyncLogic/Program.cs:46` `await app.StartAsync()` 在 `try` 之外，启动失败时临时目录泄漏；`admin`/`syncA/B/C` 均未释放。
- 建议：给所有写请求加 `EnsureSuccessStatusCode()`；修正反向消息；把 `SanitizeValue` 的边界用例补进 `SettingsLogic`（无需服务端）；补一条并发 `MediaLibrary` 用例。

#### P2-36 UI 验证的等待与阈值策略

- 位置：`tests/MediaPerformanceVerification.cs:49/120/127/146`、`tests/UiVerification.cs:1668/1670/1708`、`:36/61/78`
- 状态：**疑似（需人工确认机器基线）**
- 问题：性能阈值多为硬编码绝对值（1500ms、2s 内 <10 帧、<20 帧、1s 内 <5 帧、<1500ms/49 次、<1000ms/57 次），慢机上偶发失败；只有 :99-101/:129 是差分对照（而差分又被 P0-5 的 `< .01` 兜底削弱）。`:184` 在测量前取样 `frames`，期间若重建实例会漏计 → ratio 偏低假绿。`:36/61/78` 在 50ms 轮询里做全树 `FindVisuals`，观测本身干扰被测 UI 线程。
- 建议：阈值改为相对基线的倍数 + 最小样本量；测量期间固定实例引用；轮询改为事件驱动或放宽间隔。

---

## 七、P3：整洁度与微优化

共 15 条（P3-15 内含 11 项沿用旧结论的微优化）。这些不影响正确性，但会持续增加阅读与维护成本。

#### P3-1 空目录与更名残留应清理

- 位置：`src/Pancake.Core`、`src/Pancake.Platforms.Abstractions`、`src/Pancake.Platforms.Shared`、`src/platforms`、`legacy/Pancake.WinUI`（只剩 bin/obj）、`tests/RichTextLogic`、`tests/ServerLogic`、`tests/UiHeadless`
- 说明：全部无 git 跟踪文件。名字暗示的分层重构与三套测试从未落地，留着会让审查者、新人和搜索工具反复误判。
- 建议：删除，或各放一个 README 写明"计划中/已废弃"。

#### P3-2 错误消息尾随空格

- 位置：[src/WorkWidgets/Services/GitHubUpdateService.cs:27](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/GitHubUpdateService.cs#L27) — `"更新仓库格式应为 owner/repository。 "`
- 说明：旧报告 P3-6 原样存在。该行位于死代码 `CheckAsync` 内（见 P2-4），随死代码一起删除即可。

#### P3-3 `AppDataStore.ParseColor` 无任何防御

- 位置：[src/WorkWidgets/Services/AppDataStore.cs:109-115](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/AppDataStore.cs#L109)
- 说明：旧报告 P3-13 原样存在。`hex[..2]`、`hex.Substring(6, 2)` 对短字符串抛 `ArgumentOutOfRangeException`，非十六进制抛 `FormatException`。输入来自本机旧数据迁移，可达性低。
- 建议：改用 `ColorPalette.Resolve`（已有校验）或 `TryParse` 失败回退默认色。

#### P3-4 `IsAsciiLetters` 与 `IsAsciiLetter` 只差一个 s

- 位置：`src/WorkWidgets/Services/AutofillService.cs:452`（`string` 重载）、`:461`（`char` 重载）
- 说明：旧报告 P3-5 原样存在。两个名字几乎相同、语义不同（整串判定 vs 单字符判定），调用点 :432/:435/:498 容易看错。
- 建议：改名为 `IsAllAsciiLetters(string)` 与 `IsAsciiLetter(char)`。

#### P3-5 `ReadLaunchSettings` 为两个字段反序列化整个项目库

- 位置：[src/WorkWidgets/Services/SingleInstanceService.cs:153-165](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/Services/SingleInstanceService.cs#L153)
- 说明：二次启动时 `JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path))` 读入全部项目、作业、笔迹点，只为取 `AllowMultipleInstances` 与 `SecondLaunchAction`。大项目库下这是启动路径上的一次明显浪费。
- 建议：用 `JsonDocument` 只取 `Settings` 子树，或把这两个字段单独存一个小文件。

#### P3-6 版本号双来源（CI 已部分缓解）

- 位置：`src/WorkWidgets/WorkWidgets.csproj:18`（`<Version>2.1.1</Version>`）、`.github/workflows/release.yml:72`（`-p:Version=${{ steps.version.outputs.version }}`）
- 说明：旧报告 P2-12。**本次核实后降级**：CI 发布时用标签解析出的版本号覆盖 csproj，所以正式发布产物的版本是一致的；只有本地构建与仓库内 csproj 值会落后于最新标签。
- 建议：csproj 里改为 `<VersionPrefix>` + CI 传入，或加一条 CI 检查"csproj 版本 == 最新标签"，避免本地调试时误判更新逻辑。

#### P3-7 `FluentIcon` 的 47 个常量与手工映射表

- 位置：`src/WorkWidgets/Controls/FluentIcon.cs`（47 个 `public const string` + 逐条 `nameof(...) =>` 分支，未知符号抛 `ArgumentException`）
- 说明：新增图标要同时改常量、映射、XAML 三处。**但 `tests/verify-icon-font-contract.ps1` 会对照真实字体二进制的 `CharacterToGlyphMap` 校验这张表**，是仓库里最有价值的契约测试，所以风险已被覆盖。
- 建议：可用源生成器或字典初始化替代 switch，收益有限，优先级最低。

#### P3-8 硬编码颜色散落在代码创建的控件里

- 位置：`Controls/AttachmentImageControl.cs:88`、`:194-195`、`:410-412`；`Controls/AlignmentGuides.cs:16`（`Colors.DeepSkyBlue`）
- 说明：与 P2-31 同源但更轻——这些是选中框/手柄/对齐参考线，深色浅色下都还算可见，不像考试日卡片那样明显错色。
- 建议：随 P2-31 一起收敛到主题助手。

#### P3-9 冗余三元表达式

- 位置：[src/WorkWidgets/MainWindow.Islands.cs:301-302](file:///D:/Files/Codes/Projects/Pancake/Pancake/src/WorkWidgets/MainWindow.Islands.cs#L301)

```csharp
_settings.InfiniteBoard ? ScrollBarVisibility.Auto : scrollable ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
```

- 说明：前两个分支结果相同，等价于 `scrollable ? Auto : Disabled`（`InfiniteBoard` 为真时 `scrollable` 必为真）。
- 建议：化简，减少读者的分支推理负担。

#### P3-10 `verify-ui.ps1` 对空结果文件会抛而非给出可读失败

- 位置：`tests/verify-ui.ps1:60`（`$lines[-1]`）
- 说明：结果文件存在但为空时，`$lines[-1]` 直接抛索引异常，掩盖"验证进程什么都没写"这一真实信息。
- 建议：先判 `$lines.Count -eq 0` 并给出明确失败消息。

#### P3-11 `verify-release-startup.ps1` 的清理无保护，与 `verify-ui.ps1` 不一致

- 位置：`tests/verify-release-startup.ps1`（`finally` 中 `Remove-Item`）
- 说明：`verify-ui.ps1` 的清理有临时路径前缀 + 名模式双重守卫且包在 try 里；本脚本的 `finally` 直接 `Remove-Item`，清理失败会抛新异常并掩盖真实验证结果。**该脚本在 CI 中执行（`release.yml:92`），所以这会影响发版判读。**
- 建议：`finally` 内 `try { Remove-Item … } catch { Write-Warning … }`。

#### P3-12 `verify-layout-persistence.ps1` 用固定 `Start-Sleep` 等待窗口就绪

- 位置：`tests/verify-layout-persistence.ps1`（`Start-Sleep -Seconds 4`、关闭后 5s）
- 说明：慢机上 4s 不足以完成加载与保存，快机上白等；是典型的偶发失败来源。
- 建议：轮询 `projects.json` 的写入时间或进程输出的就绪标记，带超时上限。

#### P3-13 测试入口选项无文档

- 位置：`tests/SettingsLogic/Program.cs:20`（`--surface-only`）、`:60`（`--live`，会真实访问小米天气接口）；`tests/UiVerification.cs` 的 `--verify-image-drag` 等开关
- 说明：`--live` 依赖外网、`--verify-image-drag` 无任何脚本或 README 提及（见 P0-5），新人无法知道这些入口存在。
- 建议：在 `tests/README.md` 列出全部入口、依赖与预期输出标记。

#### P3-14 `tests` 目录布局不一致

- 位置：`tests/` 根下 5 个 `.cs`（`UiVerification.cs` 等）与 4 个测试项目子目录混放
- 说明：根下的 `.cs` 不是独立项目，而是被 `WorkWidgets.csproj:45-51` 条件编译进主程序；子目录才是可 `dotnet run` 的测试。两种性质混在同一层，容易误以为根下文件也能独立运行。
- 建议：把 5 个 UI 验证文件移入 `tests/UiVerification/` 子目录（`<Compile Include>` 路径同步更新），或在 README 里说明两类测试的区别。

#### P3-15 旧报告中仍未处理、本次复核后沿用结论的微优化

以下各项在旧报告里已提出，本次逐一 grep 确认**仍然存在**（行号已更新到当前代码）。影响都很小，列出以免在迭代中彻底遗失。

| 旧编号 | 当前位置 | 问题 | 建议 |
| --- | --- | --- | --- |
| P3-2 | `MainWindow.xaml.cs:1413` | 手势距离用 `Math.Sqrt(Math.Pow(dx,2)+Math.Pow(dy,2))`，`Math.Pow` 比乘法慢一个量级，且在指针移动路径上 | 改 `dx*dx + dy*dy`，或直接比较平方距离省掉 `Sqrt` |
| P3-3 | `MainWindow.xaml.cs:25` + `:835-836`、`:846`、`:855` | `GridSize` 是 `Math.Clamp(Math.Round(...))` 属性，却在 `DrawGrid` 的循环条件与增量里反复求值 | 循环前取一次局部变量 |
| P3-4 | `MainWindow.xaml.cs:740` | `UpdateBoardBounds` 每次拼 6 字段插值字符串做缓存键，该方法在尺寸/布局变化时高频调用 | 改用值元组比较或结构化键 |
| P3-10 | `Services/NoiseMonitorService.cs:112`、`:122` | 魔法数 `94`（dB 参考）与 `0.0000001`（下限）两处重复 | 提为命名常量 |
| P3-11 | `Controls/ExportImageView.cs:243`、`:247` | 亮度阈值 `145` 是魔法数；`_appearanceTask = RefreshBackdropAsync(...)` 不 await（:179/:252/:406 会补等，属有意设计，但异常保护缺失已单列 P1-18） | 阈值提为常量并注释依据 |
| P3-12 | `MainWindow.Islands.cs`、`MainWindow.SettingsPreviews.cs`、`MainWindow.Layout.cs` | 排版魔法数密集（`7*scale`、`Round(8*scale)`、`6.5`、`1.8`、`720/760/520/560`） | 集中到排版常量类 |
| P3-14 | `MainWindow.Settings.cs:1488-1489` | `FloatingToolbar.MaxWidth/MaxHeight` 每次 `ApplyToolbarSettings` 都从 `RootShell.Actual*` 重算 | 只在尺寸变化时更新 |
| P2-4 | `Controls/AttachmentImageControl.cs:46`、`Controls/ExportImageView.cs:58`、`:414`、`Controls/ExportTileVisual.cs:25` | 4 处 `TaskCompletionSource` 未指定 `RunContinuationsAsynchronously`，续体可能内联在完成线程上（`ExportImageView:414` 的布局等待尤其需要注意重入）。对照：`tests/SyncLogic/Program.cs` 的 6 处全部正确指定了该选项 | 统一加 `TaskCreationOptions.RunContinuationsAsynchronously` |
| P2-8 | 仓库根 | 仍无 `.editorconfig`、`Directory.Build.props`、分析器与 `TreatWarningsAsErrors`（本次构建 0 警告，但没有机制保证以后也是 0） | 加 `Directory.Build.props` 统一 `Nullable`/`AnalysisLevel`/`TreatWarningsAsErrors` |
| P2-9 | 全 `src` | UI 文案全中文硬编码，本地化不可行 | 若暂无多语言计划，明确记为产品取舍即可 |
| P2-11 | `design-qa.md:13`、`:41` | 视觉验收结论仍是 `final result: blocked` | 与 P0-5 一起解决：验证体系不可信时，视觉验收无法解除阻塞 |

---

## 八、与旧报告 `CODE-REVIEW.md`（2.0.6）的对照

旧报告共 3 条 P0、11 条 P1、12 条 P2、15 条 P3。本次**逐项复核**（不照抄结论），结果如下。

### 8.1 已修复（4 项）

| 旧编号 | 旧问题 | 复核证据 |
| --- | --- | --- |
| P1-1 | `github.Result!` / `gitee.Result` 空引用与阻塞风险 | `MainWindow.Updates.cs:17` 已加 `await Task.WhenAll(gitee, github)`，:19-20 访问 `.Result` 时两个任务均已完成，不再阻塞也不为空 |
| P2-6 | 命令行解析用子串匹配、两处风格不一致 | 已收敛为 `Services/LaunchArgs.cs` 精确解析；`tests/SettingsLogic/Program.cs:432-453` 有 22 条断言覆盖形近开关、大小写、`--view` 未知值回退、组合参数 |
| P3-8 | `legacy/WorkWidgets.WinUI/obj/` 下 5 个构建产物被 Git 跟踪 | `git ls-files legacy` 为空，`legacy/Pancake.WinUI` 只剩未跟踪的 bin/obj |
| P3-9 | `.gitignore` 只有 4 行，未忽略 `legacy/**/obj` | 现为 6 行，含 `**/bin/`、`**/obj/`、`artifacts/`，已覆盖 |

### 8.2 部分修复（3 项）

| 旧编号 | 已做的部分 | 仍缺的部分 | 本报告对应 |
| --- | --- | --- | --- |
| P0-3 | 新增 `tests/SyncLogic`（311 断言）并接入 CI，固定服务端提交 | `main`/PR 无 CI；其余三套逻辑测试仍不在流水线；解决方案仍无测试项目 | P0-4 |
| P1-2 | `MainWindow.xaml.cs:116` 改为 `Version?.ToString() ?? "未知版本"` | `MainWindow.xaml.cs:1261` 仍是 `GetName().Version!` | 归入 P2-4 同类清理 |
| P2-12 | CI 发布用 `-p:Version=` 覆盖 csproj（`release.yml:72`），正式产物版本一致 | 仓库内 csproj 仍硬编码 `2.1.1`，本地构建与最新标签会脱节 | P3-6（已降级） |

### 8.3 仍然存在（34 项）

**P0 级（2 项，原样存在）**

| 旧编号 | 当前位置 | 本报告 |
| --- | --- | --- |
| P0-1 全局异常处理器不拦截异常 | `App.xaml.cs:13`，仍未设 `args.Handled = true` | P0-1 |
| P0-2 `ClearInk_Click` async void + 绕过守卫 + 绕过回滚 | `MainWindow.Projects.cs:376`（关键行 :387），仍无 try/catch、无 `_dialogOpen` | P0-2 |

**P1 级（9 项）**

| 旧编号 | 复核结果 | 本报告 |
| --- | --- | --- |
| P1-3 空 `catch { }` 吞颜色解析异常 | 仍存在：`MainWindow.Settings.cs:1074` `try { …BrushFromHex… } catch { }` | P1-7 同源 |
| P1-4 笔迹擦除 O(笔画×点数) + 每帧分配列表 | 仍存在，且时钟层是第二份完全相同的实现 | P1-21、P2-5 |
| P1-5 自动排列对富文本做 O(列×磁贴) 次 Measure | 仍存在：`SubjectTileControl.cs:229-259` 两次 Measure + InvalidateMeasure | P2-25、P1-3 |
| P1-6 async void 事件处理器（13 处） | **仍存在且范围扩大**：新发现导出预览、编辑锁续期、屏幕上报、天气搜索等路径 | P0-2、P1-5、P1-6、P1-18、P2-34 |
| P1-7 `MainWindow` 拆分后仍约 5000 行 | 仍存在：18 个 partial 共享同一份状态，拆分未降低耦合 | P2-1 |
| P1-8 `BoardSettingsState` 约 60 属性 + 字符串冒充枚举 | **仍存在且增长到约 90 属性**，字符串枚举种类更多 | P2-1 |
| P1-9 解决方案不包含测试项目 | 仍存在：`WorkWidgets.slnx` 只有 2 个产品项目 | P0-4 |
| P1-10 更新脚本依赖系统 PowerShell + Bypass | 仍存在：`PortableUpdateService.cs:53/55` | P1-12 |
| P1-11 更新包无代码签名 | 仍存在；本次进一步发现 **Gitee 源连 SHA-256 都没有**（`digest` 仅 GitHub API 提供） | P2-15 |

**P2 级（10 项）**

| 旧编号 | 复核结果 | 本报告 |
| --- | --- | --- |
| P2-1 `AppDataStore.Save`/`Load` 死代码 | 仍存在，且 `GitHubUpdateService.CheckAsync`/`DownloadAsync` 也成了死代码 | P2-4 |
| P2-2 笔迹逻辑两处重复 | 仍存在 | P2-5 |
| P2-3 对象映射四套并行实现 | 仍存在 | P2-3 |
| P2-4 4 处 TCS 未指定 `RunContinuationsAsynchronously` | 仍存在，位置已变（见 P3-15 表格） | P3-15 |
| P2-5 窗口尺寸变化路径重复工作 | 仍存在：悬浮岛每次刷新强制 Measure 5-6 次、每次设置改动全量重建工具栏 | P2-29、P1-16 |
| P2-7 `SubjectTileControl` 死代码 | 仍存在：`CreateIconToggle:772`、`UndoLastStroke:921` | P2-4 |
| P2-8 缺 editorconfig / 分析器 / TreatWarningsAsErrors | 仍存在 | P3-15 |
| P2-9 UI 文案全中文硬编码 | 仍存在（产品取舍） | P3-15 |
| P2-10 自动填充上限只在 UI 层校验 | 仍存在：`Settings.cs:273/505` 校验，服务层不校验 | P2-21 |
| P2-11 `design-qa.md` 视觉验收 blocked | 仍存在：`design-qa.md:41` `final result: blocked` | P3-15、P0-5 |

**P3 级（13 项）**：P3-1（HttpClient，现已增至 4+ 处 → P2-6）、P3-2、P3-3、P3-4、P3-5、P3-6、P3-7（孤儿目录，现增至 3 个测试空目录）、P3-10、P3-11、P3-12、P3-13、P3-14、P3-15（`UiVerification.cs` 从 2101 行增至 2162 行）。除 P3-1 升级为 P2-6、P3-7 并入 P3-1、P3-15 升级为 P0-5 外，其余原样沿用于本报告 P3-15 表格。

### 8.4 本次新增、旧报告未覆盖的问题

旧报告聚焦于当时可见的崩溃与整洁度问题。本次新增的实质性发现主要集中在三个方向：

1. **数据丢失与迁移**：P0-3（备份还原跳过 `.json` → 网页壁纸必坏）、P1-10（还原无事务 + SyncQueue 复活）、P1-11（单项目非法致整库停写）、P1-4（瞬时 404 清空考试缓存）。这些都在 2.0.6 之后新增的备份/云同步/考试功能里。
2. **内存与渲染**：P1-17（磁贴被模型事件永久持有，每次重建看板泄漏整棵视觉树）、P1-2（点阵网格数万个 Ellipse）、P1-19（8192² 画布同步拷贝）、P1-23（图片全尺寸解码）、P1-20（WebView2 环境缓存失败结果）。
3. **测试有效性**：P0-5 整条——旧报告只说"CI 不跑测试"，本次进一步证明**即使跑了，验证信号本身也大面积不可信**（契约脚本当前必红、真失败仍写 OK、恒真断言、空集合断言、`Environment.Exit(0)`）。这是本次审查最重要的新结论。

另有 P1-22（补全定位折叠用户选区）属于旧报告未触及的交互细节缺陷。

---

## 九、建议的优先修复顺序

排序依据：**数据不可逆损失 > 进程崩溃 > 门禁可信度 > 用户可见缺陷 > 性能 > 整洁度**。同一批内按改动成本从低到高。

### 第一批：立刻处理（预计 0.5–1 个工作日，改动都很小）

| 顺序 | 条目 | 改动量 | 理由 |
| --- | --- | --- | --- |
| 1 | **P0-3** 备份还原跳过 `.json` | 改一处过滤条件 + 补一条往返测试 | 唯一会造成**用户数据不可逆丢失**的问题：还原后网页壁纸必然损坏 |
| 2 | **P0-1** 全局异常设 `Handled` | 一行 + 兜底提示 | 任何未捕获 UI 异常都直接终止进程，是所有其他崩溃条目的放大器 |
| 3 | **P0-2** `ClearInk_Click` 加 try/catch + `_dialogOpen` | 约 10 行 | 清空笔迹是不可逆操作，当前既无保护也无互斥 |
| 4 | **P0-5(1)** 修 `verify-interaction-contract.ps1:98-99` | 两行正则 | 让长期红灯恢复意义；这是后续所有门禁工作的基础 |
| 5 | **P0-5(2)** `MediaPerformanceVerification.cs:158` 按结果决定退出码 | 三行 | 当前失败也返回 0，CI 判据完全失效 |
| 6 | **P1-22** 补全用游离 `ITextRange` 取光标矩形 | 两行 | 用户可见的"选区自己消失"，修复成本极低 |
| 7 | **P2-28** 背景预览缓存键移除 `SplitRatio` | 一行 | 拖动分隔条即丢失视频播放进度，一行修复 |

### 第二批：下个迭代（预计 3–5 个工作日）

1. **P0-4** 新增 `pull_request` / `push: main` 触发的 workflow，跑构建 + 4 套逻辑测试；测试项目加入 `WorkWidgets.slnx`。**这是防止本报告问题再次累积的唯一结构性手段。**
2. **P0-5(3)(4)** 清理恒真断言、空集合断言、`RatioAgainst` 兜底、`NextLayoutAsync` 无超时等待；给 `ScheduleFontAuditVerification` 加断言或改名为诊断。
3. **P1-17** 磁贴 `Detach`：`BuildTiles` 前解绑模型事件（`CollectionChanged`/`PropertyChanged`），或改用弱事件。这是当前最大的内存泄漏源。
4. **P1-10 / P1-11** 备份还原加事务与回滚；单项目非法时隔离该项目而非停写整库。
5. **P1-13 / P1-5 / P1-6 / P1-18** 统一 async void 治理：计时器与事件回调全部包 try/catch，`Closed` 停齐所有计时器，对话框统一走 `_dialogOpen`。
6. **P1-4** 云端考试瞬时 404/204 不清缓存（区分"确实停用"与"请求失败"）。
7. **P1-23** 图片解码尺寸上限（磁贴 + 背景板）。
8. **P1-8 / P1-9 / P1-15** 三处解析边界：分卷号 `int.TryParse`、RTF 颜色表钳制、`ErrorText` 判 `ValueKind`。

### 第三批：性能专项（需要专门排期，建议先量化再改）

1. **P1-2** 点阵网格改 `Win2D` 单次绘制或 `CompositionBrush`（当前每交点一个 `Ellipse`）。
2. **P1-21 + P2-5** 笔迹：包围盒粗筛 + 共享橡皮擦实现 + 书写最小距离阈值。
3. **P1-19 + P2-23 + P2-24** 导出链路：画布分块渲染、变换/画刷复用、外观差分更新。
4. **P1-1 + P1-14** 备份压缩移出 UI 线程；`MediaLibrary` 单实例化 + 唯一 tmp 名 + `Flush`。
5. **P1-3 + P2-26 + P2-27** 排版 O(n³) 回退、`BoardTheme` 画笔缓存、若干 O(n²) 查找。
6. **P1-16 + P2-25 + P2-29** 设置改动差分刷新，取代全量重建工具栏与所有磁贴。
7. **P2-7 / P2-8 / P2-9** 释放 `CanvasImageSource`、定时器改事件驱动、动画复用。

> 建议：第三批动手前先把 `MediaPerformanceVerification` 的断言修好（第二批第 2 项），否则无法判断优化是否真的生效——这正是 P0-5 的实际代价。

### 第四批：结构性重构与安全加固（长期）

1. **P2-1** 字符串枚举收敛 + `MainWindow` 按职责拆服务（随功能改动逐步迁移，不做一次性重写）。
2. **P2-31 + P3-8** 主题取色统一走 `AutofillPopup.LookupThemedBrush` 提取出的共用助手。
3. **P2-32** 网页壁纸子资源网络白名单 + `PropertiesJson` 规范化。
4. **P2-22** `TotpSecret` 用 DPAPI 加密；解锁失败退避。
5. **P2-15 + P1-12** 发布 `.sha256` 清单并强制校验；评估去掉 `-ExecutionPolicy Bypass`（改为签名脚本或 `pwsh -File`）。
6. **P2-12 / P2-13 / P2-17 / P2-18** 云端与解析层的边界、退避、体积上限。
7. **P0-5(5)** 用真实行为测试逐步替换 4 个源码正则脚本（保留 `verify-icon-font-contract.ps1`）。

### 第五批：随手清理（可合并进任意 PR）

P2-4 死代码删除、P3-1 空目录清理、P3-2～P3-14 全部微优化、P2-35 测试自身质量、P3-15 表格各项。

### 不建议现在做的事

- **不要**为了"消除上帝对象"而一次性重写 `MainWindow`：18 个 partial 共享状态，大爆炸式重构在没有可信测试网（P0-5）的前提下风险极高。先修门禁，再增量拆分。
- **不要**在修好验证断言前做第三批性能优化：无法度量的优化容易变成自我安慰。
- **不要**把 `design-qa.md` 的 `blocked` 直接改成 `pass`：它的阻塞原因正是验证体系不可信，应随 P0-5 一起解除。

---

## 十、本次审查的限制

1. 全部 UI 行为结论来自静态阅读，未运行任何会启动窗口或需要交互的验证脚本（清单见 1.4）。标注"需人工确认"的条目需要在真机上复核。
2. 未执行 `installer/Optimize-Publish.ps1` 与 `ApplyUpdate.ps1`，发布产物完整性与更新回滚的实际行为未实测。
3. 服务端（`PancakeServer`）只在 `SyncLogic` 固定提交的快照范围内被间接验证，未审查服务端源码。
4. 性能结论中的数量级（元素个数、复杂度）按代码推算，未做 profiling 实测。
5. 本报告不修改任何现有文件；`CODE-REVIEW.md` 保持原样。