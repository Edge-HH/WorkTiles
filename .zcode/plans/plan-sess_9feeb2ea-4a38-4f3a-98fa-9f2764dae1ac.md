# PancakeServer 权限系统进化：范围管理员 + 多老师并发编辑

## Context

需求：服务器支持多账号权限体系 —— 老师账号管理自己班所教科目的作业内容、以及所有作业的排版，可新建作业/项目；多学科老师同时编辑同一作业时布局锁定、各改各科、完成自动合并、同学科互斥；管理员账号可选管理范围，范围内全科可编辑并可控制考试；所有老师和管理员都能发公告。

**现状盘点（已探索确认）**：服务端（`D:\Files\Codes\Projects\Pancake\PancakeServer`，ASP.NET Core + SQLite）已有完整账号体系：`UserAccount` 角色（Administrator/Teacher）、每班 `ClassMembership`（职务 + `SubjectsJson` 任教科目）、PBKDF2 密码 + Bearer 会话、`Access.EditableSubjects` 科目级编辑限制（`PayloadEditor.ApplyEdit` 强制）、老师可新建/删除/恢复/复制项目、老师和管理员都能发公告与群发、考试计划老师和管理员都可管理（`ExamEndpoints.CanReach`）。**真正缺的**：①管理员都是全校范围，无"管理范围"；②编辑锁是单持有者（第二个老师直接被拒），无按科目并发编辑/布局锁/自动合并；③网页控制台完全没有账号管理 UI（只有 REST）；④服务端 `src/Pancake.Shared/SettingsSchema.cs` 已损坏（语法错误、重复描述符、乱码，无法编译），需从桌面端仓库逐字恢复。

用户未答疑，按推荐默认执行：范围=年级组+班级；范围管理员=范围内全部班务；考试权限保持现状；单人编辑时所有老师都可排版。

## 实施步骤

### 1. 修复服务端损坏的 SettingsSchema.cs（前置）
- 用客户端 `D:\Files\Codes\Projects\Pancake\Pancake\src\Pancake.Shared\SettingsSchema.cs`（工作区最新版，含未提交的 WidgetProfile 工作）逐字覆盖服务端 `D:\Files\Codes\Projects\Pancake\PancakeServer\src\Pancake.Shared\SettingsSchema.cs`（两仓库必须逐字同步的既定规则）。

### 2. 共享协议 `Contracts.cs`（两仓库逐字同步）
`PancakeServer/src/Pancake.Shared/Contracts.cs` 增量（全部可选字段，向后兼容，按现有模式加中文 `/// <summary>`）：
- `EditLockRequest`：加 `List<string>? Subjects = null`（网页申请编辑会话时认领的科目；设备不传）。
- `EditLockInfo`：加 `List<string>? Subjects = null`（该编辑者认领的科目）。
- `SyncEvent`：加 `List<EditLockInfo>? Editors = null`、`bool LayoutLocked = false`（`editing` 事件携带全员编辑状态）。
改完逐字复制到 `Pancake/src/Pancake.Shared/Contracts.cs`。

### 3. 数据模型与 Access（范围管理员）
- `Data/PancakeDbContext.cs`：`UserAccount` 加 `string? AdminScopeJson`（null/空 = 全校；格式 `{"Groups":[guid],"Classes":[guid]}`）；`Data/SchemaUpgrader.cs` 加对应 `AddColumn`。
- `Services/Access.cs`：
  - 新增 `GlobalAdmin(http, sessions, out session)`（全校管理员）与 `InAdminScope(session, classId, db)`（管理员：范围空→true，否则班级 ∈ 直选班级 ∪ 分组下班级；老师：沿用 membership）。
  - `CanManageClass`、`BoardEndpoints.ManageableClasses`、`CanManageStaff` 改为经 `InAdminScope`（范围管理员自动获得范围内全科编辑、考试、公告、班务）。
- 范围解析在 `Services/SessionStore.cs` 的 `UserSession` 里缓存 `AdminScopeJson`（登录时载入）；`me` 端点（`AuthEndpoints.cs:38`）返回 `adminScope`，范围变更时 `RevokeUser`。

### 4. 端点权限收紧
- `AdminEndpoints.cs`（用户/审计/备份）与 `ClassEndpoints.cs`/`ClassGroupEndpoints.cs`（班级与分组的增删改）改用 `Access.GlobalAdmin`；配对码、设备管理等班级级操作维持 `CanManageClass`（范围管理员可用）。
- `ExamEndpoints.CanReach`：管理员分支改为"全校→true；范围管理员→目标班级全在范围内"。
- 新增账号接口：`CreateUserRequest`/`UpdateUserRequest`/`UserItem`（`Contracts/WebContracts.cs`，服务端专用，无需同步）加 `AdminScope`（新记录 `AdminScope(List<Guid> Groups, List<Guid> Classes)`），校验 id 存在、"至少一个启用管理员"规则不变。

