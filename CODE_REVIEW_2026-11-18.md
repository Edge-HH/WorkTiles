# WorkTiles 客户端地毯式代码审查报告

- **审查日期**：2026-11-18
- **仓库根**：`D:\Files\Codes\Projects\WorkTiles\WorkTiles`（分支 `main`）
- **审查方式**：只读。逐目录、逐文件系统性阅读全部源码（含未提交改动），8 个模块并行审查 + 主审逐条读码复核关键发现；所有行号来自实际 Read 输出，关键 High/Critical 与两条独立发现均已亲自二次验证。未修改/创建/删除任何项目文件（本报告除外），未执行构建、测试、安装或任何 git 写操作。
- **技术栈**：WinUI 3 / Windows App SDK 1.8（`Microsoft.WindowsAppSDK.WinUI 1.8.260803003`）、.NET 8（`net8.0-windows10.0.19041.0`）、x64、非打包（`WindowsPackageType=None`）、`SelfContained=false`、Version 2.1.1。依赖：Win2D 1.4.0、NAudio.Wasapi/WinMM 2.4.0、QRCoder 1.7.0。许可证 GPLv3。
- **架构边界**：`MainWindow` 为 partial class，拆成约 20 个分片文件；数据便携存于 `AppContext.BaseDirectory/data/projects.json`（旧名 `pancake.json` 迁移）；云同步走 HTTP/JSON + WebSocket 实时通道（Bearer DeviceToken、编辑锁协议、三方合并）；更新链 GitHub/Gitee Releases → 分卷解压 → `ApplyUpdate.ps1`；单实例用 `Local\` 命名事件 + Mutex + `data/second-launch.json`；关闭即隐藏（`AppWindow.Closing` 取消除非 `_allowClose` 或 verification 模式）。

> 说明：本项目为 C#/WinUI 3 桌面应用，不存在 Electron 的主进程/渲染进程/preload 边界；审查维度已按实际技术栈调整（窗口/托盘基础设施、数据与保存守卫、云同步、桌面磁贴、输入控件、导出与杂项服务、更新链与构建、测试体系）。

---

## 一、总体结论

代码基础功扎实：原子写（`ProjectStore.AtomicWrite`、`MediaLibrary.Save`、资产下载均为临时名 + `Flush(true)` + 原子替换）、Zip Slip 双层防护、PowerShell 参数注入防护（有测试实证）、TLS 校验未被关闭、PBKDF2/TOTP 算法正确、噪音采集无音频落盘、冲突副本保住双方内容、Composition/Win2D 画刷成对释放、GDI 字体句柄无泄漏。近期「同步失败不再丢图」「减轻界面压力」等修复在对应路径上确实成立。

但存在一条**完全瘫痪且静默的核心流程**（便携更新）和一组**数据完整性 / 安全边界缺陷**，集中在三个状态机边界：

1. **更新链双重断裂**：更新路径 `Close()` 被自己的「关闭即隐藏」处理器取消（Critical），进程永不退出、脚本 60 秒后静默失败；同时已发布资产名为 `Pancake-*`，被客户端 `WorkTiles` 过滤器排除（High），当前双源更新发现已失效。再叠加全链路无独立来源认证（哈希可选、Gitee 零校验、安装器无签名），更新链「不能用、不可信、不可诊断」。
2. **保存守卫时序 / 复用错误**：备份还原与托盘重启共用 `_skipSaveOnClose` 状态机，还原存在覆盖竞态（在途同步/自动保存用旧内存覆盖刚还原的磁盘），托盘重启跳过退出保存丢未保存内容；集控落盘绕过 `_storageReady` 写保护。
3. **编辑状态与同步替换边界**：`IsProjectBusy` 漏判桌面磁贴编辑（`_desktopEditing`），网页强制接管 + 修订前进会无声替换大屏正在编辑的内容；锁屏被托盘「应用设置」一行门控缺失完全绕过。

测试体系是另一重灾区：`verify-layout-persistence.ps1` 与当前源码契约全面脱节（恒失败 + 泄漏僵尸进程 + 删除用户真实自启），多处「跳过仍恒过」「精确串恒红」「本地开箱即失败」，且绝大多数测试不在 CI 中运行、半数无文档——安全网本身存在系统性漏洞。

**统计**：共 178 条独立问题（已跨模块去重 7 条）。Critical 1、High 16、Medium 51、Low 110。其中 16 条 High 有 7 条属测试基础设施；面向最终用户的生产 High 为 9 条。

---

## 二、严重程度统计与口径

| 严重程度 | 数量 | 口径 |
|---|---|---|
| **Critical** | 1 | 数据丢失 / 安全突破 / 核心流程完全瘫痪且静默 |
| **High** | 16 | 重要功能缺陷或明确安全风险（含 7 条测试基础设施） |
| **Medium** | 51 | 边界条件 / 竞态 / 性能 / 局部数据正确性 / 防御缺口 |
| **Low** | 110 | 健壮性、死代码、外观、维护性、低概率边界 |
| **合计** | **178** | 已去重 |

按模块分布（去重后 canonical 计数）：

| 模块 | Critical | High | Medium | Low | 小计 |
|---|---|---|---|---|---|
| 1 应用入口与窗口基础设施 | 0 | 1 | 4 | 17 | 22 |
| 2 设置/数据/项目/布局/锁屏/考试 | 0 | 3 | 5 | 10 | 18 |
| 3 云同步与项目数据层 | 0 | 1 | 10 | 15 | 26 |
| 4 桌面磁贴/壁纸/视频/工具栏岛 | 0 | 0 | 4 | 10 | 14 |
| 5 磁贴控件/输入/自动填充/笔迹/字体 | 0 | 1 | 8 | 22 | 31 |
| 6 导出/背景画刷/噪音/天气/杂项服务 | 0 | 0 | 4 | 15 | 19 |
| 7 更新链/共享库/构建与 CI/脚本 | 1 | 3 | 6 | 8 | 18 |
| 8 测试与验证脚本 | 0 | 7 | 10 | 13 | 30 |

「疑似」项均在条目内标注验证方法，未臆断为确定缺陷。

---

## 三、最高风险 Top 5

1. **[Critical] 便携更新 `Close()` 被「关闭即隐藏」拦截，更新必然静默失败**
   `src/WorkTiles/MainWindow.xaml.cs:1573`（配合 `:224-229`）。更新分支 `PersistProjects(); PortableUpdateService.Launch(...); Close();` 未置 `_allowClose=true`，Closing 处理器一律 `Cancel+Hide`。进程不退出 → `ApplyUpdate.ps1:15` `WaitForExit(60000)` 超时 → 写 `UPDATE_FAILED`、不替换不重启。用户以为在更新，实际永远停在旧版本缩在托盘。一行 `_allowClose=true` 即可修复。（模块 7 G-1，亦见模块 1 B-H1）

2. **[High] 已发布更新资产名 `Pancake-*` 被 `WorkTiles` 过滤器排除，双源更新发现已失效**
   `src/WorkTiles/Services/ReleaseUpdateService.cs:52` 要求 `name.Contains("WorkTiles")`。实测 GitHub 最新 `Pancake v2.1.2` 资产 `Pancake-win-x64-2.1.2.zip`、Gitee 最新 v2.0.7 资产 `Pancake-win-x64-2.0.7.zip`，均被排除 → `ParseRelease` 返回空更新。与 #1 叠加，当前更新链整体不可用。（模块 7 G-A）

3. **[High] 备份还原被在途同步/自动保存覆盖，还原静默失效**
   `src/WorkTiles/MainWindow.Data.cs:101-103` + `MainWindow.Cloud.cs:216`。`Restore` 写盘后先弹模态对话框，`_skipSaveOnClose` 要到对话框之后才置位；挂起期间自动保存/自动备份/集控同步用还原前的旧内存整体覆盖刚还原的 `projects.json`，并把旧状态推上云端。重启后加载到的仍是旧数据。（模块 2 A-H1，亦见模块 3 C-H2）

4. **[High] 桌面磁贴编辑未纳入 busy 判定，云端强制接管 + 替换丢失正在编辑的内容**
   `src/WorkTiles/MainWindow.Cloud.cs:230` `IsProjectBusy` 只看 `_isEditing || _projectOperation`，漏 `_desktopEditing`；`MainWindow.CloudEditLock.cs:67` 接管分支同样只看 `_isEditing`。网页老师强制接管并保存 → 修订前进 → 实时通知 → 替换当前项目 → 大屏正在写的内容无声消失（关自动保存时丢整个编辑会话）。（模块 3 C-H1 / C-M6）

5. **[High] 托盘「应用设置」绕过锁屏验证，可关闭整条锁定链**
   `src/WorkTiles/MainWindow.xaml.cs:1232-1237` 托盘 `CommandSettings` 直接 `ShowSettings()`，无 `AuthorizeAsync()`；而正常入口 `SettingsButton_Click:471-477` 有验证。锁定是入口级 enforcement，托盘菜单在锁屏期间始终可达 → 学生经托盘进设置关掉「启用锁定」与两个验证开关，整条锁定链被绕过。（模块 2 A-H3）

> 其余生产 High（同样建议优先）：无签名更新供应链（G-2）、托盘重启丢保存（A-H2）、替换实例失败后操作已 Dispose 闸门崩溃（B-H2）、EnsureSlot 重排父级致正文默认样式静默失效（E-H1）、csproj 引用未跟踪文件致全新克隆构建失败（G-B）。

---

## 四、按模块问题明细

> 每条格式：**编号 | 严重程度 | 位置** — 描述 / 影响 / 修复。模块内按严重程度排序。「疑似」附验证方法。跨模块重复项只在 canonical 模块列出，其余以「亦见」交叉引用（对照表见第五节）。

### 模块 1：应用入口与窗口基础设施

覆盖：`App.xaml(.cs)`、`MainWindow.xaml(.cs)`（1–1784 全覆盖）、`WindowChrome.cs`、`TrayMenuWindow.cs`、`CrashReportWindow.cs`、`InstancePromptWindow.cs`、`Services/SingleInstanceService.cs`、`TrayIcon.cs`、`LaunchArgs.cs`、`StartupRegistration.cs`、`SystemAccent.cs`、`AppIcon.cs`、`app.manifest`、`ViewModels/MainViewModel.cs`。

#### High

**B-H2 | High | `src/WorkTiles/App.xaml.cs:163-177` + `Services/SingleInstanceService.cs:87`**
替换实例失败后，提示窗「重启/返回」按钮操作已 Dispose 的闸门，抛 `ObjectDisposedException`。`ReplaceRunningInstanceAsync` 第 87 行先 `current.Dispose()`（释放全部事件句柄，240-243）再轮询；失败返回 null 时 App 失败分支（166-168）调 `prompt.AllowBack()` 重新启用按钮，但闸门已死。点「重启当前实例」→ `_quitSignal.Set()`、点「返回」→ `_foregroundSignal.Set()` 都对已释放句柄操作 → 经 async void Click 冒到全局处理器弹崩溃窗。旧实例卡死/退出超 10 秒（40×250ms）这一并不罕见的场景下，用户重试得到的是崩溃窗，只有「取消」能正常退出。
修复：把 `current.Dispose()` 移到成功认领新闸门之后（失败时保持 current 可用）；或失败分支重新 `SingleInstanceService.Start()` 拿新闸门。

> B-H1（更新 Close）已升级为 Critical，见模块 7 G-1。B-H3（托盘重启丢保存）与 A-H2 重复，见模块 2。

#### Medium

**B-M1 | Medium | `src/WorkTiles/MainWindow.xaml.cs:146,153,1534-1538`**
静默启动时自动更新在隐藏窗口上弹不可见 ContentDialog，`_dialogOpen` 永久占位，屏蔽一切确认对话框。`RootShell.Loaded` 自动更新检查不区分 `_silentStart`；发现新版后 `ShowUpdateConfirmAsync` 在隐藏窗口 XamlRoot 上 `ShowAsync()`，对话框打开但不可见，`_dialogOpen=true` 持续到有人关掉。开机静默自启 + 恰好有新版时，主窗口隐藏期间所有走 `_dialogOpen` 守卫的对话框（删除科目确认 1770、锁定/考试验证、消息 1778）全部静默返回 None/失败；桌面磁贴仍可交互，用户点删除毫无反应。
修复：静默/隐藏状态下推迟更新确认（首次 `AppWindow.Show()` 后再弹）或改托盘通知；`Hide()` 时不启动交互式更新流程。

**B-M2 | Medium | `src/WorkTiles/MainWindow.xaml.cs:1420-1439`（尤其 1429）**
天气首次刷新失败后永不重试。`_weatherTimer.Start()` 只在成功分支执行，全仓库无其他启动点（已 grep 核实）。开机自启时网络往往未就绪，首次失败后天气组件永久停留「天气不可用」，10 分钟周期重试根本不会建立，必须人工进设置点「测试并刷新」。
修复：把 `_weatherTimer.Start()` 移到 finally（失败也按 10 分钟重试），或失败时安排较短退避重试。

**B-M3 | Medium | `src/WorkTiles/MainWindow.xaml.cs:1450-1471`**
天气地区选择对话框游离于 `_dialogOpen` 守卫之外，且搜索回调是无保护 async void。1465 直接 `dialog.ShowAsync()` 不检查/设置 `_dialogOpen`；1455 `searchBox.TextChanged += async … await RefreshResultsAsync()`，而 `WeatherCityCatalog.SearchAsync/LoadAsync`（WeatherCityCatalog.cs:15-28）对文件缺失/JSON 损坏无 try/catch。影响：① 地区框打开期间后台更新确认/二次启动转发也去 ShowAsync → WinUI「同一时间只能一个 ContentDialog」`InvalidOperationException`；② `xiaomi_weather_cities.json` 缺失/损坏时每次键入经 async void 抛未处理异常崩溃；③ 快速键入多个 SearchAsync 并发完成顺序不定，旧结果覆盖新结果。
修复：地区框纳入 `_dialogOpen` 守卫；`RefreshResultsAsync` 包 try/catch 显示错误；用 CTS/版本号丢弃过期搜索结果。

**B-M4 | Medium | `src/WorkTiles/App.xaml.cs:139-145,156-186`**
二次启动完全忽略 `--silent`，静默启动会弹出「已在运行」提示窗。`OnLaunched` 非首实例分支无条件 `ShowInstancePrompt`，不检查 `launchArgs.Silent`。已有实例运行时再次触发静默自启（计划任务、重复 Run 项、手动再点快捷方式）会在教室大屏中央弹可见提示窗，违背静默承诺。
修复：非首实例且 `Silent` 时直接 `ForwardToRunningInstance`（携带显式参数则写 payload）后 `Exit()`，不创建任何窗口。

#### Low

**B-L1 | Low | `Services/SingleInstanceService.cs:171`** — `IsIconic(hwnd) ? 9 : 9` 死三元，唤醒总发 SW_RESTORE，把用户已最大化的窗口悄悄取消最大化。修复：改 `IsIconic ? 9 : 5`（SW_SHOW）。

**B-L2 | Low | `Services/SingleInstanceService.cs:230-243`（配合 208-222）** — Dispose 在 `Join(2s)` 超时后仍释放三个事件句柄，阻塞中的 `WaitHandle.WaitAny` 抛 `ObjectDisposedException`，后台线程未处理异常终止进程。修复：Join 失败时不 Dispose 事件句柄（留给进程退出回收），或确认线程退出后再释放。

**B-L3 | Low | `Services/SingleInstanceService.cs:59-61,79,115-150,255-259`** — 跨实例 IPC 无鉴权：`Local\` 命名事件 + `data/second-launch.json`，同会话任意进程可 Set 退出事件强杀看板、抢先写 payload 让运行实例导入任意 `.wth/.pch`；payload 仅 15 秒时间戳新鲜度、无完整性校验，`WritePayload` 非原子、读后即删，并发二次启动互相覆盖。因攻击者需已具同用户执行权限（未跨权限边界）定 Low。修复：命名对象设仅当前用户 SID 的 DACL；payload 加 nonce/HMAC 或改带 ACL 匿名管道；临时文件 + `File.Move` 原子写。

**B-L4 | Low | `Services/SingleInstanceService.cs:188-206,111-112`** — 单实例设置与信号分支整体死代码：`ReadLaunchSettings` 无调用者；`AllowMultipleInstances`/`SecondLaunchAction`（ProjectState.cs:108-111）被持久化、规范化但无 UI 无运行时效果；`_fullScreenSignal` 永无人 Set，`NoneAction`/`FullScreenAction` 不可达；xaml.cs:232,242 注释误导。修复：删死字段/分支/方法或真正接入 OnLaunched，同步修正注释。

**B-L5 | Low | `Services/TrayIcon.cs:65-83`** — `ExtractAssociatedIcon` 的 HICON 从不 `DestroyIcon`（每次创建泄漏一个，当前每进程一次）；`StringBuilder(260)` 与 `longPathAware` 清单冲突，超长安装路径被截断静默回退通用图标。修复：Dispose 时对提取所得图标 `DestroyIcon`（区分系统共享图标）；缓冲扩容或改 `LoadImage`/直接加载 `Assets\AppIcon.ico`。

**B-L6 | Low（疑似）| `Services/TrayIcon.cs:33,48-52,106-117`** — 静态 wndproc 钩子表按 hwnd 键控、永不清理，句柄复用会链到错误窗口过程：`Previous` 字典 Dispose 时不移除/不恢复原 wndproc，OS 回收 hwnd 后同进程再装 TrayIcon 时 `TryAdd` 失败沿用旧指针。当前主窗口每进程只建一次，触发苛刻。验证：Spy++ 观察窗口销毁重建后 hwnd 复用。修复：Dispose 时 `SetWindowLongPtr` 恢复原过程并移除条目，或按实例字段保存。

**B-L7 | Low | `Services/TrayIcon.cs:85-104`** — `ShowMenu()` 及 MfString/TpmReturnCmd 等常量为死代码（所有托盘点击都路由到自定义 TrayMenuWindow，原生 TrackPopupMenu 从未调用）。修复：删除 ShowMenu 与仅被它使用的常量/P-Invoke。

**B-L8 | Low | `WindowChrome.cs:14-23`** — `AsToolWindow`/`ShowWithoutActivating` 全仓库无调用者；且 `AsToolWindow` 若将来用于已显示窗口缺 `SWP_FRAMECHANGED`，任务栏按钮不会立即消失。修复：删除，或补 `SWP_FRAMECHANGED` 并注明需显示前调用。

**B-L9 | Low | `Services/LaunchArgs.cs:40-45,60-66`** — `--project` 空值时把下一个开关吞成项目名（`--project= --silent` → project="--silent" 且丢掉真开关）。修复：pendingProject 消费下一参数前检查是否以 `--` 开头。

**B-L10 | Low | `CrashReportWindow.cs:21-29`** — Present 构造/激活失败时 `_open` 永久卡死（`_open=true` 先于 `new CrashReportWindow`，Closed 订阅未建立则永不复位），进程余生所有崩溃被静默吞掉。修复：Present 内 try/catch，失败复位 `_open` 并记日志。

**B-L11 | Low（重启竞态疑似）| `CrashReportWindow.cs:122-135`** — 崩溃窗「重启」先起新进程再 Exit，可能撞单实例闸门弹「已在运行」；`Application.Current.Exit()` 绕过 Closed 不触发 SaveStateNow（关自动保存时丢未保存内容）；重启不带 `--silent/--windowed/--project`，静默实例崩溃重启后变全屏可见。验证：负载下点崩溃重启观察是否出现提示窗。修复：重启前释放闸门或等旧进程退出再拉起；透传原始命令行；退出前视状态完整性尝试保存。

**B-L12 | Low | `InstancePromptWindow.cs:86-94,109` + `App.xaml.cs:179-184`** — 替换进行中系统关闭按钮 X 仍可点：`_restarting` 期间只禁用三个按钮，X → Closed → `becameMain=false` → Exit，而旧实例已收退出信号 → 两个实例都没了。修复：`_restarting` 期间拦截 AppWindow.Closing（Cancel=true）。

**B-L13 | Low（疑似）| `InstancePromptWindow.cs:119-131,197-244`** — fitTimer 未随窗口关闭停止，成功替换路径（640ms 内 `prompt.Close()` 且 becameMain 不 Exit）剩余 Tick 继续 `PlaceOnScreen`，对已销毁窗口 AppWindow 取 Size/Resize 可能抛 COMException → 主窗口刚启动就弹崩溃窗。验证：快速点「重启当前实例」（旧实例秒退）观察 crash.log。修复：Closed 中 `fitTimer.Stop()`；PlaceOnScreen 加已关闭守卫。

**B-L15 | Low | `MainWindow.xaml.cs:448-469`** — 放弃修改后未重置编辑按钮无障碍名称：`FinishEditing` 改回「编辑看板」（438），`DiscardEditButton_Click` 只改图标 Glyph（463）漏了名称，读屏仍报「保存修改」。修复：463 附近补 `AutomationProperties.SetName(EditBoardButton, "编辑看板")`。

**B-L16 | Low | `MainWindow.xaml.cs:204`** — `LevelAvailable` 未合流，UI 忙时按采样率（默认 10Hz）无限排队（FastLevelAvailable 已有 Interlocked 合流 197-203），恢复后集中补放陈旧读数，与「减轻噪音界面压力」方向不一致。修复：套用「最新值 + 单飞标记」合流。

**B-L17 | Low | `MainWindow.xaml.cs:222`** — 初始窗口固定 1440×900，1366×768 等教室旧投影/笔记本 `--windowed` 启动时右下被裁掉，够不到边缘控件。修复：按 `DisplayArea.WorkArea` 夹取初始尺寸。

**B-L18 | Low | `MainWindow.xaml.cs:236,245-261`** — 二次启动处理整体 fire-and-forget（`_ = HandleSecondLaunchAsync`），`AppWindow.Show()`/`SetFullScreen`/`ApplyViewArgument` 抛出的异常成未观察任务异常无声消失，唤醒失败无日志无提示。修复：包 try/catch 写 `App.WriteCrashLog`。

> B-L14（BrushFromHex 8 位色语义）与 F-#7 重复，见模块 6。

---

### 模块 2：设置 / 数据 / 项目 / 布局 / 锁屏 / 考试

覆盖：`MainWindow.Settings.cs`、`SettingsPreviews.cs`、`Data.cs`、`Projects.cs`、`Layout.cs`、`BoardNavigation.cs`、`Presentation.cs`、`Updates.cs`、`Lock.cs`、`Exam.cs`、`ExamSettings.cs`、`ClockInk.cs`。

#### High

**A-H1 | High | `src/WorkTiles/MainWindow.Data.cs:101-103`（配合 `:345-371`、`MainWindow.xaml.cs:188-193/1672-1689`、`MainWindow.Cloud.cs:216`）**
备份还原存在覆盖竞态：还原后的磁盘数据会被内存旧状态写回。流程 `service.Restore(file.Path)`（101，同步写回 projects.json 与资源）→ `await ShowMessageAsync("还原完成"…)`（102，模态等待）→ `RestartAfterRestore()`（103）。`_skipSaveOnClose=true` 与停表发生在 347-349，即对话框之后。挂起期间 UI 定时器照常触发：① `_saveTimer.Tick`（xaml.cs:188-193）→ SaveStateNow；② `_autoBackupTimer` → RunAutoBackupIfDue → SaveStateNow（Data.cs:334）；③ 集控周期同步 → `RunCloudSyncBody` 直接 `_projectStore.Save(_library)`（Cloud.cs:216，不检查 `_skipSaveOnClose`）。此时内存仍是还原前旧状态，SaveStateNow 守卫（xaml.cs:1674）两条件都不成立。重启失败分支（Data.cs:363-368）把标志改回 false 后让窗口继续运行，但内存未重载，之后任何保存再次覆盖还原结果。影响：还原静默失效；开自动保存/集控/自动备份时触发概率不低；SaveStateNow 还 RequestCloudSync（xaml.cs:1683）把旧状态推上云端污染副本。
修复：保护动作提前到 Restore 成功后、弹框前——立即 `_skipSaveOnClose=true`、停 `_saveTimer`/`_autoBackupTimer`、停集控周期定时器/取消在途同步；Cloud.cs:216 加 `_storageReady && !_skipSaveOnClose` 守卫；重启失败分支改为从磁盘重载（复用 LoadPersistentState）或直接退出。（亦见模块 3 C-H2）

**A-H2 | High | `src/WorkTiles/MainWindow.Data.cs:345-371`（RestartAfterRestore），调用方 `MainWindow.xaml.cs:1238-1240`（TrayIcon.CommandRestart）**
托盘「重启」复用还原专用路径，跳过退出保存丢未保存数据。`RestartAfterRestore` 第一行 `_skipSaveOnClose=true`（347）使 Closed 里的 SaveStateNow（xaml.cs:170）经守卫（1674）短路。该语义对「还原后重启」正确，但托盘「重启」直接复用，重启前无任何保存。影响：关自动保存时违反 Data.cs:29 承诺（「关闭后仅在退出/切项目/保存作业/立即保存时写入」），整段未保存编辑（可能一节课板书）静默丢失；开自动保存时丢 600ms 防抖窗口内（及被 A-M3 停表后积压）的改动。对比托盘「退出」（xaml.cs:1241-1244）走正常 Close→保存，行为不一致。
修复：托盘重启单列路径——先 `SaveStateNow()`（尊重 `_storageReady`）再执行不跳过保存的重启；或拆 `RestartCore(bool skipSaveOnClose)`，还原传 true、托盘传 false。（亦见模块 1 B-H3）

**A-H3 | High | enforcement 面 `src/WorkTiles/MainWindow.Lock.cs`（AuthorizeAsync 239-243、TryShowSettingsAsync 296-299），绕过点 `MainWindow.xaml.cs:1232-1237`**
托盘「应用设置」绕过锁屏验证，可直接进设置页关闭锁定。正常入口 `SettingsButton_Click`（xaml.cs:471-477）先 `await AuthorizeAsync()` 再 ShowSettings；`TryShowSettingsAsync`（Lock.cs:296-299）同样带验证。但托盘 `CommandSettings` 直接 `AppWindow.Show(); Activate(); ShowSettings(); ShowSettingsPage("General")`，无验证。锁定是入口级 enforcement（无全屏锁罩），托盘右键菜单在锁屏期间始终可达。影响：设了密码/2FA 并开「打开设置前要求验证」的教室场景，学生经托盘进设置关掉「启用锁定」（Lock.cs:18-19）或两个「需要验证的操作」开关（Lock.cs:31-32），随后编辑看板也不再需验证（NeedsEditAuth 依赖这些开关）——整条锁定链被完全绕过。附带：托盘「显示/隐藏桌面磁贴」（xaml.cs:1222-1231）也在无验证下改设置，同一暴露面。
修复：`CommandSettings` 改走与 SettingsButton_Click 相同的 `AuthorizeAsync()` 门（可复用 TryShowSettingsAsync），验证失败不 Show；CommandShowTiles/HideTiles 同样纳入 NeedsEditAuth/设置验证。

#### Medium

**A-M1 | Medium | `src/WorkTiles/MainWindow.Projects.cs:309-310`（上下文 298-311、172-194）**
删除项目后清理附件只捕获 IOException，其他异常触发「磁盘已提交、内存却回滚」。`DeleteProject_Click` 在 307 `ActivateProjectAsync` 内经 PersistProjects 已把删除提交磁盘，随后 `try { DeleteAssets } catch (IOException)`。`Directory.Delete` 同样抛 `UnauthorizedAccessException`（占用/杀软/权限），逃逸到 RunProjectAction catch（180-191）把 `_library` 回滚到删除前。308 注释明确「清理失败仅留无引用资源，不恢复已删项目」，实现与意图不符。影响：磁盘项目已删、附件部分删除，内存却「复活」该项目；下次保存把复活项目写回，其引用媒体已缺失 → 坏项目，且与集控删除通知（305）矛盾。
修复：309-310 改 `catch (Exception)`（或至少补 UnauthorizedAccessException），仅报告「项目已删，附件未能清理」，不让清理失败进入事务回滚。

**A-M2 | Medium | `src/WorkTiles/MainWindow.Projects.cs:183-184`（catch 180-191）**
项目事务回滚整体替换 `_settings` 实例，设置页闭包写向孤儿对象，且回滚后 UI 不刷新。回滚 `_library = before; _settings = _library.Settings;`，before 是 `ProjectStore.Clone`（JSON 往返深拷贝，178）全新实例。设置页控件闭包持有旧实例子对象：`BackgroundEditor` 的 `style.Color=…`（Settings.cs:1168/1174/1177/1183/1186）、`SubjectAutofillRow` 的 `item.Enabled=…`（Settings.cs:366/391）、背景媒体编辑器 style 闭包（Media.cs:98-128）。回滚后这些控件继续写旧对象，改动不再进入 `_settings`；catch 只重建磁贴（189），没有 RefreshSettingControls/补全刷新/ApplyExtendedSettings，显示值与回滚后内存也不一致。代码库已有 `SettingsCopy.Into` 原地合并工具（CloudSyncService.cs:625-627 同法保身份）。影响：任何项目操作失败回滚后，设置页改背景颜色/媒体、学科补全开关、播放队列等静默丢失，界面与实际脱节直到重建。
修复：回滚改原地恢复 `SettingsCopy.Into(_library, before)`（不替换引用），随后 RefreshSettingControls() + `_refreshSubjectAutofill/_refreshHomeworkAutofill` + ApplyExtendedSettings()（Into 对列表/字典清空再灌入，补全行闭包仍换身份，必须依赖 Refresh 回调重建）。（亦见模块 4 D-#13）

**A-M3 | Medium | `src/WorkTiles/MainWindow.Projects.cs:176`（finally 193 未恢复）**
RunProjectAction 停掉保存防抖表后，早退/异常路径不恢复，积压自动保存被丢弃。进入事务即 `_saveTimer.Stop()`；大量路径在动作执行前 return（新建取消 280、重命名取消 293、删除取消 301、导入未选 319、另存未选 340、清空笔迹取消 419/423），catch 回滚同样不恢复。ScheduleSave（xaml.cs:1662-1670）只有下一次变更才重新起表。影响：开自动保存时点一下「重命名」又取消，之前 600ms 防抖内积压改动就不再定时落盘，要等下次编辑或退出；期间崩溃/断电即丢。与 A-H2 叠加丢失窗口更大。
修复：finally（193）补 `if (_isLoaded && _settings.AutoSaveEnabled) ScheduleSave();`（或记录进入时是否 pending，早退时恢复）。

**A-M4 | Medium | `src/WorkTiles/MainWindow.Data.cs:76-77,98/101,326-334`**
备份/还原/自动备份的压缩包 IO 全部同步跑在 UI 线程。`BackupService.Create/Read/Restore/PruneAutoBackups` 同步 zip 压缩/解压/删除，包体上限 4GB（BackupService.cs:21），媒体多时数百 MB。自动备份在启动时与每 30 分钟定时器触发，导出/还原由按钮直接同步执行。影响：大资源库下界面冻结数秒到数十秒（启动即卡、上课中每半小时卡一次），期间时钟/考试倒计时停走，观感像崩溃。
修复：zip IO 包进 `await Task.Run(...)`，期间显示进度 InfoBar/禁用按钮，完成回 UI 线程刷新；RunAutoBackupIfDue 改 async 并加 busy 守卫防重入。（亦见模块 3 C-M10）

**A-M5 | Medium | `src/WorkTiles/MainWindow.Cloud.cs:216`**
集控同步落盘绕过 `_storageReady` 写保护。`RunCloudSyncBody` 末尾 `if (_settings.CloudSync.Enabled) { _projectStore.Save(_library); … }` 不检查 `_storageReady`。加载失败保护（xaml.cs:1654-1658 置 false 并提示「已停止写入以保护原数据」）对这条路径无效。可达场景：`_projectStore.Load()` 成功但后续规范化/控件赋值（xaml.cs:1592-1652）抛异常时，`_settings` 已是加载出的实例（集控可能启用），同步照常落盘。影响：违背「读取失败即停止写入」契约，数据已异常时继续覆盖 projects.json，可能把可恢复原始数据固化成半损坏；也是 A-H1 的触发器之一。
修复：该行改 `if (_storageReady && !_skipSaveOnClose && _settings.CloudSync.Enabled) { … }`，与 SaveStateNow 守卫对齐。

#### Low

**A-L1 | Low | `MainWindow.Settings.cs:282-287,1174,1333-1338`** — 取色器拖动每 tick 触发全量 ApplyExtendedSettings 无节流（285 强制 `_renderedGridAppearance=string.Empty` 使网格每 tick 重画）；同文件已有轻量合并模式 QueueSurfaceRefresh（1147/1149，1493-1514）。低配教室拖颜色掉帧。修复：拖动期间走合并刷新或 150ms 防抖，Flyout 关闭再全量兜底。

**A-L2 | Low | `MainWindow.Settings.cs:1124,1128`** — Choice 对未知取值静默回退第一项不回写模型（`SelectedIndex=Math.Max(0, Array.IndexOf(...))`），界面显示「选项A」实际生效非法值下游默认，且永远无法经该控件纠正。修复：`IndexOf<0` 时调 `update(values[0])` 归一化（加载与 refresh 两处）。

**A-L3 | Low | `MainWindow.Lock.cs:18-19,31-32`** — 锁定页三个 Toggle 缺 `read:` 回调（Toggle 助手 Settings.cs:963-969 只有带 read 才注册刷新），集控下发或 A-M2 回滚后不刷新，开关显示与实际相反，用户按显示拨动写出相反值。修复：补三个 `read:` 回调。

**A-L4 | Low | `MainWindow.Exam.cs:171-175,200-204` + `MainWindow.xaml.cs:1749`** — 考试强制全屏标记不随用户手动切换清除：考试期间 Esc 可退全屏（1749 不拦截）标记不清；用户随后手动进全屏，考试结束时 HideExamOverlay 见标记真且当前全屏 → SetFullScreen(false) 把用户手动选择强行退回窗口。修复：SetFullScreen 区分用户主动切换时清标记，或记录考试前状态恢复时比对。

**A-L5 | Low | `MainWindow.Data.cs:219`** — 集控配对码未 Trim，粘贴带空白即配对失败（对比解锁输入做了 Trim，Lock.cs:275-293）。修复：`pairingCode.Text.Trim()`。

**A-L6 | Low | `MainWindow.Projects.cs:354`（catch 351-356）** — 另存失败清理逻辑可能抛异常掩盖原始保存异常：`File.Delete(path)` 本身可能抛 IOException/UnauthorizedAccessException，此时 `throw;`（355）不再执行，外层收到清理异常而非真正打包失败原因。修复：清理包独立 try/catch，保证 throw 始终重抛原始异常。

**A-L7 | Low | `MainWindow.Projects.cs:322-326`** — 导入项目先解包资源后提交，失败回滚遗留孤儿资源：WthPackageService.Import（322）已解包到磁盘，PersistProjects（324）/ActivateProjectAsync（326）抛异常时事务回滚内存但资源目录无清理。修复：Import 记录解包目录，catch 中尽力清理；或先提交清单再解包。

**A-L8 | Low | `MainWindow.Settings.cs:186-191`** — 噪音设置页用硬转型 + 魔法下标向 XAML 卡片插控件（`((StackPanel)NoiseSettingsCard.Child).Children.Insert(2/3,…)`），XAML 结构调整即 InvalidCastException 或插错位置，编译期无法发现。修复：XAML 放命名占位元素按名插入，或整卡改代码构建。

**A-L9 | Low | `Services/StartupRegistration.cs:9,15-18`（独立发现）** — 开机自启 Run 值名为全局常量 `"WorkTiles"`，不区分安装目录：多份安装共用同一值名互相覆盖；`Apply(enabled:false)` 无条件 `DeleteValue` 会删掉另一份安装登记的自启（测试脚本亦借此删除用户真实自启，见 H-3）。修复：值名含安装路径哈希或产品+路径区分；删除前校验当前值指向本安装路径再删。

**A-S1 | Low（疑似）| `MainWindow.Layout.cs:289`（对照 170 用 TryGetValue）** — Free 布局编辑态 `_settings.Widgets[key]` 直接索引可能 KeyNotFoundException。常规路径安全（ApplyFreeWidgets 先补齐键）；风险面在键被外部清掉而补齐尚未发生：集控 `SettingsCopy.Into` 对字典 Clear 后灌入源实例（SettingsCopy.cs:24-26），若服务器快照 Widgets 缺键，且合并后、ApplyCloudLayout 的 ApplyExtendedSettings（Cloud.cs:303）之前有事件触发 UpdateLayoutHandles（如 GlobalPen_Changed，Projects.cs:365→373）即抛未处理异常。验证：读 CloudSyncService 构造 merged（625-627）确认 Widgets 是否属本地键总被保留；或构造服务器下发空 Widgets 报文复现。加固（无论是否可达都廉价）：289 改 `TryGetValue`，与 170 防御风格一致。

---

### 模块 3：云同步与项目数据层

覆盖：`Services/CloudSyncService.cs`(864)、`CloudPayload.cs`(206)、`CloudRealtimeClient.cs`(89)、`ProjectMerge.cs`(76)、`ProjectState.cs`(423)、`ProjectStore.cs`(345)、`BackupService.cs`(304)、`AppDataStore.cs`(116)、`MediaLibrary.cs`(294)、`MainWindow.Cloud.cs`(522)、`CloudControl.cs`(211)、`CloudEditLock.cs`(124)。

#### High

**C-H1 | High | `src/WorkTiles/MainWindow.Cloud.cs:230`**
`IsProjectBusy` 漏掉桌面磁贴编辑状态，云端替换会丢正在编辑的内容。busy 判定 `id == _library.ActiveProjectId && (_isEditing || _projectOperation)`。桌面磁贴编辑走 `_desktopEditing`（DesktopTiles.cs:88），不置 `_isEditing`，但编辑同一份 `ViewModel.Subjects`（NoteTileContentChanged→ScheduleSave）。此时 PullOneAsync（CloudSyncService.cs:240）不认为项目忙，服务器修订更新即 Install 替换并触发 ReloadActiveProject（Cloud.cs:247-258）直接 `ReplaceSubjects(...)`，未保存桌面编辑被丢弃；AutoSaveEnabled=false 时丢整个编辑会话。现实触发链完整：网页老师强制接管编辑锁后 RenewEditLockAsync 接管条件只看 `_isEditing`（CloudEditLock.cs:67），桌面编辑不退出；网页保存使修订前进 → 实时通知 → SyncProjectsOnlyAsync → 替换当前项目 → 大屏正在写的内容无声消失。
修复：IsProjectBusy 增 `|| _desktopEditing`（必要时含 `_editAuthPending`）；RenewEditLockAsync/OnEndEditRequested 接管与 end-edit 分支同时处理桌面编辑（调 EndDesktopEdit 并保存）。验证：开桌面磁贴编辑（关自动保存），网页强制接管并保存该项目，观察大屏内容是否被替换。

> C-H2（还原覆盖竞态）与 A-H1 重复，见模块 2。

#### Medium

**C-M1 | Medium | `Services/ProjectMerge.cs:32-53`** — 三方合并静默丢弃本机对 Name/CustomPalette/CustomPalettes/AssignmentDate 的修改。上传载荷含这些字段（ProjectStore.cs:6-28、CloudPayload.cs:50-62），但合并只对 ClockInkStrokes/Theme/Palette 做字段级三方判定（33），`merged=Clone(remote)` 后其余字段一律取服务器值。本机改项目名/自定义色系、远端改某科目作业时合并成功，本机改名/色系被无声还原，提示却称「两边改的是不同科目，已自动合并」（CloudSyncService.cs:454）。修复：把 Name/CustomPalette/CustomPalettes/AssignmentDate 纳入 33 行字段级 TakeLocal 循环（两边都改且不同→返回 null 走冲突副本），提示列出实际采用字段。

**C-M2 | Medium | `Services/CloudPayload.cs:146-157`** — 背景播放列表部分解析失败时直接丢弃失败项，且记入 Base 后不再重试。LayoutSync.Merge 中 `resolvedItems.Count < items.Count` 时仅当全部失败才保留本机列表；部分失败时新列表只含成功项（155），失败项被删，随 unresolved 进入 Rebase（CloudSyncService.cs:624）写成 Base → 下轮 diff 为空不再下载/传回。跨设备更常见：他机背景上传失败保留原始本机路径（556），本机 Resolve（190）因文件不存在返回 null → 同样被删。一次瞬时下载失败让播放列表永久缺项。修复：部分失败时按引用/索引保留本机对应项（或整组沿用本机列表并记 unresolved）。

**C-M3 | Medium（疑似）| `Services/CloudSyncService.cs:39,795-797`** — `_uploaded` 缓存永不失效，服务器资源丢失后上传悬空引用，其他设备永久拉不下该项目。UploadAssetAsync 命中 `_uploaded`（键 ClassId/sha）直接返回引用跳过 HEAD/PUT；服务端 HEAD/GET 同时校验 DB 行与磁盘文件（AssetEndpoints.cs:22,114）。若服务器资源被清/存储迁移/DB 与文件不一致而本机缓存仍在：冲突重置路径（430-436，SyncRevision=0 重新上传）把悬空引用载荷传上去；其他设备 MaterializeAsync 下载失败 → 永远保留旧版（315-319）。重启本机也不自愈：缓存虽清空但 hash==SyncHash 使队列项被丢弃（357）。验证：保留服务端 DB、删除 assets 存储中某文件，本机改该项目文字触发上传，另一台设备拉取。修复：CredentialsRejected/冲突且 ServerVersion null/ResetSyncState 时清空 `_uploaded`；或服务器 PUT 项目时校验 asset 引用存在。

**C-M4 | Medium | `MainWindow.CloudControl.cs:155-157,179-181`** — 按钮闭包捕获旧 ProjectDocument 实例，同步替换后点击把旧内容回写并上传（回滚服务器修改）。班级页「在看板上打开」/项目按钮捕获渲染时 project 实例；同步轮中途 Install 用新实例替换清单元素（CloudSyncService.cs:334），重渲染要等整轮结束 RefreshCloudStatusViews；窗口期内点击 → SwitchProjectAsync(stale) → ActivateProjectAsync 用 stale.Subjects 载入 → PersistProjects → CaptureCurrentProject 按 Id 找到新实例（Projects.cs:25,108）把 stale 内容写进它的 Subjects → Queue → 以新修订号上传旧内容，服务器刚收到的修改被静默回滚。最近项目浮出层（Projects.cs:207-211）同 pattern 窗口更大。修复：闭包只捕获 project.Id，点击时从 `_library.Projects` 重新解析，找不到提示已变更。

**C-M5 | Medium | `MainWindow.Cloud.cs:208-225`** — 同步轮内落盘失败被静默吞掉。RunCloudSyncBody 的 catch 只 Debug.WriteLine；216 `_projectStore.Save` 失败（磁盘满/权限/校验不过）时本轮编辑与服务器修订/哈希都没持久化，界面却照常显示同步完成；其他写盘路径都有 ReportStorageError，唯独这条高频路径没有。修复：catch 区分落盘失败并 ReportStorageError("同步结果未能保存", ex)。

**C-M6 | Medium | `MainWindow.CloudEditLock.cs:63-69,111-116`** — 强制接管/end-edit 只处理看板编辑；空闲时 end-edit 立即释放锁不等上传。① RenewEditLockAsync 仅在 `_isEditing && HeldBy.Holder==User` 时退出编辑；桌面编辑被接管后继续无锁编辑（与 C-H1 叠加成丢数据链）；HeldBy==Device（另一大屏接管）完全无动作。② OnEndEditRequested 非看板编辑分支直接 `_editLockGranted=null` 释放锁，不管 `_editLockReleaseAfterSync` 是否还有未上传完修改——网页随即载入大屏保存前的旧内容，与 72 行注释「网页因此一定能载入大屏刚保存的内容」矛盾。修复：接管与 end-edit 同时结束桌面编辑；非编辑态收到 end-edit 若 `_editLockReleaseAfterSync==projectId` 走 ReleaseEditLockIfUploaded「上传完再释放」。

**C-M7 | Medium | `Services/CloudSyncService.cs:78-79,842-847`** — 允许明文 http/ws，设备令牌、配对码与作业内容可被局域网嗅探。配对与所有 API 用 cloud.ServerUrl 原样拼接，`Authorization: Bearer DeviceToken` 随每请求发送；实时通道 http→ws（CloudRealtimeClient.cs:27,47）。设置页占位符即 `http://192.168.1.10:5080`（Data.cs:173），scheme 校验接受 http（Data.cs:213），无 TLS 强制或警告（证书校验本身默认开启）。影响：校园网内学生设备可 ARP 欺骗/嗅探获取令牌 → 冒充大屏读写删除全班项目、向所有大屏发指令。修复：优先 https/wss；选 http 明确警告；局域网可自签证书 + 指纹固定；文档写明威胁模型。

**C-M8 | Medium | `Services/ProjectState.cs:188,253-255`（存储链 ProjectStore.cs:161、BackupService.cs:122,177-178）**
DeviceToken 与 TOTP 密钥明文持久化，且随备份导出/还原克隆设备身份。`CloudSyncSettings.DeviceToken`、`LockSettings.TotpSecret/PasswordHash/PasswordSalt` 随 library.Settings 明文写进 `data/projects.json`；BackupService.Create 把完整 Settings 打进 settings.json（122），还原原样写回（177-178），不脱敏不重新配对。ProjectState.cs:247 注释「只保存密码哈希与 2FA 密钥，不保存明文」有误导性——TOTP 密钥本身就是明文密钥材料。影响：教室共享电脑任何本地进程/用户可读令牌接管班级集控、用 TOTP 密钥自行生成有效验证码绕过 2FA；备份复制到另一台电脑还原得到两台同令牌同 DeviceId 的「同一设备」，状态上报与定向指令互相错乱。密码侧 PBKDF2 实现正确，但哈希+盐同文件可离线爆破弱密码。
修复：令牌/TOTP 用 DPAPI（ProtectedData，CurrentUser）加密落盘；备份导出剔除/标记 CloudSync.DeviceToken/DeviceId，还原到新机器强制重新配对；修正 :247 注释并在设置页提示密钥明文保存。
> 注：F-#5 曾称 TOTP 存于 `worktiles.json`（AppDataStore.Save），经核实 AppDataStore.Save/Load 全仓库无调用者（死代码，见 C-L13），实际存储为 ProjectStore → projects.json，已并入本条并修正路径。（亦见模块 6 F-#5）