### 5. 多老师并发编辑（核心）
- `Services/EditLockService.cs` 重构：键仍为 `(ClassId, ProjectId)`，值变为 `设备占用(单个) + 用户编辑会话列表`，每项 `{Holder, Subjects(HashSet), Since, Beat}`：
  - 设备申请：有任一用户会话→拒绝（现状语义）；用户申请：大屏占用中→拒绝，`force` 仍只接管大屏（发 `end-edit`）；同一 (UserId, ClientId)→续期并更新科目；**认领科目与他人重叠→拒绝（423，附带占用者及科目）**。
  - `Blocking`：设备写入被任一用户会话拦截（现状）；网页写入被设备占用拦截（现状）。
  - `LayoutLocked` = 用户会话数 > 1；`Sweep` 逐会话清理并经 `editing` 事件广播剩余编辑者。
- `Endpoints/EditLockEndpoints.cs`：PUT 接收 `Subjects`（与 `Access.EditableSubjects` 求交校验，老师不能认领非任教科目；管理员/班主任可认领全部），响应与 `editing` 事件带 `Editors`/`LayoutLocked`。
- `Endpoints/ProjectEndpoints.cs` `PUT .../content`：
  - `WebContracts.HomeworkEditRequest` 加 `string? ClientId = null` 定位会话。
  - 持有会话者：`ExpectedRevision` 不符时**不再 409，直接应用到最新版上**（设备被拦、其他网页编辑者科目不相交，天然安全合并）；布局判定：单会话→全部磁贴 Rect 可改（老师也可排版整个作业）；多会话→所有 Rect 与 Theme/Palette 改动拒绝（403，提示"多位老师同时编辑，布局已锁定"）。
  - 无会话者：完全沿用现行为（409 冲突、EditableSubjects 限制）。
- `Services/PayloadEditor.cs` `ApplyEdit` 加 `IReadOnlySet<string>? layoutSubjects` 参数（null=全部可排；空=布局锁定），Rect 越权时抛 `EditDeniedException`；Theme/Palette 仍要求不限科目的用户（班主任/管理员）。

### 6. 网页控制台 `wwwroot/app.js`（+app.css）
- **新页面 账号管理**（`routes` 加 `#/users`，仅全校管理员可见 nav）：用户列表（`GET /api/admin/users`）+ `modal()` 表单：新建账号（用户名/显示名/角色 老师·管理员；选管理员时出现范围选择器："全校" 或勾选年级组+班级）、分配班级（`PUT .../classes`）、重置密码、启用/停用、删除；复用 `viewClasses`/`viewActions` 现有模式。
- `viewClasses`：范围管理员隐藏新建班级/分组管理按钮（`state.me.adminScope != null`）。
- **作业编辑器**（`classHomework` 闭包，1471–2387）：进入编辑→认领科目（多科目时弹勾选框，默认=我可编辑科目，管理员/班主任默认全选）；锁横幅显示"X老师 正在编辑：数学…"，`LayoutLocked` 时禁用拖拽手柄/主题配色按钮（`bindStage`/`openScheme` 经新 `canLayout` 门控）；`onEditing` 事件实时更新横幅与布局锁；423 显示占用者与科目；释放后经事件解锁。

### 7. 测试
- `tests/ServerLogic/Program.cs`（手写集成 harness，`dotnet run` 运行）：
  - 新增"范围管理员"段：建范围管理员（勾一个组）→ 范围内可建项目/全科编辑/发公告/管考试计划，范围外 403，不能访问用户/班级管理接口；全校管理员不受限。
  - 新增"并发编辑"段：两老师不同科目同时取得会话→互相 423 拒绝改对方科目 Rect、布局锁定；同学科第二个老师申请→423；一方旧修订保存→自动合并不 409；全部退出→布局解锁；大屏占用互斥与 force 接管回归。
  - 调整现有"第二个网页编辑者被拒"断言为新语义。
- 桌面端 `tests/SyncLogic` 不改代码，跑通验证设备侧协议未破坏。

### 8. 客户端仓库（Pancake）同步
- 仅 `Contracts.cs` 逐字复制（步骤 2）。桌面端无逻辑改动（未知事件类型走全量同步、设备流程不变）。
- 提醒（不自动改）：桌面端 `.github/workflows/release.yml` 已固定服务端 commit `a7b227a6`，服务端合入后需按其注释更新 pin。

## 验证
1. 服务端：`dotnet run --project tests/ServerLogic/ServerLogic.csproj`（期望 `PASS: N server checks.`，含新段）。
2. 桌面端：`dotnet run --project tests/SyncLogic/SyncLogic.csproj -p:PancakeServerDir=../PancakeServer/`。
3. 手动走查：启动服务端 → admin 登录网页控制台 → 新建老师 A（语文）与老师 B（数学）+ 一个范围管理员 → 两浏览器分别以 A/B 编辑同一作业：验证科目认领、互锁、布局锁定横幅、自动合并；范围管理员验证范围内外权限与考试页。

## 不涉及
- remoteci 同步（AGENTS.md 要求仅适用于 remoteci 开发，本项目与其无关联，已确认无引用）。
- 桌面端 UI/逻辑改动、`SettingsSchema` 设置项增删（仅整文件恢复）。