**C-M9 | Medium | `Services/CloudSyncService.cs:355,792,813,822`** — 大文件 SHA-256 与整项目 JSON 克隆/序列化全部在 UI 线程同步执行。同步约定在界面线程续体运行（31 注释）；UploadAssetAsync 首行同步 `_hasher.Sha256Of(path)`（整文件读盘哈希）；DownloadAssetAsync 对已存在目标与下载临时文件各做一次同步全量哈希；PushAsync/MaterializeAsync 每轮对每个项目 BuildProjectPayload → ProjectStore.Clone（CloudPayload.cs:52，缩进 JSON 序列化+反序列化整份项目，含可达 2 万条笔迹）。附件多/图片大时每轮数百 ms 到秒级 UI 冻结，3 分钟兜底同步使其周期性发生，抵消「界面减压」努力。修复：哈希与载荷构建移入 Task.Run（只读快照后后台计算），结果回 UI 线程应用；库对象突变仍留 UI 线程。

**C-M11 | Medium | `Services/BackupService.cs:52,99`** — 包内条目按文件名去重，同名不同内容文件被静默合并/丢弃。背景 AddFile：`if (byPath.ContainsValue(entry)) return;` 第二个同名文件直接跳过，其引用经 EntryOfPath 得 ""，设置里该背景被静默置空。项目附件：`entry=projects/{id}/{文件名}`，`if (!files.Any(item=>item.Entry==entry))` 同项目内两个不同源文件同名时只打包第一个，但两条 asset.Path 都改写成同一 entry，还原后第二张图被第一张顶替。现代 Guid/hash 命名不撞名，但 ProjectStore.Load 接受任意绝对路径遗留附件（ProjectStore.cs:72），旧数据不同目录同名图片（IMG_0001.jpg）真实可撞。修复：条目名加内容哈希前缀或冲突序号，按源路径一一映射；同名不同源显式报错而非跳过。

> C-M10（备份同步 IO 压 UI 线程）与 A-M4 重复，见模块 2。

#### Low

**C-L1 | Low | `CloudSyncService.cs:115-122`** — CleanupRemovedAssets 里 `catch(IOException){}` 后仍 `_removedProjects.Remove(id)`，删除失败目录永不再清理；UnauthorizedAccessException 未捕获会中断整批。修复：失败保留 id 下轮重试，捕获更宽 IO 异常族。

**C-L2 | Low | `CloudSyncService.cs:328-335`** — 被服务器版本替换的项目旧附件文件永不回收（Install 直接换文档，旧 Guid 附件留在 data/projects/<id>/assets，只有远端删除才清理），长期双向同步磁盘持续增长。修复：落盘成功后删除新文档未引用的旧附件。

**C-L3 | Low | `CloudSyncService.cs:177-188`** — RefreshBoard/RefreshExam 绕过同步闸门（不检查 `_running`、不经 RunCloudSyncAsync），与在途轮同名请求并发时响应乱序，Board/Exam/ServerClockOffset/DeviceId 可能被旧响应覆盖，LastSyncError 互相串写（transient 下轮自愈）。修复：并入统一闸门或按序执行。

**C-L4 | Low（疑似）| `CloudSyncService.cs:574-577`** — 任一背景上传失败就跳过所有背景键规范化写回（`normalized` 过滤 `!(BackgroundKeys.Contains(k) && failed.Count>0)` 是全局的），一个文件持续失败时其余已成功但被服务器规范化的背景键也不写回 → 与 Base 永远有 diff → 每轮 PATCH、修订号空转。验证：播放列表放一个超大文件 + 一个 lowercase 颜色，观察多轮是否重复 PATCH 同一键。修复：按键记录失败集合，只豁免失败键。

**C-L5 | Low（疑似）| `CloudPayload.cs:23-26`** — 哈希缓存按 大小+mtime 判新，保留时间戳的同尺寸覆盖（robocopy /COPY:DAT）命中旧哈希 → 去重/引用指向旧内容。NTFS 时间精度下常规编辑不命中。验证：保留时间戳同尺寸替换已缓存文件后触发同步。修复：缓存命中文件抽样重算，或文档化该风险。

**C-L6 | Low | `CloudPayload.cs:108,140,148,200`** — 对服务器 JSON 先 `GetValue<string>()` 后校验，类型不符抛 InvalidOperationException 使布局步骤中止（不在 607 行 JsonException/InvalidDataException 过滤内，落 StepAsync 泛型 catch，报错晦涩；设置本身不被污染）。修复：先按 GetValueKind 判型或先 sanitize 再提取引用。

**C-L7 | Low | `CloudRealtimeClient.cs:58-66`** — 消息组装无大小上限，MemoryStream 无限追加直到 EndOfMessage；异常服务器或 ws:// 明文中间人可用超大帧耗尽内存（OOM 不在 catch 过滤内，循环死亡）。修复：设上限（如 1MB），超限断开按退避重连。

**C-L8 | Low | `CloudRealtimeClient.cs:31-38,72-77`** — 重连循环对未列举异常静默死亡（catch 仅 4 类），且 Stop 的 CTS 先 Cancel 立即 Dispose，循环线程在「检查未取消→Task.Delay 注册」间被抢占可能抛 ObjectDisposedException 逃出 RunAsync 成未观察异常，无日志，实时通道永久断开（靠 3 分钟轮询兜底）。修复：循环外层 catch-all 记录并继续退避；Stop 等 `_loop` 完成后再 Dispose CTS（或改 socket.Abort）。

**C-L9 | Low | `ProjectStore.cs:58-64,198-209`** — 清单无「最后完好副本」自动恢复；外部损坏/手改后 Load 抛异常进入保护模式无自动回退；此时用「仅设置」范围备份还原，BackupService.Restore 184/201 `store.Load()` 仍读损坏清单抛异常 → 恢复路径失败。修复：Save 成功后保留 projects.json.last-good；Restore 对 store.Load() 失败降级默认设置。

**C-L10 | Low | `ProjectStore.cs:71-77`** — Load 对绝对路径附件不做归属校验（相对路径有越界检查，`Path.IsPathRooted` 直接放行），被篡改/遗留清单可让渲染器读任意本地文件（需本机文件写权限）。修复：加载时对外部绝对路径重新收纳（OwnAttachments）或拒绝。

**C-L11 | Low | `ProjectStore.cs:316-318`** — .wth 导入大小校验信任 zip 头声明的未压缩长度（`Entries.Sum(e=>e.Length)` 可被伪造头部欺骗），实际解压可远超 512MB 填满磁盘。修复：解压时累计实际写出字节设硬上限。

**C-L12 | Low | `BackupService.cs:215,230-237`** — 自动备份文件名用本地时间（auto-yyyyMMddHHmmss），DST 回拨/时钟回退会重名覆盖或误删最新（Prune 按字典序）。修复：UTC 命名 + 冲突后缀，或同名跳过/递增。

**C-L13 | Low | `AppDataStore.cs:15-27`** — Load/Save（worktiles.json）为无调用者死代码，且 Save 用固定 `.tmp` 名（多实例互踩）；ParseColor（109-115）对畸形 hex 直接抛异常（当前上游有校验兜底）。修复：删死 API，或改唯一临时名并容错。

**C-L14 | Low | `MediaLibrary.cs:159-233`** — 自有媒体副本只增不减（历史列表截断 8 条但 data/backgrounds 文件从不删除；ImportWallpaperAsync 每次新 Guid 目录，同一网页项目反复导入成倍占盘）。修复：按引用计数/定期清理未被任何项目/设置/历史引用的自有副本（先确认无打开句柄）。

**C-L15 | Low | `MainWindow.Cloud.cs:68,74-80`** — `_screenReportDebounce` 未在 Closed 停止（Closed 停了 9 个计时器漏掉它，关窗后可能再发一次 PUT）；多处 async void 事件处理器（_boardRefreshDebounce/_screenReportDebounce Tick、CloudEditLock.cs:37-49 TryEnqueue(async…)）无兜底，UI 异常直接进程崩溃。修复：Closed 统一停表；async void 包 try/catch 记日志。

---

### 模块 4：桌面磁贴 / 壁纸 / 视频 / 工具栏岛

覆盖：`MainWindow.DesktopTiles.cs`(986)、`Islands.cs`(799)、`Media.cs`(210)、`Controls/DesktopTileWindow.cs`(1071)、`DesktopWidgetHost.cs`(867)、`WebWallpaperView.cs`(174)、`VideoFrameImage.cs`(121)、`BackgroundVisual.cs`(422)、`Services/DesktopTileLayout.cs`(277)、`WidgetLayout.cs`、`VideoPlacement.cs`、`BackgroundPlaylist.cs`、`WebWallpaperPackage.cs`、`WallpaperEngineLibrary.cs`、`IslandScroll.cs`。

> 实际情况校正：`MainWindow.Islands.cs` 是浮动工具栏岛（缩放岛/富文本岛/字体穿梭），非通知队列；`WebWallpaperPackage.cs` 无任何 zip 解包代码，壁纸导入走 `MediaLibrary.ImportWallpaperAsync→CopyDirectory`（拒绝 ReparsePoint、路径包含检查、Directory.Move 原子落盘），该路径安全。模块整体守卫质量较高（生命周期闭锁、token/epoch 防重入、画刷 OnDisconnected 释放、Win2D 共享设备、帧合并 Interlocked 均正确）。

#### Medium

**D-#1 | Medium | `MainWindow.DesktopTiles.cs:676-684,746-753,760-767,403-408`**
桌面磁贴持久化布局按「科目名」keyed，同名科目互相串位/覆盖。窗口字典已按对象身份 keyed（SubjectIdentity，969-975，注释明言「科目没有稳定 Id」），但持久化层仍按名字匹配：PlaceFreeTiles:676 `pool.FirstOrDefault(Name 相等)`；SaveFreePosition:760/SaveRelativePosition:746 同样 FirstOrDefault；改名事件 403-408 只改第一条同名 placement 后 break。AddSubjectCore:980 默认名「新科目」，同名是常态。影响：两个同名科目时拖动 B 覆盖 A 的保存位置（760 总命中第一条）；下次 PlaceFreeTiles A 拿到 B 坐标、B 落入 NextFreeSpot 新条目，磁贴互换/叠位；改名改错另一条 placement；OrderDesktopSubjects:429 按名取第一匹配，同名间拖动排序（FinishDesktopDrag:529 存名字）也是歧义空操作。
修复：给 SubjectBoard 增持久化稳定 Id，DesktopTilePlacement/DesktopTileOrder 改按 Id keyed，旧数据按名一次性迁移（同名按出现顺序对应）；短期至少把 403-408 改为按「该科目当前实际占用的那条 placement」定位，SaveFreePosition 写同一条。

**D-#2 | Medium | `Services/DesktopTileLayout.cs:23-67,140-146` + `MainWindow.DesktopTiles.cs:327-331,551-552`**
拖动插入引导每个指针增量做 n+1 次全量 Arrange，约 O(n⁵)，UI 线程随科目数恶化。Arrange 对第 i 块枚举 (i+1)² 候选（tops×rights，46-47）逐个 Overlaps（53，O(i)），单块 O(i³)、整轮 ≈ n⁴/4；InsertionGuide:140-146 对每个插入下标各跑一次完整 Arrange（n+1 次）→ 单次 ≈ n⁵/4。UpdateDesktopInsertionLine 在每次 DesktopDragDelta（327-331，60-125Hz）都调用并重建 slots。影响：n=12 约 6 万次矩形比较/增量（尚可）；n=20 约 90 万次/增量 → 每帧 10ms 级拖动卡顿；n=30 数百万次/增量拖动不可用，伴随每增量数百小对象分配。教室 9-12 科常见，超 15 科进入卡顿区。
修复：短期拖动开始缓存 sizes/work，InsertionGuide 节流合并（位移≥阈值且每帧至多一次，低优先级 DispatcherQueue 合并最新光标）；中期利用「插入点前缀排列与插入位置无关」做前缀增量复用，或换 skyline/MaxRects 装箱；tops/rights 的 Distinct 换预排序数组去重。

**D-#3 | Medium | `MainWindow.DesktopTiles.cs:515,766-767,686-690`（另 738,964）**
自由布局保存/回放不夹取到工作区，磁贴可被放到屏幕外且无法再拖回。FollowCursor（DesktopTileWindow.cs:210-222）拖动期间不限范围；FinishDesktopDrag 自由模式 515 直接 SaveFreePosition(moved.PixelBounds)，766-767 只换算不夹取；PlaceFreeTiles 686-690 按保存值原样回放也不夹取。影响：单监视器即可触发——拖到工作区外松手位置原样保存，下次任何 ArrangeDesktopTiles（改主题/缩放/增删/同步）后磁贴摆到工作区外部分或完全不可见，完全出屏后无法再抓到，只能切自动排列找回。
修复：SaveFreePosition/SaveRelativePosition 内把 X/Y 夹取到 [0, workWidth-tileWidth]/[0, workHeight-tileHeight]（负值归零），PlaceFreeTiles 再夹一次兜底；或 FollowCursor 期间即限制在工作区内。
关联疑似：DesktopWidgetHost.cs:684-743 WindowProc 无 WM_DPICHANGED 分支，TryGetDesktopWorkArea:870-879 恒用主窗口 DPI/工作区，拖到不同 DPI 副屏宿主不重缩放、内容按旧 DPI 栅格化。验证：100% 与 150% 双屏拖磁贴跨屏观察缩放是否跟随；修复方向为响应 WM_DPICHANGED 重算 SiteBridge，或把拖动夹取在主工作区（与上合并）。

**D-#4 | Medium | `Controls/DesktopWidgetHost.cs:347-351,711-723,337-341` + `DesktopTileWindow.cs:789` + `MainWindow.DesktopTiles.cs:944-945`**
缩放拖动/飞入飞出动画期间大量冗余原生调用（区域重建×2、DWM 属性×3、SiteBridge×2 每 tick）。SetRoundedRegion:347-351 无变更守卫，每次 CreateRoundRectRgn + SetWindowRgn(redraw:true) + ApplyDwmAttributes（481-495，含 DwmExtendFrameIntoClientArea、3 次 DwmSetWindowAttribute、EnumChildWindows）。MoveAndResize 尺寸变化路径已调 SiteBridge(339)+ApplyRoundedRegion(340)，SetWindowPos 同步触发的 WM_SIZE(711-723) 又做一遍。FollowDesktopResize 每指针 tick 再调 ApplyDesktopCorner(945)→SetRegionRadius（半径恒定）→第三次 DWM、第二次 Rgn。ApplyVisual(789) 在 320ms 飞行动画每个 16ms tick 对每段（最多 3 段）无条件 SetRegionRadius。影响：每次缩放 tick ≈ 2 次 SetWindowRgn 强制重绘 + 3 轮跨进程 DWM 写 + 3 次 EnumChildWindows + 2 次 SiteBridge；自动隐藏/显示动画 ≈ 20 tick×3 段同样开销，伴随闪烁风险。自动隐藏是教室高频路径。
修复：宿主记录「上次已应用的 (radius,width,height)」命中即返回；WM_SIZE 去掉与 MoveAndResize 重复的 SiteBridge/ApplyDwmAttributes；ApplyVisual 对 SetRegionRadius 按段缓存上次半径。

#### Low

**D-#5 | Low | `DesktopWidgetHost.cs:614-620`** — 全局 WinEvent 钩子范围过宽（0x0003–0x0016，idProcess=0/idThread=0 即所有进程），OnWinEvent:623-654 实际只处理 0x0016 与 0x0003，其余事件（菜单/滚动/拖放/Alt+Tab）白跑一趟 UI 线程派发。修复：拆两个 min==max 钩子（0x0003 与 0x0016），Dispose 时两个都 Unhook。

**D-#6 | Low | `DesktopWidgetHost.cs:656-662`（触发 636-651；注释 153,433）** — 点击空白桌面时 EnsureVisibleAboveDesktop 用 HWND_TOP 把磁贴抬到普通窗口之上，违背「普通窗口仍可覆盖在上方」语义。单击空白桌面使 Progman 成前台，未最小化的浏览器/播放器被磁贴盖住（可自愈瞬时错序，课堂可见）。修复：按交互态选锚 `_interactive ? HwndTop : HwndBottom`（非交互态 owner=Progman，HWND_BOTTOM 被系统夹在桌面之上、普通窗口之下，与 RestackDesktopGroup 一致）。备注：RestackDesktopGroup 经 MSDN 语义核对正确，非编辑分支命令栏确在磁贴之上，无 Z 序反转。

**D-#7 | Low | `DesktopWidgetHost.cs:278-287`（调用点 25,28,31,45,55,58,243,254,261,264,266,269,275,544,551,553,557）** — 调试探针 Trace 残留：无条件 Directory.CreateDirectory + File.AppendAllText 到 data/probe-trace.txt，catch 吞异常。每宿主创建约 7 条、每次 ApplyBackdrop 3-4 条，初始同步 n 块磁贴即上百次 UI 线程同步文件追加；probe-trace.txt 无限增长；发布版携带调试痕迹。修复：删除探针，或置 `#if DEBUG`/运行时开关后改缓冲异步写。

**D-#8 | Low | `Controls/VideoFrameImage.cs:100-106`（重建 80-84）** — CanvasImageSource 从不 Dispose（ReleaseSurfaces 只置空 _image.Source/_source，只 Dispose _frame），GPU/原生资源只靠终结器回收，连续换片/缩放重建期间原生占用累积。修复：`_source?.Dispose(); _source=null;`（先置空 _image.Source 再 Dispose）。

**D-#9 | Low | `Controls/DesktopTileWindow.cs:888-892`** — 圆角归零时 Composition 裁剪几何与 GeometricClip 弃置不 Dispose（radius<=0.5 分支 `visual.Clip=null; _clipGeometry=null;`，旧 CompositionRoundedRectangleGeometry/CreateGeometricClip 均未释放）。修复：置 null 前 `_clipGeometry?.Dispose()` 并保存/释放旧 GeometricClip。

**D-#10 | Low | `MainWindow.DesktopTiles.cs:703-710`** — NextFreeSpot 列满后只左移一次且不复查重叠（705-709 仅 x 左移一个 tile 宽 y=0 即返回，不再 Overlaps），新磁贴可能直接压在旧磁贴上。修复：左移做成循环（每移一列复查 Overlaps 直到不重叠或 x==0），或复用 Arrange 候选扫描取最右空位。

**D-#11 | Low | `Controls/WebWallpaperView.cs:38-53`** — WebView2 加固缺口：`AreDefaultScriptDialogsEnabled` 保持默认 true（第三方网页壁纸 JS 可弹 alert/confirm/beforeunload 模态框打断课堂）；`FrameNavigationStarting` 未挂（主框架白名单管不到 iframe，页面可用 iframe 加载任意远程源铺在桌面背景）。本地文件面已被虚拟主机映射+HostObjects 关闭收敛，故 Low。修复：`AreDefaultScriptDialogsEnabled=false`；挂 FrameNavigationStarting 同样 https+host 校验否则 Cancel（按需放行 about:blank/srcdoc）。

**D-#12 | Low（疑似设计缺口）| `DesktopWidgetHost.cs:96-99,548-550`** — Win10/旧 Win11 上「毛玻璃」静默退化为纯透明无提示。AcrylicDesktopBackdrop.OnTargetConnected 在 `!DesktopAcrylicController.IsSupported()` 时直接 return 不赋背景画刷；ApplyBackdrop:548-550 只按设置选 Acrylic 不预判支持性；应用最低支持 Win10 17763 而 DesktopAcrylicController 需 Win11 22H2+；设置页无按 OS gating（IsSupported 仅在两个背景类内部）。验证：Win10 21H2 实机开磁贴玻璃观察无模糊且无提示。修复：ApplyBackdrop 先判 IsSupported，不支持显式回退 TransparentDesktopBackdrop，设置页标注系统要求或禁用。

**D-#14 | Low（疑似）| `DesktopWidgetHost.cs:246-258,263-268` + `DesktopTileWindow.cs:63-84,148-156`** — Create 失败路径静默/半初始化：CreateWindowEx 返回 0 时 255 静默 return（EnsureShown 已置 _shown=true，后续 ShowSurface 因 Handle==0 返回，磁贴永不出现且无错误上报）；256-258 已注册 _instances/_activeHosts/钩子之后 263-268 XamlSource.Initialize/Content 抛异常时 window 未入字典不可达，但静态字典仍持有 → 1×1 窗口与钩子引用泄漏到进程结束。验证：调试版给 Initialize 注入失败观察句柄残留。修复：263-275 包 try/catch，失败 DestroyWindow + 移除字典（最后一个则 Unhook）后再抛/记；EnsureShown 在 Handle==0 时给可观测错误。

**D-#15 | Low | `Controls/BackgroundVisual.cs:210-219,266,288-293`** — BlockLiveWeb 期间无预览图的网页壁纸仍活体渲染；快照失败时永久停留，破坏其他表面玻璃采样。BlockLiveWeb=true 且无预览图时 211 `allowLive || preview is null` 仍创建活体 WebWallpaperView，266 使其留树，217-218 触发快照；288-290 等 Core 最多 5s 放弃、293 捕获失败也放弃，且 `_snapshotPath=path` 阻止重试 → _image 恒 null，活体网页永久留在 BlockLiveWeb 环境，玻璃表面采样到黑/错误背景且不自愈。修复：快照失败按退避重试；或 BlockLiveWeb 且无预览时不挂活体网页改挂纯色/占位层，待快照成功再切回。

> D-#13（项目操作失败回滚致媒体编辑器闭包指向孤儿对象）与 A-M2 同根因，见模块 2（A-M2 定 Medium）。

---

### 模块 5：磁贴控件 / 输入 / 自动填充 / 笔迹 / 字体

覆盖：`Controls/SubjectTileControl.cs`(1437)、`TransparentInputMarkers.cs`(952)、`AutofillInputs.cs`(599)、`AutofillPopup.cs`(416)、`AttachmentImageControl.cs`(559)、`AttachmentArea.cs`(75)、`ClockInkLayer.cs`(186)、`InkHit.cs`、`InkStrokeVisual.cs`、`InkWidthPresetButton.cs`、`ColorSwatchButton.cs`、`RecentImagesView.cs`(70)、`AlignmentGuides.cs`、`RichTextContentMetrics.cs`(18)、`Services/AutofillService.cs`(526)、`PinyinIndex.cs`(73)、`InkGeometry.cs`、`ImageCropGeometry.cs`、`FontService.cs`(256)。

#### High

**E-H1 | High | `Controls/SubjectTileControl.cs:300,341-342`；根因 `Controls/TransparentInputMarkers.cs:131-136,437-469`**
EnsureSlot 永久重排父级，使两处「直接子级找 RichEditBox」的查找在已加载磁贴上永远落空。每个作业编辑器创建时经 ApplyTransparentInputChrome→TransparentInputMarkers.Attach（SubjectTileControl.cs:737,452），Loaded 时 EnsureSlot 把编辑器从 row.Children 移进 _slot Grid（437-469：parent.Children.Remove(live)→_slot.Children.Add(live)→parent.Children.Insert(index,_slot)，全文件无 unwrap 还原路径）。此后 `row.Children.OfType<RichEditBox>()` 恒为空；而 ApplyBodyDefaults 还要求 `editor.IsLoaded`（:342）——已加载的编辑器必然已被包裹，两条件互斥。影响：① TileBodyStyleChanged（MainWindow.Settings.cs:1020-1028）改「正文默认字体/字号/加粗/斜体」时对所有看板与桌面磁贴调 ApplyBodyDefaults，实际一个编辑器都找不到 → 设置对已有未格式化正文静默失效（只有重建/新建编辑器经 CreateRichEditor.Loaded 的 ApplyDefaultBodyStyle 才拿到新默认值，掩盖问题）；② MeasureContentSize 富文本精确测量分支（:300-301，RichTextContentMetrics.Width+134）在已加载磁贴永远走不到，落入 else if 用 row.DesiredSize.Width+34 兜底 → 自动排列「按文字收紧磁贴」宽度估计失真，与设置预览不一致。
修复：两处查找改递归视觉树查找（复用现成 FindVisualEditors/FindHomeworkEditors，SubjectTileControl.cs:964-981，按 Tag is HomeworkEntry 过滤，镜子 Tag=MirrorTag 自动排除）；或让 EnsureSlot 在 row 上保留可识别标记。验证：改正文默认字体，观察看板已有未格式化条目是否立即换字体。

#### Medium

**E-M1 | Medium | `Controls/SubjectTileControl.cs:738-748`** — Paste 拦截是 async void 且 await 后无异常保护。`editor.Paste += async (_,args) => { args.Handled=true; … string text = await clipboard.GetTextAsync(); … }` 整个 lambda 无 try/catch；Clipboard.GetTextAsync() 在剪贴板延迟渲染源失效等场景抛异常，async void 未处理异常直接崩溃进程；且 args.Handled=true 在 await 前设置，失败时默认粘贴也已被吞。修复：包 try/catch 失败回退默认粘贴或提示；args.Handled 放到成功取到文本之后。（对照 :683 的 async 因被调方 MainWindow.xaml.cs:732-741 自带 try/catch 已覆盖，不需单独处理。）

**E-M2 | Medium | `Controls/SubjectTileControl.cs:1343-1363`** — 笔迹越界后当前笔段「死亡」，重新进入磁贴不再出墨。PointerMoved 越界置 `_activeStrokeData=null; _activeStrokeShape=null` 但保留 `_inkPointerId`；回界内后既不新建笔段也不续画（:1357 条件恒假）直到抬笔。注释（:1349）只说「避免跨空隙连线」，未实现「重新进入起新段」；对照橡皮擦路径（:1353-1356）越界返回后能继续擦，行为不一致。影响：书写画出磁贴边缘再回来后续笔画全部丢失，已落笔短笔段留在模型（:1333 落笔即入模型），用户看到「断墨」。修复：越界结束当前段；重新进入且仍按着时新建一段（复用 PointerPressed 建段逻辑），或至少续接但跳过越界点。验证：画一条穿过磁贴边界的线观察回界后是否出墨。

**E-M3 | Medium（疑似）| `Controls/AutofillInputs.cs:497-513`（对照学科侧 :311-318）** — 作业补全采纳后回读期望值，与学科侧已修复的输入法竞态不一致。`string expected = DocumentText();` 在 WriteText 之后回读；学科侧 :311-318 明确注释「期望值取候选名本身，不回读输入框：输入法可能在写入的同时把剩余的字接上去」并做写入后立即核对覆盖。RichEditBox 同样存在该竞态，若发生回读到的 expected 已被污染，ApplyGuard 之后忠实守护并补写错误文本，把「数学学」类重复固化进正文；作业侧也没有 :317-318 的即时覆盖兜底。验证：中文输入法在作业框输入拼音（组合未结束）时采纳候选观察是否重复字；或在 WriteText 与回读间注入一次 TextChanged 模拟竞态。修复：期望值按 previous 替换区间纯计算（start=caret-typed.Length，expected=previous[..start]+suggestion.Text+previous[caret..]）不回读，并补写入后即时核对。

**E-M4 | Medium | `Controls/AttachmentImageControl.cs:164`** — 图片解码只限宽不限高，极端长图内存失控。`DecodePixelWidth=Math.Clamp(框宽×raster×3,768,4096)`，高度随原图比例无上限；1:10 以上长截图（聊天记录/滚动截屏是作业常见素材）解码高度可达数万像素；最小 768 让小框图片被强制放大解码。单张长图可占数十至数百 MB（解码宽 1350×高 13500×4B≈73MB），多张叠加 OOM/卡顿。修复：同时设 DecodePixelHeight 上限（如 4096），或按 max(宽,高) 换算取小者；下限 768 按框尺寸放宽。

**E-M5 | Medium | `Controls/AttachmentImageControl.cs:431-441,498-513`** — ResetImage 不重置 `_imageBaseWidth`，复位后画面不再铺满取景框。base 在构造时（:129 ApplyTransforms）按恢复的 Scale 计算并缓存（`aspect*cover/Scale`），只在 ==0 时重算。若附件保存时 Scale=2 则 base=cover/2；点「复位」（工具栏 :119 可达）把 Scale 置 1 后画面宽=base=cover/2 只有取景框一半大四周露底。新图（Scale=1）不复现易漏测。另复位也不重置 Rotation（语义需产品确认）。修复：ResetImage 加 `_imageBaseWidth=0;` 让 EnsureImageBase 按 Scale=1 重算；明确 Rotation 是否归零。验证：放大图片→保存→重开→点复位。

**E-M6 | Medium（疑似）| `Services/FontService.cs:199-214`** — 字号 NumberBox 逐键钳制到 ≥8 并回写，正常输入被打断。ValueChanged 内 NormalizeSize 钳制 8–96，差值 >0.01 即回写 input.Value。WinUI NumberBox 在键入可解析数字时即触发 ValueChanged：输入「18」第一键「1」→Value=1→钳成 8 回写「8」，第二键追加成「88」；凡首位数字 <8 的字号（12–79 大多如此）都被打错。注释本意只是收敛小数。验证：聚焦字号框键入「18」观察最终值（若 NumberBox 仅在提交时求值则本条不成立）。修复：键入过程不做下限钳制（只处理 NaN/小数），LostFocus/Enter 提交时再 NormalizeSize。

**E-M7 | Medium | `Controls/SubjectTileControl.cs:623,625-707`** — Entries_CollectionChanged 直接全量重建无焦点保护（:623 `=> RebuildEntries()`）。ReloadFromModel 有 ContainsFocus() 守卫（:378），但模型集合变更事件绕过守卫直接重建所有编辑器；双视图（看板+桌面磁贴）是受支持场景。影响：另一视图增删作业时正在本视图输入/IME 组合中的编辑器被销毁，焦点丢失、未上屏组合文本丢失（FlushPendingText 只保已上屏文本）。修复：CollectionChanged 路径加与 ReloadFromModel 相同的焦点守卫，失焦后再重建；或增量增删行。

**E-M8 | Medium（疑似）| `Controls/RecentImagesView.cs:45`** — 最近图片用 `new Uri(path)` 加载，含 `%` 的合法文件名直接抛异常崩溃。普通图片无 .preview.png（MediaLibrary.cs:60-64 仅壁纸类有）走原始路径；NTFS 文件名可含 `%`（如「100%完成.png」），new Uri 把 `%` 当转义前缀，非法转义抛 UriFormatException；Refresh 由构造/Loaded/Changed 以 void 调用，异常未捕获 → 崩溃。含 `#` 的文件名被截断成 fragment 静默加载失败。历史列表最多 8 条经 File.Exists 过滤路径本身合法，问题只在 Uri 构造。验证：导入名为 `a%b.png` 的图片再打开图片选择器；或 `new Uri(@"D:\a%b.png")` 复现。修复：改 BitmapImage + StorageFile.GetFileFromPathAsync/SetSourceAsync（与 AttachmentImageControl.cs:158-165 一致），或 new Uri 前转义/包 try/catch 降级占位图。

#### Low

**E-L1 | Low | `SubjectTileControl.cs:528-541`** — ModelStamp 字段不全（只含 Name/AccentHex/InkStrokes.Count/条目文本与 RTF/附件 Path+Scale+Offset+Position），附件 180° 旋转且裁切偏移 0 时 stamp 不变 → 另一视图 ReloadFromModel（:378）跳过旋转不传播；方形图 ±90 同理；笔迹只记数量「擦一笔+画一笔」数量不变也不传播。修复：stamp 纳入 Rotation/FrameWidth/ViewportHeight；笔迹用增量版本号或内容摘要替代 Count。

**E-L2 | Low | `SubjectTileControl.cs:528-541,378,390`（触发 DesktopTiles.cs:187-201）** — ModelStamp 全量字符串开销，每次保存节拍对每块磁贴构建两次（长 RTF×多条目×多磁贴时每节拍拼接数 MB 字符串两次纯粹为比较相等）。修复：模型侧维护脏版本号/哈希，stamp 只比较版本；或缓存上次 stamp 哈希。

**E-L3 | Low | `SubjectTileControl.cs:1377-1393`、`ClockInkLayer.cs:155-171`、`InkHit.cs:9-27`** — 橡皮擦命中全量扫描无 break、按字典序取最后命中、逐次拷贝点集（每次 PointerMoved 对每条笔迹 InkHit.Contains，≤256 点 stackalloc 拷贝、>256 堆分配，命中不 break 最终擦字典序最后一个而非最上层/最近一笔，O(笔迹数×点数)）；ClockInkLayer.EndStroke（:146-153）空擦也触发 StrokesChanged 安排保存。备注：InkGeometry.cs:5-10 IReadOnlyList 重载生产无调用（仅 tests/ProjectLogic/Program.cs:104）内部整表拷贝可删。修复：先包围盒粗筛；命中即 break（或按 Z 序/距离选优）；InkHit 直接索引免拷贝；缓存包围盒。

**E-L4 | Low（疑似）| `SubjectTileControl.cs:1404`（对照 ClockInkLayer.cs:173-174 有 Math.Max(0.0001,…)）** — RenderStoredStrokes 除以 TipScale 无零守卫，项目 JSON/云载荷若含 0（损坏/手工构造）产生 Infinity/NaN 点，Polyline 渲染未定义。验证：手工把某笔迹 TipScaleX 置 0 后打开。修复：与 ClockInkLayer 统一走同一 ScaleOut 守卫函数。

**E-L5 | Low | `SubjectTileControl.cs:1357-1361`、`ClockInkLayer.cs:120-125`** — 书写无抽稀/合批，Polyline 点集随移动事件线性增长（高频指针长笔迹数千点，XAML Polyline 每次 Add 全量重渲染单笔画 O(n²)，橡皮擦命中随之变慢，项目 JSON 膨胀）。修复：最小距离阈值（如 0.75 DIP）或时间节流抽稀；GetIntermediatePoints 合批；长笔迹分段。

**E-L6 | Low | `SubjectTileControl.cs:1257-1271,1411-1417`** — 死代码 CreateIconToggle、UndoLastStroke（全库 grep 含 tests 仅定义无调用，「撤销上一笔」无 UI 入口，实际可用的是 ClearStrokes）。修复：删除；若撤销是规划功能接上入口或留 TODO。

**E-L7 | Low（疑似）| `AutofillInputs.cs:159-167`（常量 :48）** — ApplyGuard 首次补写后立即 Stop（`_restore(); Stop();`），MaximumRestores=2 形同虚设；若输入法在组合结束与失焦两时机各上屏一次，第二次覆盖不再被纠正。验证：构造两时机都重新上屏的输入法场景观察最终文本。修复：补写后不 Stop 继续守护到 MaximumRestores 或观察窗口结束；或删误导性常量。

**E-L8 | Low | `AutofillInputs.cs:248-256` + `SubjectTileControl.cs:207-213`** — 聚焦即清空「新科目」并写回模型，空名被持久化（GotFocus→ClearDefaultTitle，_suppress 只屏蔽补全自己的 handler；磁贴 TextChanged 只查 _applyingModel 照常把空串写进 _subject.Name 触发保存）。点一下新磁贴标题再点别处（不输入）科目名永久变空，列表/导出/补全都看到空名，失焦无占位恢复。修复：默认标题当占位符（失焦为空恢复「新科目」），或清空时同步抑制模型写入直到真正输入。

**E-L9 | Low | `AutofillPopup.cs:121-127,173-185`** — 快速路径不更新 _anchor/_caret（SameEntries 命中只 Place 不写字段），Reposition 用旧 _caret；候选列表不变而光标移动后滚动看板浮层贴回旧光标位置（MatchSubjects/MatchHomework 返回存储实例引用 AutofillService.cs:116-122,160-166，快速路径可达）。修复：快速路径同步 `_anchor=anchor; _caret=caret;`。

**E-L10 | Low | `AutofillPopup.cs:281-304`** — 候选行无 UIA 自动化信息（Border+Tapped，无 AutomationProperties.Name/Peer），屏幕阅读器无法感知候选列表与选中项（键盘操作本身可用 :209-233）。修复：行设 AutomationProperties.Name（候选文本+详情），或改 ListBoxItem/自定义 AutomationPeer。

**E-L11 | Low | `TransparentInputMarkers.cs:324-328,297-309,558-573,699-723`** — 选区罩层每键成本：Update 无条件 QueueFitCaret、FindCaret 递归全树启发式识别、CopyContent 整篇 GetText/SetText RTF + RebindBundledFont + CaptureFallbacks、标题逐字符 2 次 COM 矩形。长文档选中状态下输入明显变重（CollectRich 已按行取矩形 :765-787 正文侧尚可）。修复：缓存光标 Rectangle 引用（模板变化失效）；无选区跳过 FitCaret；dirty 同步只复制选区附近或增量更新。

**E-L12 | Low | `TransparentInputMarkers.cs:236-241`** — FitCaret 一次瞬时异常即永久禁用该编辑器光标裁切（`_caretClipBroken=true` 无恢复路径），偶发 COM 异常后光标永久恢复「行盒高」外观直到重建，静默降级无日志。修复：改计数/退避重试，或下次 Loaded 复位标志。

**E-L13 | Low | `TransparentInputMarkers.cs:834-850,896-901`** — 系统选区高亮被永久置透明（HideSystemHighlight 只置透明 _highlightHidden 不复位，Hide 不恢复画刷），标题选中文本后焦点移走（KeepSelectionVisible 为 null 时罩层隐藏）选区既无系统高亮也无罩层 → 不可见，此时重新聚焦键入会替换「看不见的选区」意外丢字。修复：Hide 时恢复默认 SelectionHighlightColor（至少 WhenNotFocused），或保持罩层在失焦选区上可见。

**E-L14 | Low | `AutofillService.cs:493-504`、`PinyinIndex.cs:29-48`、`AutofillInputs.cs:574-581`** — 补全匹配每键对每条候选展开拼音无缓存（Score 内 _pinyin.Expand(name)，NormalizePinyinQuery(query) 每条重复算；每次两个 StringBuilder+ToLower），词库上限 300 条+学科条目每键数百次字符串构建；CurrentInput 每事件读全前缀文本。修复：按 name 缓存 (full,initials)（改名失效）；query 规范化提循环外；CurrentInput 只读光标附近窗口。

**E-L15 | Low | `PinyinIndex.cs:17,50-72`** — 拼音索引单读音（Dictionary<char,string> 一字一音）+ 缺文件永久静默降级（文件缺失/IO 异常直接返回空索引无日志无重试），多音字（长/重）只能按一个读音匹配，资源缺失时拼音补全永久退化为汉字匹配无提示。修复：数据格式支持多读音（或常见多音字双码）；加载失败记日志并在设置页提示。

**E-L16 | Low | `AutofillService.cs:449-450`** — IsHan 漏掉扩展 B 区及以后汉字（代理对）与「〇」（仅 \u3400–\u9FFF、\uF900–\uFAFF），姓名/地名用扩展区汉字时 MinimumInput 当非汉字（:507-512 需 2 字符才触发）、NormalizeToken hasHan 判定失效（:419-437）、IsRecordable 走字母规则可能拒收（:343-365）。修复：按 Rune 枚举判断 CJK 区间（含 \u20000+ 与 \u3007）。

**E-L17 | Low | `FontService.cs:49,55-61`** — InstalledCache 无界，键来自外部 RTF 字体名，每个未命中名字触发一次 GDI 枚举并永久缓存，构造含海量不同字体名的项目可撑大缓存（句柄本身无泄漏：:66-78,221-242 均 finally DeleteDC，偏移 28 正确，GC.KeepAlive 到位）。修复：缓存设上限（LRU 1024）；明显非法名字先做长度/字符校验。

**E-L18 | Low | `AttachmentImageControl.cs:182-193,180,184`** — 图片加载失败提示永久残留（catch 插入错误 TextBlock 无移除逻辑，Loaded 重入重复插入），ImageReady 一次性闩锁（_imageReady.TrySetResult 先 false 后 true 无效），同一实例再次加载成功错误提示仍盖在图上，导出侧 ExportTileVisual.PrepareAsync 等待 ImageReady 拿到首次失败值。修复：成功路径移除错误提示；重试前重置 TCS 或改可复位状态。

**E-L19 | Low | `Controls/AttachmentArea.cs:39-53`** — 测量高度含工具栏（bottom 用 image.DesiredSize.Height），选中图片工具栏可见时 DesiredSize 增高约 40px，区内后续图片靠 SlotTop 不跳但区域总高变化使其后作业条目在选中/取消时上下跳动，与 :15 注释意图部分矛盾。修复：bottom 改 FrameHeight+工具栏预留或统一按 SlotTop 累计，工具栏以 overlay 呈现。

**E-L20 | Low | `RecentImagesView.cs:29-31,38`** — 构造+Loaded 双重 Refresh，且每项标题读盘反序列化（DisplayTitle→MediaLibrary.ReadLabels 每次 File.ReadAllText+反序列化，MediaLibrary.cs:46-57,271-275），每次打开/库变更重建两遍，8 项×每项一次磁盘读+JSON 解析。修复：去掉 ctor Refresh（Loaded 会做）；ReadLabels 加带失效通知的缓存。

**E-L21 | Low | `ClockInkLayer.cs:50-61`** — 退出书写不调 EndStroke（注释称「退出书写时结束当前笔段」，实现只置空字段不触发 StrokesChanged），书写中途退出墨水模式最后一段笔迹已在模型（:112 落笔即入列表）但宿主收不到保存通知，若随后无其他保存触发点即关应用该段丢失。修复：`if (!drawing) { EndStroke(); … }` 复用收笔逻辑。

**E-L22 | Low（疑似）| `RichTextContentMetrics.cs:15`** — Width 的 GetRect 无异常保护，调用方 MeasureContentSize 可能在编辑器尚未完成布局时执行（自动排列/预览刷新时序），未加载文档的 GetRect 行为依赖 TOM 实现可能抛 COM 异常。验证：项目加载后立即触发自动排列观察是否抛异常。修复：包 try/catch 返回 0，与同模块 TryRect（TransparentInputMarkers.cs:820-832）防御风格一致。

---

### 模块 6：导出 / 背景画刷 / 噪音 / 天气 / 杂项服务

覆盖（35 文件）：`Controls/ExportImageView.cs`、`ExportTileVisual.cs`、`ExportBackdrop.cs`、`BorderlessPopupHost.cs`、`BlurBackdropBrush.cs`、`PersistentMicaBackdrop.cs`、`SurfaceBackground.cs`、`FluentIcon.cs`、`Themes/ThemeResources.xaml`、`Models/BoardModels.cs`、`InkToolSettings.cs`、`Services/BoardLayout.cs`、`BoardTheme.cs`、`BoardNotices.cs`、`ColorPalette.cs`、`DefaultContentColors.cs`、`GridAppearance.cs`、`LayoutMetrics.cs`、`ExportLayout.cs`、`HoverTransition.cs`、`ToolbarAutoHidePolicy.cs`、`ToolbarChrome.cs`、`MenuBarPolicy.cs`、`SettingsCopy.cs`、`ClockFormat.cs`、`ExamSchedule.cs`、`ExamScheduleStore.cs`、`LockService.cs`、`NoiseMonitorService.cs`、`NoiseAlertGate.cs`、`NoiseAlertPlayer.cs`、`NoiseAlertTone.cs`、`QrCodeImage.cs`、`XiaomiWeatherService.cs`、`WeatherCityCatalog.cs`。

#### Medium

**F-#1 | Medium（疑似崩溃）| `Controls/ExportImageView.cs:348-353`** — ResizeSelected 未防护「标题高于画布」，产生负缩放/负尺寸。_titleSize 最大 200（:29）、画布高度最小 256（:41）、标题可 200 字符换行，_titleHeight（:235）可超过 _height.Value；:348 `maximum=Math.Min(_width.Value/source.Width, (_height.Value-_titleHeight)/source.Height)` 为负，:349 `tile.Scale=Math.Min(Math.Max(.001,_scale.Value/100), maximum)` 取负，:350 Width/Height 随之为负，:367 写入负 Width。自动排列路径对同一条件已有守卫（ExportLayout.cs:19-20 抛友好异常，ExportImageView.cs:297 捕获），但 _scale.ValueChanged→ResizeSelected（:153）完全没有。影响：画布调小/标题调大后选中磁贴改缩放进入负缩放（磁贴翻转不可见、保存被禁用）；若 WinUI 对负 Width 抛 ArgumentException，该异常在 ValueChanged 无 catch 直接崩溃。验证：高度 256+标题字号 200+长标题→自动排列（报「标题过高」）→点击旧句柄→改缩放值观察是否崩溃；确认崩溃则升级 High。修复：ResizeSelected 开头加 `if (_titleHeight >= _height.Value) return;`（或 maximum 钳到 ≥0.001 并给与 ExportLayout 一致的状态提示）。

**F-#2 | Medium | `Controls/ExportImageView.cs:40-41,418-422`** — 允许最大 8192px，但 WinUI 3 RenderTargetBitmap 每边上限 4096，大尺寸导出必然失败且先白白消耗 GB 级内存。宽高 NumberBox Maximum=8192；微软工程师 2026-11-06 在 microsoft-ui-xaml#8733 确认 WinUI 3「bitmap is capped at 4096 pixels per side」；:420 请求渲染后 :421-422 守卫把 4096 上限转成友好报错——>4096 导出在典型设备永远无法成功。失败前 :264-265 已按全尺寸跑完 Win2D 背景管线（ExportBackdrop.cs:17/34 两块 CanvasRenderTarget + :31/36 两张 WriteableBitmap + :43 GetPixelBytes 托管拷贝，8192×8192×4B≈256MB/块，峰值约 1.3GB）。影响：UI 明示支持到 8192，用户选大尺寸等待长时间背景生成、占大量内存最终只得「请降低图片尺寸后重试」，低内存设备可能先 OOM。修复：Maximum 降到 4096（或启动探测实际上限并 UI 标注）；确需 >4096 分块 RenderAsync 拼接或改 Win2D 离屏合成整条管线；ExportBackdrop.ToBitmap（:39-46）改 GetPixelBytes(SoftwareBitmap)/直接流拷贝减少一次 256MB 中转。验证：100% 缩放选 8192×4608 导出观察 :422 报错文本实际返回尺寸（预期 4096×2304 左右）。

**F-#3 | Medium（疑似）| `Controls/ExportImageView.cs:419-422`** — 高 DPI 下对 RenderAsync 带尺寸重载的输出语义假设未经验证，>100% 缩放可能全部导出失败。画布逻辑尺寸即目标像素数（:217-218），代码假设「输出物理像素=请求逻辑尺寸×RasterizationScale」故 :420 传 width/dpiScale；但 UWP 文档语义是 scaledWidth/scaledHeight 直接决定输出位图尺寸（PixelWidth==scaledWidth）。若 WinUI 3 1.8 带尺寸重载仍输出「恰好 scaledWidth×scaledHeight 物理像素」，则 150% 缩放下请求 1280×720 只得 1280×720，:421 守卫对任何尺寸都抛错——高 DPI 用户导出整体不可用（友好报错不崩溃）；反之若输出=请求×缩放则现写法正确。静态审查无法裁决，现有 UI 测试（tests/UiVerification.cs:1332）只断言不抛异常未断言输出尺寸，100% 缩放机器两种语义都通过。验证：150% 缩放机器导出 1920×1080 记录 :420 后 bitmap.PixelWidth/PixelHeight；确认后去掉除法直接传 width/height，或按实测比例重渲染一次；并在 UiVerification 增「输出 PNG 尺寸==请求尺寸」断言覆盖高 DPI。若证实失败面为全体高 DPI 用户升级 High。

**F-#4 | Medium | `Controls/ExportTileVisual.cs:149-192`** — 颜色/模糊拖动期间每次事件全量重建所有磁贴 RTF 与笔迹无节流。picker.ColorChanged（ExportImageView.cs:102）与 blur.ValueChanged（:125）拖动中高频触发 RefreshAppearance，:246 对每个磁贴 SetAppearance：对所有 RichEditBox 重新 Document.SetText 整段 RTF（:161-175）+ 字体重绑（:173）+ 清空重建全部 ink Polyline（:179-191 逐点 Add）。背景生成有 100ms 防抖（ExportImageView.cs:262）但 SetAppearance 这条最重路径无任何防抖/增量判断。磁贴多笔迹多时拖颜色/模糊明显卡顿。修复：SetAppearance 加与背景相同防抖（拖动结束再全量）；或拆分增量——背景色变但明暗判定（:152 阈值 145）未翻转时只更新 Foreground/画刷不重载 RTF/不重建 Polyline，明暗翻转或色系切换才全量。

> F-#5（TOTP 密钥明文存储）与 C-M8 重复，见模块 3（已修正存储路径为 projects.json）。

#### Low

**F-#6 | Low | `Services/ColorPalette.cs:18-20`** — 新自定义色板非法时保留旧值，与注释承诺「按鲜明色系显示」矛盾（Use 仅在新 custom 合法时覆盖 _custom，非法不清空；IsCustom 只查 Length==Vivid.Length，「上次合法+本次非法」仍按旧自定义色显示）。修复：`else _custom=[];` 非法即清空使 IsCustom 自然回退。

**F-#7 | Low（疑似）| `Services/ColorPalette.cs:18` ↔ `ViewModels/MainViewModel.cs:105-111`** — 接受 9 字符颜色但不校验十六进制，BrushFromHex 按 RGB 前 6 位解析导致 #AARRGGBB 偏色。:18 只查长度 7/9 与首字符 '#' 不查 hex 合法性；9 字符自定义强调色经 Resolve 流入 BrushFromHex，后者恒 A=255 取前 6 位当 RGB（AA RR GG 被当 RGB）。本地 UI 写自定义色为 7 字符（MainWindow.xaml.cs:1138），GridAppearance 的 ARGB 背景走另一条自洽解析（GridAppearance.ParseColor 按 #AARRGGBB），故仅云端下发/手工编辑 JSON 可触发，磁贴强调色与网格色对同一 8 位值呈现不同颜色。验证：手工把某学科 AccentHex 改「#80FF0000」置 IsAccentExplicit=true 观察颜色。修复：ColorPalette.Use 校验全 hex 且仅接受 7 字符（或统一转 7）；或让 BrushFromHex 支持 8 位 ARGB 与 GridAppearance.ParseColor 对齐，非法长度回退默认色。（亦见模块 1 B-L14）

**F-#8 | Low | `Models/BoardModels.cs:124-127`** — SupplementSummary 把所有附件计为「张图片」含 PDF/文件类（`details.Add($"{Attachments.Count} 张图片")` 不区分 Kind）。作业含非图片附件时摘要文案错误（「2 张图片」实为 1 图+1 PDF）。修复：按 Kind 分组计数分别输出「N 张图片 / M 个文件」。

**F-#9 | Low | `Services/HoverTransition.cs:53-66`** — 等待中元素提前剥离成功时不清理 Waiting/LayoutUpdated，静态强引用滞留（:53 `if (TryStrip(element) || !Waiting.Add(element)) return;` 元素已在 Waiting 而本次 TryStrip 成功直接 return，既不移除 Waiting 也不退订 :61 挂上的 OnLayout；:29-31 每次 Suppress 置 true 都新挂 Loaded/ActualThemeChanged 匿名处理器反复切换累积）。缓存型弹窗/长寿命树按钮被静态 HashSet（:49）永久根住。修复：成功路径统一清理（ConditionalWeakTable 记录 OnLayout，任何 TryStrip 成功处退订并 Waiting.Remove）；Waiting 改弱引用；OnSuppressChanged 先判是否已订阅。

**F-#10 | Low（疑似）| `Services/NoiseMonitorService.cs:90-96`** — BitsPerSample<8 时 bytesPerSample=0，采集回调在锁内死循环（:90 `format.BitsPerSample/8` 无下限防护，为 0 时 :96 for 循环永不推进且整段在 _gate 锁内 :94，Stop()/TryCalibrate 随之卡死 UI）。WASAPI 共享模式混音格式恒 16/24/32 位或 float，正常设备不可达，属防御缺口。验证：Start 成功后日志输出 format.BitsPerSample/Encoding 枚举本机输入设备确认无 <8 位。修复：循环前加 `if (bytesPerSample<=0) return;`（或触发 CaptureFailed）。

**F-#11 | Low | `Services/QrCodeImage.cs:13,15`** — QRCoder 1.7.0 的 QRCodeGenerator/PngByteQRCode 均 IDisposable 但未释放（仅 :14 QRCodeData 有 using），当前无未托管资源仅卫生问题，依赖升级后若引入资源即成泄漏。修复：`using QRCodeGenerator generator=new(); using PngByteQRCode code=new(data);`。

**F-#12 | Low | `Services/WeatherCityCatalog.cs:17`** — `_cities ??= await LoadAsync()` 非原子，并发搜索重复加载（连续键入并发 SearchAsync 同时见 _cities==null 各自读文件+反序列化整份城市表后写覆盖先写，结果等价无损坏但重复 IO/CPU）。修复：改 Lazy<Task<…>> 或 SemaphoreSlim(1,1) 包裹初始化。

**F-#13 | Low | `Services/XiaomiWeatherService.cs:29-31`** — current/temperature/value 用 GetProperty 硬取，畸形响应抛 KeyNotFoundException 且英文异常直达状态栏（alerts 分支 :36-46 反而做了防御；调用方 RefreshWeatherAsync catch（MainWindow.xaml.cs:1431-1437）把 exception.Message 原样显示，用户看到「The given key 'current' was not present…」）。接口改版/限流返回错误 JSON 时不崩溃但状态栏英文技术报错。修复：改 TryGetProperty 链，缺失抛 InvalidDataException("小米天气响应缺少当前天气数据。") 中文友好消息。附注：Endpoint HTTPS（:12）；appKey/sign 为小米市场接口公开常量非用户私有密钥不构成泄密；HttpClient 单例；response/stream/JsonDocument 均释放；温度解析 InvariantCulture。

**F-#14 | Low | `Services/LockService.cs:68-80`** — VerifyTotp 无重放保护，同一验证码在 ±1 步窗口内可重复使用（:76-78 对 step-1..step+1 逐一比对接受后不记录已用步，同一 6 位码约 90 秒窗口内可多次通过；:78 用字符串 == 非固定时间比较，本地 6 位码风险可忽略）。肩窥/截获一次验证码后可在窗口期重放解锁，对课堂锁屏低危。修复：LockSettings（或内存）记录最近接受 step，拒绝 step<=lastAcceptedStep；比较换 CryptographicOperations.FixedTimeEquals（ASCII 字节）。验证：SettingsLogic 用同一 (secret,code,now) 连调两次 VerifyTotp 当前均 true。

**F-#15 | Low | `Services/ExamSchedule.cs:42-54`** — 朴素墙钟 AddMinutes：DST 时区回拨重复小时会二次评估同一考试段（dayStart Kind=Unspecified + AddMinutes 纯墙钟，调用方传 DateTime.Now 同为墙钟自洽；中国无 DST 国内无影响；有 DST 时区秋季回拨日 01:00-02:00 考试段被评估两遍倒计时中途重置）。跨午夜 EndMinute≤1440 段不能跨天、凌晨开考 EnterMinutesBefore 窗口被午夜截断为 :27-29 注释明示设计取舍非缺陷。修复（如需支持 DST）：Evaluate 改 DateTimeOffset+明确时区计算段边界，或文档声明「考试时刻按墙钟解释，DST 回拨小时行为未定义」。验证：单测构造 TZ=America/New_York、2026-11-01 01:30 段模拟回拨前后两个 now。

**F-#16 | Low | `Services/ClockFormat.cs:16`** — 「HH:mm:ss」/「HH:mm」使用当前文化的时间分隔符（':' 是文化敏感占位符随系统区域替换，如芬兰语为 '.'），非中文/英文区域教室时钟显示「12.34.56」（纯外观）。修复：`time.ToString("HH':'mm':'ss", CultureInfo.InvariantCulture)`。

**F-#17 | Low | `Controls/BorderlessPopupHost.cs:179,80-89`** — RegisterClassEx 返回值未检查（失败返回 0 不报错，后续 CreateWindowEx 失败 :79 静默 return 托盘菜单不出现无提示）；hwnd 创建后 XAML 初始化异常无回滚（:80 已注册进静态 Instances 后 :83-88 Initialize/Content/SystemBackdrop 抛异常 Create 无 try/catch，唯一调用方 TrayMenuWindow.cs:155 同样无 catch，Session 构造抛出 → _current 未赋值 → 已创建 HWND/XamlSource 无人 Dispose 泄漏，托盘点击事件未处理异常可致崩溃）。修复：atom==0 抛带 Win32 错误异常；Create 中 hwnd 成功后段落包 try/catch 失败 Instances.Remove+DestroyWindow 再抛；TrayMenuWindow 构造/Show catch 降级。

**F-#18 | Low | `Services/SettingsCopy.cs:28-31,39`** — IList 分支共享源元素实例（浅拷贝）；递归 Into 无 visited 集合对象图有环即 StackOverflow；Into 按 target.GetType() 取属性却对 source 调 GetValue，运行时类型不同抛 ArgumentException。已核实当前唯一调用方 CloudSyncService.cs:626 合并后即丢弃源对象、设置为无环 POCO 树，现网无实际影响，属通用工具隐性契约缺口。修复：XML 注释明确「源对象复制后不得再使用/不得含环」；或 IList 对可克隆元素深拷贝；递归加 HashSet<object> visited 防环。

**F-#19 | Low | `Controls/ExportImageView.cs:381-382,415-416`** — 保存期间左侧选项仍可交互（SaveAsync 只禁 _save 与句柄命中 :382，宽/高/标题/色系在 RenderToFileAsync 多个 await 期间仍可改，改宽高触发 SetDimensions+AutoArrange :217-219 与正在进行的渲染竞争产出与所选参数不一致的图片；:406 只 await 当时的 _appearanceTask 其后新触发的背景刷新不被等待）；:415 TryEnqueue 返回 false（调度器已关闭）时 laidOut 永不完成 await 悬挂（仅退出路径）。修复：_saving 期间置 optionsScroll.IsEnabled=false（finally 恢复）；检查 TryEnqueue 返回值 false 时抛 InvalidOperationException 走 :394 catch。

**F-#20 | Low | `Controls/BlurBackdropBrush.cs:9-78,84-152`** — BlurBackdropBrush 与 DesktopGlassBrush 近乎逐行重复（仅 BorderMode 一处不同 :45 Hard vs :120 Soft，OnConnected/OnDisconnected、效果链、Fallback 全重复，各自释放均正确无泄漏），修一处漏一处的典型温床。修复：抽公共基类或合并为带 EffectBorderMode 构造参数的类，两个名字保留薄派生/工厂。

---

### 模块 7：更新链 / 共享库 / 构建与 CI / 脚本

覆盖：`Services/GitHubUpdateService.cs`(77)、`ReleaseUpdateService.cs`(122)、`PortableUpdateService.cs`(81)、`SplitUpdatePackage.cs`(56)、`ApplyUpdate.ps1`(58)、`installer/Optimize-Publish.ps1`、`.github/workflows/release.yml`(112)、`src/WorkTiles.Shared/Contracts.cs`(173)、`SettingsSchema.cs`(412)、`RichText/RtfCodec.cs`(403)、`WorkTiles.Shared.csproj`、`WorkTiles.csproj`(56)、`WorkTiles.slnx`、`Properties/launchSettings.json`、`.gitignore`、8 个 `.tmp-*.py`、`MainWindow.Updates.cs`。

#### Critical

**G-1 | Critical | `src/WorkTiles/MainWindow.xaml.cs:1573`（配合 `:224-229,:73`）；失败表现 `ApplyUpdate.ps1:14-15`**
便携版自动更新整体失效：更新路径的 `Close()` 被「关闭即隐藏」拦截，进程永不退出。更新确认后执行 `PersistProjects(); PortableUpdateService.Launch(configuration); Close();`，但没有设置 `_allowClose=true`。InitializeAppWindow 注册的 AppWindow.Closing 处理器在 `_allowClose==false` 时 `args.Cancel=true; _appWindow.Hide();`。全库只有三处设置 _allowClose（托盘退出 xaml.cs:1242、单实例退出请求 :237、数据还原后重启 Data.cs:360），更新路径不在其中；代码库自身在其它所有编程式 Close() 前都置位该标志，证明编程式关闭同样触发可取消的 Closing。影响：主窗口只是被隐藏，进程继续存活（托盘/桌面磁贴）；ApplyUpdate.ps1:15 WaitForExit(60000) 超时 → 抛错 → 写 UPDATE_FAILED → $canRestart 仍 false → 不替换任何文件、不重启。用户看到窗口消失以为在更新，实际应用藏在托盘运行旧版本；每次检查更新重复「下载→隐藏→60 秒后静默失败」。tests/UpdateLogic 用 int.MaxValue 作父 PID 并 -SkipRestart，只测脚本不测应用退出，抓不到此回归（疑似由近期托盘/隐藏式关闭改动引入）。
修复：在 :1572 Launch 成功后、Close() 前加 `_allowClose=true;`（与托盘退出一致）；更稳妥让脚本先写「已就绪」标记，应用确认脚本启动后再退出，退出前停噪音监测/云同步等后台活动；补端到端测试：真实启动应用→触发更新→断言进程退出且文件被替换。（亦见模块 1 B-H1）

#### High

**G-2 | High | `Services/ReleaseUpdateService.cs:102-107`；`Services/GitHubUpdateService.cs:71`（配合 MainWindow.xaml.cs:1575、ReleaseUpdateService.cs:56 允许 .exe/.msix）；`.github/workflows/release.yml:80-112`**
「下载→校验→执行」全链路缺乏独立来源认证：哈希校验可选、Gitee 源从不校验、安装器路径无签名验证、CI 不签名。全链路唯一完整性手段是 GitHub API 资产自带 digest 字段且为可选（`part.Digest?.StartsWith("sha256:")==true` 才校验）；Gitee v5 API 不提供 digest → 选 Gitee 源时下载内容零校验；哈希与下载 URL 来自同一 API 响应只能防传输损坏不能防仓库/账号沦陷；非便携资产（.exe/.msix）经 LaunchInstaller 直接 UseShellExecute 执行无 Authenticode/签名检查；CI 只产出 zip 不签名不发布签名 SHA256SUMS。影响：维护者账号或 Release 资产被篡改（供应链攻击）时攻击者替换 zip/exe 即可在每台点「立即更新」的教室大屏执行任意代码（自动检查更新默认开启 xaml.cs:146，仅需一次用户点击），当前无签名体系下 TLS 是唯一防线。
修复：① CI 用 Authenticode 签 WorkTiles.exe 并对 zip 生成 detached 签名（minisign/signify），客户端内置固定公钥在 Prepare 前强制验签；② 过渡：GitHub 源强制要求 digest 存在（缺失即拒绝），CI 额外发布签名校验文件；③ LaunchInstaller 前用 WinVerifyTrust 验证 Authenticode 且颁发者固定否则拒绝；④ Gitee 源无法验签前至少提示风险或降级仅提示不自动安装。

**G-A | High | `Services/ReleaseUpdateService.cs:52`（独立发现，实测验证）**
已发布更新资产名 `Pancake-*` 被 `WorkTiles` 过滤器排除，双源更新发现已失效。:52 资产过滤 `if (!name.Contains("WorkTiles", OrdinalIgnoreCase) || …) continue;` 要求名称含「WorkTiles」。实测（2026-11-18）：GitHub 最新 Release 为「Pancake v2.1.2」，资产 `Pancake-win-x64-2.1.2.zip`（带 digest）；Gitee 最新 v2.0.7，资产 `Pancake-win-x64-2.0.7.zip`（另有源码包 v2.0.7.zip/tar.gz）。两者均不含「WorkTiles」→ 全部被排除 → ParseRelease 找不到可安装资产 → 返回空更新（:62 `new(version,tag,notes,null)`）。影响：针对当前已发布的 Release，客户端在 GitHub 与 Gitee 两源都发现不了任何更新，自动更新形同虚设；与 G-1 叠加，更新链「发现」与「应用」两端同时断裂。注：release.yml:85 现已把未来产物命名为 `WorkTiles-win-x64-{version}.zip`，故下一次以新命名发布的 Release 可恢复发现（但 G-1 仍会阻断应用）；Gitee 仓库路径仍为 EdgeHH/Pancake（:27）。
修复：过滤器同时接受历史命名（如 `name.Contains("WorkTiles") || name.Contains("Pancake")`），或重新发布以 WorkTiles 命名的资产/在 Release 中补一份 WorkTiles 命名副本；统一仓库与产物命名后移除兼容分支；补一条「ParseRelease 对真实 Release 报文能选出资产」的回归测试。

**G-B | High | `src/WorkTiles/WorkTiles.csproj:19,36,37`（独立发现，git 实测验证）**
csproj 引用的 `Assets\AppIcon.ico` 与 `Assets\Logo\logo.png` 未被 git 跟踪，全新克隆构建失败。:19 `<ApplicationIcon>Assets\AppIcon.ico</ApplicationIcon>`、:36 Content logo.png、:37 Content AppIcon.ico。实测 `git status --porcelain` 显示 `?? src/WorkTiles/Assets/AppIcon.ico`、`?? src/WorkTiles/Assets/Logo/logo.png`（均未跟踪、未被 .gitignore 忽略），而其余 Content 资产（Fonts/Pinyin/Settings/xiaomi_weather_cities.json/Tools/7zip 含 7za.exe）与 ApplyUpdate.ps1 均已跟踪。两文件存在于本机磁盘（近期修改）但不在版本库。影响：全新克隆缺少这两个文件 → ApplicationIcon 嵌入失败（构建错误）+ Content 复制失败，CI/新开发者无法从干净仓库构建；本机因文件存在而正常，掩盖问题。
修复：把 `src/WorkTiles/Assets/AppIcon.ico` 与 `src/WorkTiles/Assets/Logo/logo.png` 纳入版本控制（git add 并提交）；在 CI 增加「干净克隆构建」冒烟以捕获此类遗漏。

#### Medium

**G-3 | Medium | `src/WorkTiles.Shared/SettingsSchema.cs:356,359`；消费方 `Services/CloudPayload.cs:187-191`、`BackgroundPlaylist.cs:14`、`Controls/BackgroundVisual.cs:385`**
同步的背景路径放行 UNC：File.Exists 探测 + 图片加载触发 SMB 外联，可致 NetNTLM 凭据哈希泄露。schema 注释声称「路径只接受 asset: 引用或本机路径文本」，但 ImagePath 仅 Text(n,1024)、Playlist 同，不做 schemes/UNC 限制；班级设置由网页/老师端下发（ApplyLayoutAsync→LayoutSync.Merge），恶意值 `\\attacker.tld\share\wall.png` 先被 CloudPayload.Resolve（:190 `File.Exists(value)||Directory.Exists(value)`）探测——即使文件不存在 Windows 也会向攻击者主机发起 SMB 认证；若攻击者挂出真实 PNG 则路径被应用为背景并经 BitmapImage(new Uri(path)) 再次 SMB 拉取。影响：拥有班级设置写权限者（被盗老师账号/恶意网页端/沦陷服务端）可收集大屏机器账号 NetNTLM 哈希（离线爆破/中继），并探测、把设备上任意已存在本地图片渲染到教室大屏（信息泄露）；http(s) URL 被 File.Exists 挡下，UNC 是主要向量。修复：SettingsSchema.Background 的 ImagePath/Playlist 清洗拒绝 `\\`/`//` 开头及含 `:` 的非盘符路径，只允许 asset: 引用或本机绝对路径（最好限定媒体/用户目录白名单）；CloudPayload.Resolve 对非 asset 引用同样拒绝 UNC。验证：测试服务端把 ImagePath 设 UNC，抓包观察大屏是否发起 SMB 会话。

**G-4 | Medium | `Services/ReleaseUpdateService.cs:86,106`；`SplitUpdatePackage.cs:29-38`；`PortableUpdateService.cs:13-15`；`MainWindow.xaml.cs:146,1557,1564`**
更新下载/staging 残留无限累积无任何清理。每次检查新建 `updates\{version}-{GUID}` 目录（:86），校验失败抛异常但不删已下载分卷（:106）；解压目录+NoCompression 重打包 zip 均保留；payload/backup 保留；启动即自动检查、先下载后询问，用户点「稍后」包就留下。全库 grep 无删除 updates 旧目录代码。一次完整更新链峰值 ≈ 分卷+合并包+解压目录+无压缩 zip+payload+backup（约 5–6 倍包体积），失败/取消/拒绝后全部残留。开启自动更新的教室机器每次启动发现新版就下载整包（百 MB 级），多次「稍后」或网络中断后磁盘被数 GB 垃圾占满（便携版常放空间有限系统盘）。修复：下载/校验失败删本次 GUID 目录；用户拒绝后保留包但记录路径供下次复用；启动清理 updates 下除最近一次成功 staging 外旧目录（backup 保留期如 7 天）。

**G-5 | Medium | `src/WorkTiles/ApplyUpdate.ps1:6,28-38,44-51`**
ApplyUpdate.ps1 无崩溃一致性：断电/强杀中途留下混合安装，无日志式恢复。回滚依赖内存中 $changed 列表逐文件「备份→覆盖」，脚本进程被杀/断电/蓝屏时已覆盖与未覆盖文件混合，备份虽在磁盘但无持久化清单/恢复入口；回滚本身尽力而为（catch { $failure+=… }），目标文件被占用时恢复失败仅记日志。影响：更新中途异常 → 应用目录新旧 DLL 混杂可能无法启动，用户只能手动从 data\updates\{GUID}\backup 恢复但无人知道该路径。修复：覆盖前把完整文件清单+备份映射写入 stage 下 journal；应用启动检测未完成 journal 提示/自动完成回滚；或改「目录级切换」（新版本解压到 app-{version}，改启动器指向，旧目录保留）。

**G-6 | Medium | `Services/PortableUpdateService.cs:51-58`；`ApplyUpdate.ps1:39,52,56`；`MainWindow.xaml.cs:1572-1573`**
更新失败对用户完全不可见，脚本被策略拦截时应用已退出且无兜底。应用启动脚本后立即关闭/隐藏自己，不读退出码不读日志；若 powershell 被 AppLocker/WDAC/组策略 ExecutionPolicy（MachinePolicy 优先于 -ExecutionPolicy Bypass）拦截脚本根本没运行：无日志无更新无重启；若脚本运行但失败 UPDATE_FAILED 也只躺在 data\updates\{GUID}\update.log。用户视角「程序消失了」或「更新永远不成功」，无错误提示与自助恢复入口。修复：① 应用退出前等脚本写「已接管」标记（短超时），超时不退出并报错；② 设置/关于页读取展示最近一次 update.log 结果；③ 覆盖「脚本未启动」分支（脚本失败且 $canRestart 时已会重启旧版，这点做得对）。

**G-7 | Medium（疑似）| `src/WorkTiles.Shared/RichText/RtfCodec.cs:44,117-119`**
Normalize 二次方拼接 + 解析无嵌套深度上限，恶意 RTF 可致 CPU/内存 DoS。① 构造每字符交替 `\b\b0` 的 RTF → 每字符一个 run → Normalize 相邻同格式合并时反复整串拷贝（:44 `result[^1].Text + run.Text`）O(n²)，100KB 级输入即可产生 GB 级拷贝量；② 构造百万个 `{` 的 RTF → 百万个 State 克隆（:117-119 每个 `{` 无条件 stack.Push(state.Clone()) 无深度限制，对比 System.Text.Json 有 64 层限制），内存线性放大无上限。RTF 来自同步项目/网页/粘贴属不可信输入；未发现越界/崩溃向量（GroupEnd 尾部反斜杠 +1 偏移经核算被调用方安全吸收，\'hh、\u、字体表边界正确）。影响取决于服务端项目 payload 体积上限（不在本模块内，标疑似），若上限宽松恶意老师/网页可让全班大屏卡死或 OOM。修复：Normalize 用 StringBuilder 累积同格式段；Parse 增组深度上限（如 512 超限抛 InvalidDataException）与输入长度上限；字体表/颜色表限长。验证：构造上述两种输入调 Parse 观察耗时/内存（tests/RichTextLogic 已有骨架可加用例）。

**G-8 | Medium（疑似）| `src/WorkTiles.Shared/SettingsSchema.cs:316`（受影响键 :89-90,112-113）；消费方 `CloudPayload.cs:161`、`MainWindow.xaml.cs:1594-1600`**
Internal 类设置零校验直通同步，错误类型/超大值可致布局应用失败或内存压力。`SettingKind.Internal => value?.DeepClone()` 不做类型/尺寸/取值校验；服务端 Sanitize 后仍原样下发：错误类型（如 CustomPalette 给对象而非数组）在桌面 Deserialize<BoardSettingsState> 抛 JsonException 整批布局应用失败（CloudSyncService.cs:607 捕获后记错误键持续卡住）；超大数组直接进设置文件与内存；DockedWidgetLayoutVersion 被远端置 0 反复触发停靠组件重置。malicious/有 bug 的网页端可拒绝服务式破坏班级布局同步或重置大屏组件摆放。标疑似：服务端可能另有 payload 体积/类型限制（WorkTilesServer 不在本次范围）。修复：为每个 Internal 键登记具体校验（CustomPalette/CustomPalettes：字符串数组+长度上限；CustomizedDockedWidgets：已知键名集合；DockedWidgetLayoutVersion：0–2 整数），不再无条件 DeepClone。验证：向测试服务端写错误类型 CustomPalette 观察大屏同步错误与设置页行为。

#### Low

**G-9 | Low | `.github/workflows/release.yml:25,28,95,107,112`** — Action 未钉 SHA（checkout@v5/setup-dotnet@v5/upload-artifact@v7 均可变 tag）；`github.ref_name` 直插 run（`gh release create "${{ github.ref_name }}"`、`--title "WorkTiles ${{ github.ref_name }}"`），git tag 名允许 `"`/`;`/`$()` 且触发过滤器 v*.*.* 拦不住，当前安全完全依赖 40-43 行版本正则闸门先失败，一旦把使用 ref_name 的步骤挪到闸门之前即成脚本注入（对比服务端 checkout 已钉 SHA :54 做法正确）。修复：所有 Action 钉 commit SHA；ref_name 一律经 env: 传入 run 内用 $env:REF_NAME。其余 CI 注入面已验证安全（inputs.version 经 env+正则；steps.version.outputs 只含 \d+.\d+.\d+；token 仅默认 github.token，job 级 contents:write 为发 Release 必需，顶层 read）。

**G-10 | Low | `.gitignore:1-6`** — 漏掉敏感/产物目录，内部 bug 截图与 AI 记忆文件存在误提交/已提交风险。.gitignore 仅 .vs/、bin/、obj/、artifacts/、.mimosa/、worktiles.db*；仓库根实际存在未跟踪的 .tmp-*.py（8 个）、.tmp-bug-images/（用户 bug 文档截图可能含教室/学生画面）、.tmp-tray-probe*.xbf、.tmp-tray-probeThemes/、.idea/ .claude/ .cursor/ .zcode/ 全未忽略；.workbuddy-ai/memory/2026-09-18.md（内部评审记忆）已被 git 跟踪。8 个临时脚本逐一全文扫描无 token/密码/内部 URL，仅本机文档路径与内部 bug 清单。影响：git add -A 类操作会把 bug 截图、内部跟踪表、AI 记忆推上公开仓库（隐私/信息泄露），产物污染仓库。修复：.gitignore 追加 .tmp-*/、.tmp-*、.idea/、.claude/、.cursor/、.zcode/、.workbuddy-ai/、*.user、TestResults/；将已跟踪的 .workbuddy-ai/memory 移出索引（git rm --cached，本次未执行任何写操作）。

**G-11 | Low | `Services/GitHubUpdateService.cs:24-45,47-69`** — 大半为死代码且与主更新路径语义分叉。全库仅 IsInstallable（被 ReleaseUpdateService 调用）与 LaunchInstaller（MainWindow:1575）被使用；死代码 CheckAsync 把 repository 未转义直插 API URL（:28），DownloadAsync 对 DownloadUrl 无 https-only 检查（对比 ReleaseUpdateService.cs:53 有）、按版本号固定目录 FileMode.Create 覆盖（多实例互踩），留着误导后续维护者接错路径。修复：删 CheckAsync/DownloadAsync 或整个类合并进 ReleaseUpdateService，仅保留 IsInstallable/LaunchInstaller（后者按 G-2 加签名验证）。

**G-12 | Low | `src/WorkTiles/ApplyUpdate.ps1:55,14,17-37`** — 一组边界/体验问题：① 重启不带原启动参数（:55），--windowed/--view/--project/静默 丢失，教室开机静默启动场景更新后突然弹全屏；② ParentProcessId 复用竞态（:14），应用退出与脚本启动间 PID 被复用 → Get-Process 等到无关进程 → 60 秒后误判「应用未退出」放弃更新（罕见）；③ powershell.exe 无 longPathAware 清单（应用有 app.manifest:12），安装路径接近 260 字符 Copy-Item 可能失败（疑似，取决于系统 LongPathsEnabled）；④ 新版本删除的文件在旧安装目录残留，原生 DLL 按名搜索可能加载旧版残留（低概率正确性风险）。修复：update.json 增原始命令行字段重启透传；进程创建时间+PID 双重校验；超长路径明确错误；按 payload 清单删除「旧版有新版无」文件（先备份）。已验证安全：-ExecutionPolicy Bypass 仅进程级、脚本来自安装目录（同用户信任边界无提权）；ArgumentList 正确 quoting + 全程 -LiteralPath + ConvertFrom-Json，JsonSerializer.Serialize 默认非 ASCII 转义 \uXXXX，update.json 纯 ASCII，Windows PowerShell 5.1 按 ANSI 读无 BOM 也不乱码（tests/UpdateLogic:42 用「程序 ' & 空格」目录实测覆盖引号/&/空格/中文）；等待旧进程退出设计正确（先等退出再动文件，data 双重排除）。

**G-13 | Low | `Services/SplitUpdatePackage.cs:31-36`；`Tools/7zip/README.md`** — 解压先于后置链接检查的残余窗口；7za.exe 自身哈希未登记。前置 listing 检查（:22-24）已拒绝 Symbolic Link/Hard Link/Attributes 含 l/加密条目，后置检查兜底；但若某种链接形态未被 listing 字段覆盖，7za 解压过程中「先建链接、后续条目穿链接写」可在检查前越界（疑似，取决于 7-Zip 26.03 对各类链接的字段报告与 Windows 创建符号链接权限）。实测捆绑 7za.exe SHA-256 为 `edbee35370e14030e4c785cf88200f42dc651c1eb4217c1e3963c38a12f099b0`，README 登记的是上游 7z2603-extra.7z 整包哈希非 exe 自身，CI 也不校验，二进制被替换无从发现。修复：README/CI 登记并校验 exe 自身哈希；解压改 -snl-/-snh-（禁用链接创建若版本支持）或解压到受控低权限目录；tests/UpdateLogic 增 unix 符号链接 zip 条目用例。其余（ArgumentList 防注入、-- 防选项注入、5 分钟超时杀进程树、2GB/10000 条目上限、标准 z01 分卷与 .001 切片两种语义）均正确。

**G-14 | Low | `src/WorkTiles.Shared/RichText/RtfCodec.cs:172,256,265-277`** — 保真性小问题（无安全影响）：\u 参数 >65535 或 <-65536 时 unchecked 截断为任意 BMP 字符（:172）；字体名可混入控制字符并随 Fonts 上报/渲染（:256）；SplitFontEntries 只要出现一个 `{` 就放弃分号分隔的无花括号字体条目，混合写法丢字体（:265-277）。往返一致性主路径（默认白 #F7F7F9↔IsDefaultWhite、\uc1?、代理对、\par/\tab、颜色表索引）经核对正确。修复：\u 参数范围校验（拒绝/替换 U+FFFD）；字体名过滤控制字符；SplitFontEntries 支持混合表。

**G-15 | Low | `src/WorkTiles.Shared/SettingsSchema.cs:283`** — Same 数值比较的溢出/精度等价（`double.Parse(x.ToJsonString())==double.Parse(...)`）：1e999 与 2e999 都解析为 +∞ 判等；>2^53 整数精度丢失不同值判等。Sanitize（:337 IsFinite）会拒非有限值，但 Same/ChangedKeys 也用于未清洗 payload 差异比较（CloudSyncService.cs:624,634）可能漏报变更致 Base 记录失真。修复：数值先按原始文本比较不等再尝试 decimal/双精度；或超 double 精度直接判不等。

**G-16 | Low（疑似）| `src/WorkTiles/WorkTiles.csproj:10,26-32`；`.github/workflows/release.yml:65-74`** — 依赖与发布方式风险。QRCoder 1.7.0、NAudio 2.4.0、Win2D 1.4.0、WindowsAppSDK.WinUI 1.8.260803003 凭现有知识无已知严重 CVE（标疑似，建议 CI 加 `dotnet list package --vulnerable` 定期核查）；WindowsAppSDK 1.8 非打包自包含发布依赖 WindowsAppSDKSelfContained=true（CI 已设），但 csproj 本地默认 SelfContained=false，开发者手工 publish 忘加参数会产出框架依赖包，而 PortableUpdateService.Prepare:40-41 必要文件检查（exe/dll/pri/runtimeconfig）不含 coreclr/运行时 DLL 拦不住这种包，更新到没装 .NET 8 桌面运行时的机器起不来。修复：Prepare 必要文件清单加 coreclr.dll（或 hostfxr.dll）强制自包含；CI 加漏洞扫描；关注 WindowsAppSDK 1.8 servicing。

---

### 模块 8：测试与验证脚本

覆盖（18+ 文件）：`tests/UiVerification.cs`(2509)、`SelectionVerification.cs`(310)、`ImageDragVerification.cs`(214)、`MediaPerformanceVerification.cs`(199)、`GridCoverageVerification.cs`(139)、`MediaUiVerification.cs`(131)、`SettingsLogic/`(Program.cs 741 + csproj)、`SyncLogic/`(Program.cs 569 + csproj)、`ProjectLogic/`、`UpdateLogic/`、`verify-ui.ps1`、`verify-interaction-contract.ps1`、`verify-requested-ui-contract.ps1`、`verify-layout-persistence.ps1`、`verify-color-formatting-contract.ps1`、`verify-rich-text-roundtrip.ps1`、`verify-release-startup.ps1`、`verify-icon-font-contract.ps1`、README 测试章节。

#### High

**H-1 | High | `tests/verify-layout-persistence.ps1:47-59,74-84`**
关闭契约漂移：脚本对当前源码必然失败，且泄漏进程与临时目录。脚本以正常模式启动（--windowed --view=…），:56 用 CloseMainWindow() 期望进程退出；但 MainWindow.xaml.cs:224-229 的 AppWindow.Closing 在非验证模式下无条件 Cancel+Hide（仅 _allowClose 或 verification 放行，_allowClose 只在托盘退出 :1242 与 QuitRequested :237 置位）。WM_CLOSE 只把窗口藏进托盘，:57 WaitForExit(5000) 必然超时、:58 必然 throw。finally :75-77 只再发一次 CloseMainWindow（窗口已隐藏无效）没有 Stop-Process → 测试进程永久残留（带托盘图标）；:83 Remove-Item -ErrorAction SilentlyContinue 因 exe 被占用静默失败 → 临时目录残留。影响：该脚本当前 100% 失败完全失去验证价值；每次运行在用户机器留下一个僵尸 WorkTiles 进程和一份含真实数据副本的临时目录。修复：给应用加测试专用退出通道（--test-exit 置 _allowClose，或复用单实例 gate QuitRequested），脚本改用该通道；finally 无论成败都 Stop-Process -Id $process.Id -Force 兜底再清理（参照 verify-ui.ps1:65-70）。

**H-2 | High | `tests/verify-layout-persistence.ps1:19-44`**
复制整个构建输出（含 data/），pancake.json 夹具被真实数据遮蔽，测试非密封。:19 Copy-Item (Join-Path $outputDirectory '*') 不排除任何目录；已实测两个候选构建目录的 data 都存在真实 projects.json，ProjectStore 优先读 projects.json，:23-44 写入的 pancake.json 迁移夹具根本不会被加载 → 迁移路径从未被测；:64-67 拿夹具布局与复制来的真实项目布局对比 → 必然 Compare-Object 失败（假失败）。对比 verify-ui.ps1:20 明确排除 data。影响：测试目标（旧数据迁移+布局保持）完全落空；复制的 data 可能携带真实集控凭据——本机当前副本 ServerUrl/DeviceToken/ClassId 均为空（已只读核验），但若开发者曾在该构建目录配对过班级服务器，测试实例会以真实设备身份连接真实服务器同步/上报，污染云端设备列表与编辑锁（疑似条件性风险）。验证：检查构建目录 data/projects.json 的 CloudSync.ServerUrl 是否非空后运行脚本观察服务端设备列表。修复：复制时排除 data、verification、startup-ok.txt（与 verify-ui.ps1:20 对齐）再写夹具。

**H-3 | High | `tests/verify-layout-persistence.ps1:47-52`**
唯一跑正常模式的脚本：删除用户真实开机自启注册表项 + 麦克风/网络/托盘副作用。正常模式 Loaded 路径（MainWindow.xaml.cs:141-151）执行 StartNoiseMonitoring（真实采集麦克风）、RefreshWeatherAsync（真实访问小米天气）、自动备份定时器、InstallTrayIcon，以及 :150 StartupRegistration.Apply(_settings.LaunchAtStartup,_settings.SilentStartup)。StartupRegistration.cs:15-18 enabled=false 时 key.DeleteValue("WorkTiles")——值名全局常量（:9）不区分安装目录；夹具（:26-35）未设 LaunchAtStartup，ProjectState.cs:133 默认 false → 脚本每跑一次就删除用户真实安装的 HKCU Run 自启项（本机当前该值不存在已只读 reg query 核验，一旦用户开启自启即被静默移除）。反向：若复制来的 settings LaunchAtStartup=true 则把临时目录 exe 路径写进 Run，下次开机指向已删除临时目录。影响：测试越出沙箱修改用户真实系统配置（自启丢失或指向失效路径），并产生麦克风占用、外网请求、托盘残留。修复：让脚本走验证模式或新增测试开关跳过 :141-151 全部副作用；至少夹具固定 LaunchAtStartup=false 并测试前后保存/恢复 Run 值（治标），根治是禁止测试进程触碰 StartupRegistration。

**H-4 | High | `tests/UiVerification.cs:747-748,845`**
IME 诊断切换系统键盘布局后异常路径不恢复，污染用户输入环境。:747 LoadKeyboardLayout("00000804",1)（KLF_ACTIVATE 系统级激活中文布局）+ :748 PostMessage WM_INPUTLANGCHANGEREQUEST；恢复 :845 PostMessage(hwnd,0x0050,0,original) 只在成功路径执行，:792/:813/:833/:839 任一 Check 抛异常即跳 :850 catch，用户当前输入法被留在中文状态需手动切回。影响：可选模式 -AutofillImeOnly 每次失败都把开发者/用户系统活动键盘布局改成中文输入法，真实用户环境污染。修复：把 :845-847 恢复移入 finally（original 在 :746 已捕获），恢复后校验 GetKeyboardLayout 是否回到原值。

**H-5 | High | `tests/UiVerification.cs:686-700,738/768/781/820,848`**
失焦时全部检查静默跳过，仍无条件写 OK 标记 → 假通过。ReadyAsync（:686-700）前台丢失时只记一条 skip 证据返回 false；四个按键区块（:738 对照组、:768 空闲态、:781 组合态、:820 上屏后）全包在 `if (await ReadyAsync())` 里。若整段运行期间窗口持续抢不到前台（另一 WorkTiles 实例、通知弹窗），所有 Check 一次都不执行，:848 仍无条件 evidence.Add("AUTOFILL_IME_DIAGNOSTIC_OK")，verify-ui.ps1:49/:61 以末行标记判定 → 报 PASS。影响：-AutofillImeOnly 模式可在零检查执行情况下绿灯通过，掩盖输入法补全回归。修复：记录核心区块是否实际执行（executed 计数），未执行写 SKIPPED/FAILED 而非 OK；或 ReadyAsync 失败重试 N 次后直接 throw。

**H-6 | High | `tests/verify-requested-ui-contract.ps1:182,190`**
两处精确字符串断言已与源码漂移，脚本当前必然 exit 1（假失败）。已实测：① :182 要求 MainWindow.Islands.cs 含 `BoardScroller.ChangeView(null, null`——当前 0 命中（现为 :738 ChangeView(x,y,zoom,true)、:786 ChangeView(x,y,(float)_boardZoom,true)）；② :190 要求 `bool zoomBefore = false;`——当前 Islands.cs:553 为 `bool zoomBefore = !vertical && _settings.ToolbarPosition.EndsWith("Right");`。两条各加一个 failure，:212-214 exit 1。其余抽查锚点（:198 让位逻辑、:186-189 滑条、:208 图标映射）当前均命中。影响：整个 requested-ui 契约脚本对当前代码恒红，开发者只能忽略它，契约门禁形同虚设；也是「精确串断言」脆弱性的实证。修复：两条放宽为语义锚点（`bool zoomBefore =`、`BoardScroller\.ChangeView\(`）或按当前实现更新精确串；并把该脚本纳入 CI（见 H-M4）漂移才会被及时发现。

**H-7 | High | `tests/SyncLogic/SyncLogic.csproj:8,11` + `tests/SyncLogic/Program.cs:18-23`**
SettingsSchema 取自相邻服务端仓库副本，当前默认本地运行必然失败。SyncLogic 不引用桌面端 src/WorkTiles.Shared，SettingsSchema 经 WorkTiles.Server.csproj:13 传递自服务端仓库 Shared 副本。已实测两端 SettingsSchema.cs 当前存在实质差异：服务端含 SettingKind.WidgetProfile、new("WidgetProfile",…) 描述符与整套 WidgetProfile 校验，桌面端完全没有（桌面 src/*.cs grep WidgetProfile 零命中）。Program.cs:18-20 遍历服务端 SettingsSchema.All 并 Check(SettingsSchema.Get(defaults,descriptor.Key) is not null)，defaults 来自桌面端 BoardSettingsState 序列化（无 WidgetProfile 键，Get 对缺失键返回 null，已读服务端 SettingsSchema.cs:249-257 确认）→ 用默认 WorkTilesServerDir（相邻检出）运行时 :20 必抛「设置 WidgetProfile 不存在」。README:296 文档化的本地命令正是无参运行；CI 不受影响（release.yml:54 固定了不含 WidgetProfile 的服务端快照）。影响：文档化的本地同步测试命令开箱即失败，失败原因与被测代码无关，取决于相邻仓库停在哪个分支；测试语义随外部仓库状态漂移。修复：让 SyncLogic 直接 Compile/ProjectReference 桌面端自己的 Shared（或同时引用两份并加「两端 schema 一致性」断言）；csproj/README 写明所需服务端快照，本地默认路径失配给明确提示而非裸断言失败。

#### Medium

**H-M1 | Medium | `tests/UiVerification.cs:724-734,846-847`** — 窗口子类化只在成功路径恢复，异常路径存在委托回收崩溃风险。:734 Marshal.GetFunctionPointerForDelegate(procedure) 子类化窗口过程，恢复 :846 与 GC.KeepAlive(procedure) :847 都在成功路径末尾；任何 Check 抛异常后窗口仍挂指向托管委托的 WNDPROC，而委托只被局部变量引用、KeepAlive 被跳过 → 窗口销毁前若有消息到达可能触发 CallbackOnCollectedDelegate 崩溃（疑似竞态）。验证：在 :792 前人为制造失败运行 -AutofillImeOnly 观察是否崩溃及日志。修复：SetWindowLongPtr 恢复与 GC.KeepAlive 移入 finally；或委托存实例字段保证生命周期覆盖窗口存活期。

**H-M2 | Medium | `tests/SelectionVerification.cs:111-121,232-240`** — 条件断言无 else 失败分支，目标缺失时静默跳过（假阴性）。:111-112 按硬编码颜色（A==104、R==186）找选区色带，找不到时 :119-120 两条几何断言直接消失；:232 缩放后 `if (host is not null)` 包裹 :237-239 两条对齐断言，缩放回归致罩层宿主 null 时检查同样静默跳过（对比 :225-227 滚动检查有 else check(false) 正确写法）。罩层颜色调整或缩放破坏罩层这两类回归可绿灯通过。修复：两处补 else check(false, "band/host not found …")。

**H-M3 | Medium | `tests/UiVerification.cs:517-519`（及 ImageDragVerification.cs:96-98）** — 真实按键/鼠标注入只在开头校验一次前台，中途失焦会把输入打进用户窗口。AutofillKeys 模式 :519 检查一次前台后持续约 15 秒注入 Down/Up/Tab/Enter/Escape（SendKey :953-959），期间用户切窗或弹通知按键落入任意前台应用（Enter/Tab 可能触发对方按钮/提交），同时本测试以难以理解的断言失败告终；ImageDrag 用真实鼠标拖动 :96 置顶缓解但未逐步复查（IME 诊断 ReadyAsync 逐步复查是正确范式）。修复：每个 SendKey/SendMouse 批次前复查 GetForegroundWindow（失焦即重抢或明确 FAILED 中止）；README 再强调「运行期间勿操作电脑」。

**H-M4 | Medium | `.github/workflows/release.yml:56-60,88-92`** — CI 只跑 SyncLogic 与冒烟启动，其余全部测试从不自动化执行。发布流水线仅执行 SyncLogic（:59）和 verify-release-startup（:92），且发布构建 -p:EnableUiVerification=false（:71）；SettingsLogic/ProjectLogic/UpdateLogic、6 个 UI 验证器、5 个契约脚本均无 CI 入口，README 也未记载多数（见 H-L13）。这些测试覆盖的回归（更新回滚、锁屏逻辑、契约漂移如 H-6）可一路绿灯进入正式发布。修复：release.yml 增 windows-latest 作业跑三个逻辑测试+全部契约脚本（纯静态/临时目录无需交互桌面）；UI 验证器至少发版前作为手动门禁清单写入 README，或自托管交互 runner 定时跑。

**H-M5 | Medium | `tests/verify-ui.ps1:1-13,37`** — --verify-image-drag 没有任何脚本入口，ImageDragVerification 整个从不执行。App.xaml.cs:101-104 支持 --verify-image-drag，WorkTiles.csproj:51 编入 ImageDragVerification.cs（214 行真实鼠标拖动/八点缩放/让位断言），但 verify-ui.ps1 参数表（:1-13）与映射（:37）无对应开关，全仓库 grep（含 .github、README）无任何调用方。图片拖拽这一核心交互的整套真实输入验证是死代码，回归无人发现。修复：verify-ui.ps1 增 -ImageDragOnly 开关，映射 --verify-image-drag、结果文件 image-drag-result.txt、标记 IMAGE_DRAG_VERIFICATION_OK（三者验证器已写好 :35/:40），并写入 README。

**H-M6 | Medium | `tests/SyncLogic/Program.cs:48,563-569`** — StartAsync 在 try 之外；finally 清理链一环失败即泄漏。:41-47 Build 后 :48 await app.StartAsync() 位于 :49 try 之前——启动失败（端口/DB 异常）时 finally 不执行，temp root（server.db 等）泄漏、半启动 app 未 Dispose；finally 内 :565 StopAsync 若抛异常 :566-568 全跳过；:568 仅 catch IOException，杀毒短暂锁文件引发的 UnauthorizedAccessException 向外传播掩盖测试本身成败。修复：StartAsync 移入 try；StopAsync/DisposeAsync/ClearAllPools/Delete 各自独立 try/catch（Delete 同时捕获 UnauthorizedAccessException）。

**H-M7 | Medium | `tests/ProjectLogic/Program.cs:10`** — Reject 接受五类异常，无关 bug 也被当成「正确拒绝」（假阳性）。`catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)`——被测代码若因无关缺陷抛出其中任何一种（如 setup 错误的 InvalidOperationException）Reject 仍判「已拒绝」（对比 UpdateLogic/Program.cs:10 只认 InvalidDataException 的严格写法）。损坏包/穿越包拒绝测试可能因错误原因通过。修复：按用例指定期望异常类型（必要时校验消息关键词），与 UpdateLogic 对齐。

**H-M8 | Medium | `tests/SettingsLogic/Program.cs`（全文）+ `SettingsLogic.csproj:38`** — 桌面端 SettingsSchema 被编译进测试却从未被断言。SettingsLogic 引用桌面端 Shared（csproj:38）但 Program.cs 中 SettingsSchema 零出现（grep 仅 :659 BackupScopes.Sanitize 属另一类型）；唯一测 schema 行为的 SyncLogic:17-37 用的是服务端副本（见 H-7）。桌面端 SettingsSchema.cs 的默认值、登记完整性、Sanitize 规则没有任何测试直接覆盖——两端已发生的 WidgetProfile 漂移正是这种缺口的实例。修复：SettingsLogic 补一段与 SyncLogic:17-37 等价的 schema 断言（用桌面端 Shared），并加「桌面/服务端 schema 一致性」检查。

**H-M9 | Medium | `tests/verify-layout-persistence.ps1:8`** — 硬编码非 x64 构建路径，与 README/CI 构建配置不一致，可能测到陈旧二进制。:8 固定 bin\Release\net8.0-windows10.0.19041.0\win-x64；README:226 与 release.yml:69 都用 -p:Platform=x64 输出在 bin\x64\Release\…；本机两个目录同时存在（已 ls 核验），脚本会拿到另一配置下可能过期的构建。修复：改 -PublishDirectory 参数（与其他脚本一致），或按修改时间解析最新构建并打印所用路径。

**H-M10 | Medium | `tests/verify-color-formatting-contract.ps1:28-31`** — 块正则无 .Success 守卫，签名改名后检查空过（假阴性）。:28 用 `private RichEditBox CreateRichEditor[\s\S]*?private StackPanel BuildFormattingToolbar` 截取块，任一签名变化 → Match 失败 → .Value='' → :29 -match 'Foreground\s*=' 为 false → 不报失败检查静默消失（当前锚点存在 SubjectTileControl.cs:717,983 已核验，属潜在漂移；同仓库 verify-rich-text-roundtrip.ps1:24 已有 -not $captureMethod.Success 守卫正确写法）。修复：加 `if (-not $richEditorBlock.Success) { $failures.Add(...) }`。

#### Low

**H-L1 | Low | `UiVerification.cs:1570,1588`；`GridCoverageVerification.cs:132`** — check(true,…) 把纯证据行伪装成断言（恒过只为输出测量值，但生成 PASS: 行被 verify-ui.ps1:62 计入「N real-window checks」虚增真实断言数）。修复：直接 evidence.Add(…)。

**H-L2 | Low | `tests/verify-ui.ps1:34-35`** — 设置的测试环境变量未还原（$env:WORKTILES_TEST_VIDEO/_BASE 指向临时目录，脚本结束后仍留在调用方会话，临时目录删除后后续读取该变量的运行拿到失效路径）。修复：finally 恢复/清除这两个变量。

**H-L3 | Low | `tests/verify-release-startup.ps1:25,12`** — finally 清理无 try/catch 可掩盖 PASS（:25 Remove-Item 在 ErrorActionPreference=Stop 下裸执行，进程刚退出 DLL 常被杀毒/系统短暂占用 → 清理抛异常把成功冒烟结果变成 CI 失败；verify-ui.ps1:68-70 已意识到并守卫本脚本却没有）；:12 超时直接 throw finally 删临时目录 crash.log 等证据全丢（verify-ui.ps1:42-45 超时会先保全证据）。修复：清理包 try/catch+Write-Warning；超时分支先复制 verification/data 证据再 throw。

**H-L4 | Low | `tests/verify-icon-font-contract.ps1:5`** — ConvertFrom-Json -AsHashtable 仅限 PowerShell 7+ 无 #Requires 声明（README:175 示范用 Windows PowerShell 5.1 跑契约脚本，照同样方式跑本脚本报「找不到参数 AsHashtable」；CI 用 pwsh 但从不执行本脚本 H-M4，问题只在本地暴露；其余健壮 :14 count>=20 防空过，FluentIcon.cs 当前 47 条 const 符合 :9 正则）。修复：文件头加 #Requires -Version 7.0，或改 5.1 兼容 JSON→Hashtable 转换。

**H-L5 | Low | `tests/verify-interaction-contract.ps1:23-26`** — 死检查被 :54-56 完全覆盖（:23-26 仅在 XAML 含 BoardScrollViewer 且代码无 ScrollMode.Disabled 时报错，而 :54-56 只要 XAML 含 BoardScrollViewer 就报错，当前 XAML 无该名称已核验，前者永不可能独立触发）。修复：删除 :23-26。

**H-L6 | Low（疑似）| `tests/SettingsLogic/Program.cs:220-228`** — FirstChanceException 全 AppDomain 计数可能混入无关异常（:225 挂全局首次机会异常钩子统计 FileNotFoundException，窗口期内运行时自身程序集/资源探测如卫星程序集 probing 也计入，致 :228 missingExceptions==0 偶发失败）。验证：在触发程序集加载时机（首次 JsonSerializer 调用前后）运行该段观察计数。修复：计数按 StackTrace 过滤到 WallpaperEngineLibrary 帧，或改断言 ReadProject 内部不抛（包装探测）。

**H-L7 | Low（疑似）| `tests/ProjectLogic/Program.cs:37`** — 绝对路径泄漏断言只查 JSON 转义反斜杠形态（`!Contains(root.Replace("\\","\\\\"))` 只覆盖 C:\\… 形态，若序列化/拼接 bug 产生正斜杠绝对路径或 file:/// URI 则漏检；当前 System.Text.Json+Path.Combine 产生反斜杠故为潜在缺口）。验证：构造含正斜杠绝对路径的附件保存后跑该断言。修复：同时检查未转义形态、正斜杠形态与 file:/// 前缀。

**H-L8 | Low | `tests/UpdateLogic/Program.cs:155`；`ApplyUpdate.ps1:34,44-51,25,54-57`** — 补充越界断言弱（:155 只查 root\escape 一处，主断言 :150-151 Reject 已逐条要求 InvalidDataException 故仅为补充 weakness）；回滚残留空目录（ps1 回滚只恢复文件 :44-51，:34 新建目录不删除 → 失败更新后目标目录残留空目录）；重启与联接拒绝路径无测试（全程 -SkipRestart Program.cs:60，重启分支 ps1:54-57 与 ReparsePoint 拒绝 ps1:25 无覆盖）。另核验 Prepare 的 ParentProcessId 传 int.MaxValue（Program.cs:105），ps1:14 Get-Process 找不到即跳过等待设计正确无 60s 卡死。修复：补充断言检查 target 外无任何新文件；回滚清理本次新建空目录；为联接拒绝与重启分支补用例。

**H-L9 | Low | `tests/ImageDragVerification.cs:200,207`** — SendInput 返回值未检查（注入失败 UIPI 拦截等返回 0 时静默继续，后续几何断言以误导性消息失败；:88 图片解码结果仅记证据不检查是注释明示有意设计可接受）。修复：SendMouseInput 返回 !=1 时 throw 明确错误。

**H-L10 | Low | `tests/MediaPerformanceVerification.cs:55-65`** — SettingChanged 同步耗时只测量不限制（:60 收集 durations、:65 输出 maxUiWork 但无阈值断言，外观更新单次卡数秒的回归不会失败；首段 :50 的 1500ms 调度停顿检查只覆盖自然刷新阶段）。修复：对 durations.Max() 加合理上限断言。

**H-L11 | Low | `tests/MediaUiVerification.cs:1,110,113`** — 过时注释与重复枚举（:1 注释引用不存在的 verify-background-media.ps1，实际由 verify-ui.ps1 -BackgroundMediaOnly 生成夹具，:42 硬编码 BaseDirectory\media-test.mp4 与 verify-ui.ps1:24 落盘位置一致当前可工作；:110 与 :113 两次调用 WallpaperEngineLibrary.ReadVideos 全量枚举用户库只读无写入）。修复：更正注释；缓存一次 ReadVideos 结果。

**H-L12 | Low | `tests/RichTextLogic`、`tests/ServerLogic`、`tests/UiHeadless`** — 三个空目录遗留（已 ls 核验均为空无 csproj/源码，疑似历史遗留误导维护者）。修复：删除。

**H-L13 | Low | `README.md`（测试章节）** — 多数测试入口无文档（grep 核验：verify-requested-ui-contract / verify-color-formatting-contract / verify-rich-text-roundtrip / verify-icon-font-contract / verify-layout-persistence 五个脚本，以及 -GridCoverageOnly / -SelectionOnly / -MediaPerformanceOnly / -FullScreenHintOnly / -FontAuditOnly / -AutoLayoutOnly 六个开关、--verify-image-drag 入口均未在 README 出现）。修复：补一张「测试入口总表」（脚本/开关/前置条件/是否需交互桌面）。

---

## 五、跨模块重复项去重对照

下列同一根因问题被多个模块独立发现，已合并为一条（canonical 列），统计不重复计数：

| 问题 | canonical | 重复来源 |
|---|---|---|
| 便携更新 Close() 被隐藏式关闭拦截 | 模块 7 G-1（Critical） | 模块 1 B-H1 |
| 备份还原覆盖竞态 | 模块 2 A-H1（High） | 模块 3 C-H2 |
| 托盘重启跳过退出保存 | 模块 2 A-H2（High） | 模块 1 B-H3 |
| 项目事务回滚替换 _settings 致设置页/媒体编辑器闭包写孤儿对象 | 模块 2 A-M2（Medium） | 模块 4 D-#13 |
| 备份/还原同步 zip IO 压 UI 线程 | 模块 2 A-M4（Medium） | 模块 3 C-M10 |
| DeviceToken/TOTP 明文持久化 + 备份克隆身份 | 模块 3 C-M8（Medium） | 模块 6 F-#5（存储路径已修正为 projects.json） |
| BrushFromHex 8 位 hex 语义与 GridAppearance 相反 | 模块 6 F-#7（Low） | 模块 1 B-L14 |

独立发现（非子代理报告，主审实测验证）：G-A（更新资产命名脱节，High）、G-B（csproj 引用未跟踪文件，High）、A-L9（StartupRegistration 全局 Run 值名多安装互踩，Low）。

---

## 六、已核查并排除的疑点（正面确认，避免误报）

- **Zip Slip / 更新包解压**：PortableUpdateService.ValidateRelativePath/ValidateTarget（:60-80）+ 脚本侧重验（ApplyUpdate.ps1:21-27）双层防护，拒绝 rooted/`..`/尾点尾空格/ADS/设备名/data 首段/符号链接/重复路径，2GB+10000 条目上限，tests/UpdateLogic:150-155 覆盖 → 未发现漏洞。壁纸导入走 MediaLibrary.CopyDirectory（拒绝 ReparsePoint、路径包含检查、Directory.Move 原子落盘），WebWallpaperPackage 无 zip 解包面。
- **PowerShell 命令注入**：ArgumentList 正确 quoting + 全程 -LiteralPath + ConvertFrom-Json + JSON \u 转义，含空格/引号/分号/中文路径均安全（tests/UpdateLogic:42 实测）；-ExecutionPolicy Bypass 仅进程级、脚本来自安装目录（同用户信任边界，无提权）。
- **TLS / 硬编码密钥**：全仓无 ServerCertificateCustomValidationCallback 等关闭点；更新链全 HTTPS 且资产 URL 强制 https（ReleaseUpdateService.cs:53）；全部指定文件与 8 个临时脚本未见硬编码 token/密码/内部 URL；小米天气 appKey/sign 为公开常量非私有密钥。
- **原子写 / 数据完整性基础**：ProjectStore.AtomicWrite、MediaLibrary.Save、DownloadAssetAsync 均唯一临时名 + Flush(true) + 原子替换；AssetReference.TryParse 严格 64 位 hex + 1–10 位字母数字扩展名，云端正则路径无法注入；BackupService.IsSafeEntry/CheckEntries 有效阻断 ..、\、:、绝对路径与重复条目；DeleteAssets 只用受控根 + Guid；冲突「先备服务器版本→另存本机副本→再替换」「任一附件下载失败即放弃替换」确实保住双方内容；projects.json 损坏进入 _storageReady=false 保护模式拒写。
- **锁屏密码学**：PBKDF2-SHA256/100k/16B 盐/32B 哈希、FixedTimeEquals、FormatException 兜底正确；TOTP 的 HMAC-SHA1、大端计数器、动态截断、Base32 编解码符合 RFC 6238/4648（问题仅在存储 C-M8 与重放 F-#14）。
- **噪音采集隐私与生命周期**：全链路仅计算能量/分贝，无音频落盘无文件写入；WasapiCapture Start 先 Stop、失败 catch 再 Stop+CaptureFailed，Stop 退订→StopRecording→Dispose→MMDevice.Dispose→锁内清统计；NAudio 2.4.0 Dispose=StopRecording+Join 采集线程+释放 audioClient，复用缓冲同步消费安全；24 位符号扩展与 float 判定正确；噪音合流（Interlocked+TryEnqueue）逐交错分析正确无丢失超阈值事件。
- **Composition/Win2D/GDI 资源**：BlurBackdropBrush/DesktopGlassBrush OnDisconnected 成对 Dispose；ExportBackdrop 共享设备不 Dispose（正确）其余全 using；PersistentMicaBackdrop 控制器释放正确；FontService CreateCompatibleDC/DeleteDC 成对 finally 释放、LOGFONTW FaceName 偏移 28 正确、回调 GC.KeepAlive 到位；VideoFrameImage Interlocked 单帧合并、先 Dispose 帧图再 Dispose 播放器顺序正确。
- **保存/冲刷链路**：每键纯文本立即写模型、RTF 150ms 防抖、保存 tick 前 CaptureCurrentProject 对所有磁贴 FlushPendingText、Unloaded 也冲刷、RebuildEntries 先冲刷再重建；ApplyGuard 定时器 Stop 清回调、Unloaded 停守护、20 次自停；罩层无反馈循环（镜子 TextChanged 未订阅，CWT+WeakReference 生命周期正确）；清空笔迹经 _contentChanged→ScheduleSave / StrokesChanged→ScheduleSave 保存链路成立。
- **云指令面**：集控指令只有 message/open-project/end-edit 三种，均 TextBlock 展示或受 RunProjectAction 约束的项目操作，无命令执行/注入面；布局同步键清单排除 CloudSync/Lock（SettingsSchema.DeviceLocalKeys），凭据不随布局上传；自动填充词库明文但本机不同步（DeviceLocalKeys 排除），无按键注入（全 src grep SendInput/keybd_event/mouse_event/InputInjector 零命中），不存任何凭证/otpauth 密钥。
- **桌面 Z 序 / 拖动 / 同步身份**：RestackDesktopGroup 经 MSDN SetWindowPos 语义核对正确（被定位窗口落在锚下方），非编辑分支命令栏确在磁贴之上无 Z 序反转；桌面拖动走 FollowCursor 屏幕光标绝对位移、缩放走 FollowDesktopResize 光标绝对坐标，均不受 tile RenderTransform 影响无漂移；云路径 SettingsCopy.Into 原地合并、ActivateProjectAsync 原地改保身份，仅失败回滚破身份（A-M2）；每保存节拍 ReloadIdleTileContent 有 ContainsFocus/ModelStamp 守卫未变更不重建；插入线呼吸动画实为 Composition ScalarKeyFrameAnimation 合成器线程驱动。
- **数学 / 边界**：RestoreApplied 前后缀差分多组边界推演正确；RotateImage ±90 与 ImageCropGeometry.RotateOffset 在 Y 向下坐标系一致；InkGeometry span 命中测试正确；ExportLayout 二分可行性单调性成立、标题过高有友好守卫；LayoutMetrics.GridTooDense 溢出防护正确；BoardLayout shelf+装箱/AlignSizes/Snap 边界正确；IslandScroll NaN/Infinity 守卫齐全；WidgetLayout Move/Resize/ResizeCentered 夹取齐全；VideoPlacement 非正尺寸守卫与 Fit/Zoom/Stretch 正确。
- **其它**：SingleInstanceService 双重 Dispose 幂等（注释覆盖还原重启场景）；Mutex createdNew 判首实例用法正确、AbandonedMutex 自愈；考试 async void Tick 经 StepAsync 兜底所有异常、effective! 空安全、EnterMinutes 越界由 ExamSchedule 钳制；自动备份「失败不覆盖上次成功时间」顺序正确（先 Create 成功才置 LastRunAt）；导出前 SaveStateNow 失败不影响导出内容（BackupService.Create 序列化传入内存库克隆不依赖磁盘）；InitializeComponent 期间 XAML 事件回弹有 !_isLoaded/null 守卫不会 NRE；TrayMenuWindow/BorderlessPopupHost 资源链完整（Session.Close→host.Dispose→XamlSource.Dispose+DestroyWindow+静态字典移除）。
- **CI 注入**：版本正则闸门 + env 传参使当前所有 ${{ }} 插值安全；inputs.version 经 env+正则；steps.version.outputs 只含 \d+.\d+.\d+；token 仅默认 github.token，权限最小化合理；无 issue/PR 标题进入 run 的路径（ref_name 直插属脆弱模式见 G-9）。
- **测试隔离正面**：验证模式 MainWindow(false,"verification") 确实跳过噪音/天气/更新/备份/注册表/托盘（xaml.cs:141-152）；verify-ui.ps1:20 与 verify-release-startup.ps1:10 正确排除 data；四个逻辑测试均 temp+GUID 且删除前有 temp 前缀守卫；UpdateLogic 用假 HttpMessageHandler 免网络、int.MaxValue 父进程号避免等待；UpdateLogic 回滚测试真实有效（:145-149 锁定文件→失败→回滚真实 powershell 执行）；云同步失败路径覆盖好（SyncLogic:98-108 大图上传失败保留队列、:549-559 凭据吊销提示重新配对）；锁屏逻辑覆盖扎实（SettingsLogic:411-478）。唯一破坏隔离的是 verify-layout-persistence.ps1（H-1/H-2/H-3）。

---

## 七、修复优先级建议

**第一批（立即，恢复核心流程与堵安全洞，改动小）**
1. G-1：更新 Close() 前加 `_allowClose=true`（一行恢复更新应用端）。
2. G-A：更新资产过滤器兼容历史 Pancake 命名 / 重新发布 WorkTiles 命名资产（恢复更新发现端）。
3. A-H3：托盘 CommandSettings 纳入 AuthorizeAsync 门（一行堵锁屏绕过）。
4. G-B：把 AppIcon.ico、logo.png 纳入版本控制（恢复干净克隆构建）。
5. C-H1 + C-M6：IsProjectBusy 与接管/end-edit 分支纳入 _desktopEditing（堵桌面编辑丢内容链）。

**第二批（数据完整性状态机重构）**
6. A-H1 + A-M5：还原保护动作提前到弹框前 + Cloud.cs:216 加 _storageReady/!_skipSaveOnClose 守卫；重启失败分支重载内存。
7. A-H2：托盘重启单列保存路径（拆 RestartCore(bool skipSaveOnClose)）。
8. A-M1/M2/M3：RunProjectAction 事务边界一次性加固（catch 范围、SettingsCopy.Into 原地回滚 + Refresh 回调、finally 恢复防抖表）。
9. C-M1/M2：三方合并纳入 Name/CustomPalette/CustomPalettes/AssignmentDate；播放列表部分失败保留本机项。

**第三批（供应链与可信更新）**
10. G-2：Authenticode 签名 + detached 签名强制验签 + 安装器 WinVerifyTrust；过渡期 GitHub 强制 digest。
11. G-5/G-6：ApplyUpdate.ps1 journal 崩溃一致性 + 更新失败可见性（已接管标记 + 关于页展示 update.log）。
12. G-3：背景路径拒绝 UNC（堵 NetNTLM 泄露）；C-M7：http/ws 警告或强制 https；C-M8：令牌/TOTP DPAPI 加密 + 备份脱敏。

**第四批（测试安全网修复，否则回归持续漏网）**
13. H-1/H-2/H-3：verify-layout-persistence.ps1 走验证模式/测试退出通道 + 排除 data + 禁止触碰 StartupRegistration + finally Stop-Process 兜底。
14. H-4/H-5：UiVerification IME 布局恢复移入 finally + 失焦未执行写 FAILED 而非 OK。
15. H-6/H-7：requested-ui 契约锚点放宽/更新 + SyncLogic 引用桌面端 Shared（或加两端一致性断言）。
16. H-M4：CI 纳入三个逻辑测试 + 全部契约脚本；H-M5：补 ImageDrag 脚本入口。

**第五批（性能与体验，与「界面减压」方向一致）**
17. C-M9 + A-M4：同步哈希/载荷构建与备份 zip IO 移入 Task.Run。
18. D-#2/D-#4：拖动插入引导节流/前缀复用 + 宿主原生调用变更守卫。
19. E-H1：两处富文本查找改递归视觉树（恢复正文默认样式与精确测量）。
20. F-#2/F-#3：导出 Maximum 降 4096 + 高 DPI RenderAsync 语义实测校正。
21. B-M1/M2/M3/M4：静默启动更新对话框推迟、天气失败重试、地区框纳入 _dialogOpen、二次启动尊重 --silent。

其余 Medium/Low 按模块就近合并到上述批次或日常维护处理；疑似项先按各自验证方法确认再决定是否升级（F-#1/F-#3 若证实崩溃/全体高 DPI 失败则升 High）。

---

*本报告由只读地毯式审查生成，所有行号以审查时磁盘内容为准；未对项目做任何修改。疑似项均已标注验证方法，建议修复前按方法复核。*