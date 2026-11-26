namespace WorkTiles.Services;

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 2;
    public BoardSettingsState Settings { get; set; } = new();
    public List<SubjectState> Subjects { get; set; } = [];
}

public sealed class NamedPalette
{
    public string Name { get; set; } = "";
    public List<string> Colors { get; set; } = [];
}

public sealed class BoardSettingsState
{
    public const string DefaultTileBodyFontFamily = "HarmonyOS Sans SC";

    public string LayoutMode { get; set; } = "Split";
    public double SplitRatio { get; set; } = 0.4;
    /// <summary>分屏方向：Horizontal 左右分屏（默认），Vertical 上下分屏。窗口大小不再自动改变方向。</summary>
    public string SplitOrientation { get; set; } = "Horizontal";
    /// <summary>分屏时对调两个区域：false 为时钟在前、作业在后，true 为作业在前、时钟在后。</summary>
    public bool SplitSwapped { get; set; }
    public int DockedWidgetLayoutVersion { get; set; }
    // 按布局模式和稳定组件标识保存位置，避免受约束布局覆盖自由布局坐标。
    public Dictionary<string, RegionPlacement> Widgets { get; set; } = new();
    public HashSet<string> CustomizedDockedWidgets { get; set; } = [];
    public bool InfiniteBoard { get; set; }
    public double GridSize { get; set; } = 48;
    public double AutoLayoutGap { get; set; } = 0;
    public bool AutoLayoutAlign { get; set; } = true;
    public bool AutoLayoutResize { get; set; } = true;
    public string GridStyle { get; set; } = "Grid";
    public string GridColor { get; set; } = "#6956565C";
    public double GridLineThickness { get; set; } = 1.6;
    public string GridDotColor { get; set; } = "#8F56565C";
    public double GridDotDiameter { get; set; } = 3;
    public bool ShowGridWhileEditing { get; set; } = true;
    public double TileTitleSize { get; set; } = 29;
    public double TileBodyFontSize { get; set; } = 20;
    public string TileBodyFontFamily { get; set; } = DefaultTileBodyFontFamily;
    public bool TileBodyBold { get; set; }
    public bool TileBodyItalic { get; set; }
    public bool PastePlainTextOnly { get; set; }
    /// <summary>磁贴右下角显示学科名称。默认打开，保持现有看板的大字水印。</summary>
    public bool ShowTileSubjectLabel { get; set; } = true;
    /// <summary>磁贴外框圆角，单位是像素。默认 3，和原来的细圆角一致。</summary>
    public double TileCornerRadius { get; set; } = 3;
    public BackgroundSettings TileBackground { get; set; } = new();
    public bool SharedBackgroundEnabled { get; set; }
    public BackgroundSettings SharedBackground { get; set; } = new();
    public BackgroundSettings ClockBackground { get; set; } = new();
    public BackgroundSettings BoardBackground { get; set; } = new();
    public bool ToolbarIconOnly { get; set; } = true;
    public string ToolbarPosition { get; set; } = "BottomCenter";
    public double ToolbarScale { get; set; } = 1;
    public double ToolbarRadius { get; set; } = 14;
    public double ToolbarBorderThickness { get; set; } = 1;
    // 空字符串表示跟随主题线条色；设为固定颜色后切换深浅主题不再变色。
    public string ToolbarBorderColor { get; set; } = "";
    public double ToolbarHorizontalInset { get; set; } = 24;
    public double ToolbarVerticalInset { get; set; } = 24;
    public bool ToolbarGlass { get; set; }
    public double ToolbarBlur { get; set; } = 20;
    public string ToolbarBackgroundColor { get; set; } = "";
    public double ToolbarBackgroundOpacity { get; set; } = 0.8;
    public bool ToolbarBackgroundColorCleared { get; set; }
    // 时钟区组件卡（天气/噪音）的背景样式：默认完全沿用主题底色，保持升级前的观感。
    public bool WidgetGlass { get; set; }
    public double WidgetBlur { get; set; } = 20;
    public string WidgetBackgroundColor { get; set; } = "";
    public double WidgetBackgroundOpacity { get; set; } = 1;
    public bool WidgetBackgroundColorCleared { get; set; }
    // 时钟区文字外观：默认沿用内置字体与主题文字色；粗细只作用于时间数字。
    // 颜色为空或纯黑/纯白时跟随深浅主题反转（深色显示白色、浅色显示黑色）。
    public string ClockFontFamily { get; set; } = "";
    public string ClockFontWeight { get; set; } = "Normal";
    public string ClockColor { get; set; } = "";
    public bool ToolbarAutoHide { get; set; }
    public double ToolbarAutoHideSeconds { get; set; } = 5;
    public string ToolbarHideAnimation { get; set; } = "Fade";
    public string UpdateSource { get; set; } = "GitHub";
    public string Theme { get; set; } = "Dark";
    public string Palette { get; set; } = "Vivid";
    /// <summary>自定义色系的 9 个颜色，顺序与鲜明色系一一对应。只在色系为自定义时使用。</summary>
    public List<string> CustomPalette { get; set; } = [];
    /// <summary>用户新建的自定义色系。名称从自定义1开始往后排，默认菜单里不出现。</summary>
    public List<NamedPalette> CustomPalettes { get; set; } = [];
    public double NoiseIntervalSeconds { get; set; } = 0.1;
    public string MicrophoneDeviceId { get; set; } = "";
    public double NoiseThresholdDb { get; set; } = 60;
    public bool NoiseAlertEnabled { get; set; }
    public bool PauseNoiseWhenMinimized { get; set; }
    /// <summary>窗口失去焦点时停止噪音监测。最小化已经会失焦，最小化暂停优先显示。</summary>
    public bool PauseNoiseWhenUnfocused { get; set; }
    public double NoiseAlertVolume { get; set; } = 1;
    public double CalibrationTargetDb { get; set; } = 40;
    public bool ShowWeatherAlerts { get; set; } = true;
    /// <summary>班务条显示哪些公告：All（全部生效公告）或 ImportantOnly（仅重要、紧急与置顶）。</summary>
    public string BoardBannerLevels { get; set; } = "All";
    // 保留旧配置字段以兼容已有数据；共享采集格式现在由 Windows 输入设备决定。
    public int MicrophoneSampleRate { get; set; } = 16000;
    public double MicrophoneCalibrationDb { get; set; }
    public string WeatherCityName { get; set; } = "北京";
    public string WeatherCityCode { get; set; } = "101010100";
    public bool GridSnappingEnabled { get; set; } = true;
    public bool AutoUpdateEnabled { get; set; } = true;
    /// <summary>自动保存：开启后改动会在停止操作约 0.6 秒后写盘；关闭后仅在退出软件或手动保存时写入。</summary>
    public bool AutoSaveEnabled { get; set; } = true;
    /// <summary>允许多实例：默认关闭，关闭后同一安装目录只允许同时打开一个软件窗口。</summary>
    public bool AllowMultipleInstances { get; set; }
    /// <summary>已打开时再次启动的行为：Prompt（提示实例重复）、Foreground（打开主界面）、FullScreen（打开主界面并全屏）、None（不执行任何操作）。</summary>
    public string SecondLaunchAction { get; set; } = "Prompt";
    /// <summary>预览（查看模式）下显示顶部菜单栏；默认关闭，菜单栏只在编辑模式显示。</summary>
    public bool ShowMenuBarInPreview { get; set; }
    /// <summary>时钟是否显示秒。关闭后只显示时和分。</summary>
    public bool ClockShowSeconds { get; set; } = true;
    /// <summary>秒数形态：Corner 为右上角小数字，Large 为并入主时间的大数字。</summary>
    public string ClockSecondStyle { get; set; } = "Corner";
    /// <summary>关于页实验功能：打开后设置侧栏显示集控。默认关闭。</summary>
    public bool ShowExperimentalCloud { get; set; }
    /// <summary>关于页实验功能：打开后设置侧栏显示自动填充。默认关闭。</summary>
    public bool ShowExperimentalAutofill { get; set; }
    /// <summary>标题栏向下避让的距离（px），用于避让 ClassIsland 主界面等窗口。</summary>
    public double MenuBarAvoidDistance { get; set; }
    public AutofillSettings Autofill { get; set; } = new();
    public LockSettings Lock { get; set; } = new();
    public DataSettings Data { get; set; } = new();
    public CloudSyncSettings CloudSync { get; set; } = new();
    /// <summary>本机考试安排：不连接集控时也能按设定时间全屏显示；不参与班级布局同步。</summary>
    public LocalExamSettings LocalExam { get; set; } = new();
    /// <summary>在 Windows 桌面上显示当前项目的科目磁贴。本机窗口行为，不参与班级同步。</summary>
    public bool DesktopTilesEnabled { get; set; }
    /// <summary>开机自动启动。开启后才允许静默启动。</summary>
    public bool LaunchAtStartup { get; set; }
    /// <summary>开机自启时不打开主窗口，只留托盘；桌面磁贴若已开启则照常显示。默认开启。</summary>
    public bool SilentStartup { get; set; } = true;
    /// <summary>每个项目一份桌面顺序，键是项目 Id，值是科目名。旧数据按名读取，仅作迁移来源与旧客户端兼容。</summary>
    public Dictionary<string, List<string>> DesktopTileOrder { get; set; } = new();
    /// <summary>每个项目一份桌面顺序，键是项目 Id，值是科目稳定 Id。同名科目也能区分，优先于按名的旧顺序。</summary>
    public Dictionary<string, List<Guid>> DesktopTileOrderIds { get; set; } = new();
    /// <summary>桌面磁贴是否从右上角往空隙里自动排列。关掉后停在当前屏幕位置，不回到开启前的布局。</summary>
    public bool DesktopTilesAutoArrange { get; set; } = true;
    /// <summary>桌面磁贴的显示缩放，只影响桌面窗口，不改作业板上的尺寸。</summary>
    public double DesktopTilesZoom { get; set; } = 1;
    /// <summary>关闭自动排列后，每个项目记住磁贴在工作区里的位置。坐标相对工作区左上角，单位 DIP。</summary>
    public Dictionary<string, List<DesktopTilePlacement>> DesktopTilePlacements { get; set; } = new();
    /// <summary>桌面磁贴外观跟随主界面。默认开启；旧配置缺这个字段时也保持跟随。</summary>
    public bool DesktopTileLookSynced { get; set; } = true;
    /// <summary>关闭同步后，桌面磁贴使用的标题、正文、圆角和背景。同步开启时不参与绘制。</summary>
    public DesktopTileLook DesktopTileLook { get; set; } = new();
    /// <summary>桌面控制窗外观跟随主界面控制窗。默认开启。</summary>
    public bool DesktopCommandLookSynced { get; set; } = true;
    /// <summary>关闭同步后，桌面控制窗使用的大小、圆角、边框和背景。</summary>
    public DesktopCommandLook DesktopCommandLook { get; set; } = new();

    /// <summary>桌面磁贴实际使用的外观。同步开启时取主界面，关闭时取本机副本。</summary>
    public DesktopTileLook EffectiveDesktopTileLook() =>
        DesktopTileLookSynced ? DesktopTileLook.Capture(this) : DesktopTileLook;

    /// <summary>桌面控制窗实际使用的外观。同步开启时取主界面，关闭时取本机副本。</summary>
    public DesktopCommandLook EffectiveDesktopCommandLook() =>
        DesktopCommandLookSynced ? DesktopCommandLook.Capture(this) : DesktopCommandLook;

    /// <summary>再次启动行为只认四种取值；无法识别的旧配置或手改值回到“提示实例重复”。已保存的打开主界面、全屏和不操作仍然有效。</summary>
    public static string NormalizeSecondLaunchAction(string? value) =>
        value is "Prompt" or "Foreground" or "FullScreen" or "None" ? value : "Prompt";
}

/// <summary>
/// 本机考试安排：不连接集控时也能按设定时间全屏显示考试信息。作为本机设置保存，
/// 不参与班级布局同步；服务器下发的启用计划优先于本机安排。
/// </summary>
public sealed class LocalExamSettings
{
    public bool Enabled { get; set; }
    /// <summary>开考前提前进入考试画面的分钟数。</summary>
    public int EnterMinutesBefore { get; set; } = 5;
    /// <summary>大时钟与倒计时是否显示到秒。</summary>
    public bool ShowSeconds { get; set; } = true;
    /// <summary>考场名：显示在考试画面右上角。</summary>
    public string RoomName { get; set; } = "";
    public List<LocalExamDay> Days { get; set; } = [];
}

public sealed class LocalExamDay
{
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public List<LocalExamSegment> Segments { get; set; } = [];
}

/// <summary>本机时间表的一段：Kind 为 exam/break；时间用当天 00:00 起的分钟数，避免时区换算。</summary>
public sealed class LocalExamSegment
{
    public string Kind { get; set; } = WorkTiles.Shared.ExamSegmentKinds.Exam;
    public string Subject { get; set; } = "";
    public string Label { get; set; } = "";
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
}

/// <summary>集控连接设置。未配对时保持空值，主程序继续走原有本地保存路径。</summary>
public sealed class CloudSyncSettings
{
    public string ServerUrl { get; set; } = "";
    public Guid? ClassId { get; set; }
    public string ClassName { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public Guid? DeviceId { get; set; }
    /// <summary>本机设置为“不同步布局”时只同步项目；班级策略为关闭时同样不同步。</summary>
    public bool SyncLayout { get; set; } = true;
    /// <summary>
    /// 已应用或已上传的服务器布局修订号，以及当时与服务器一致的设置 JSON（Base）。
    /// 按键三方合并时，本机相对 Base 的改动上传，服务器相对 Base 的改动应用。
    /// </summary>
    public long LayoutRevision { get; set; }
    public string LayoutBaseJson { get; set; } = "";
    public string LayoutPolicy { get; set; } = "Shared";
    /// <summary>在看板顶部显示公告。</summary>
    public bool ShowBoardBanner { get; set; } = true;
    public bool Enabled => !string.IsNullOrWhiteSpace(ServerUrl) && ClassId.HasValue && !string.IsNullOrWhiteSpace(DeviceToken);
}

/// <summary>数据页设置：作业保留、备份与自动备份属于软件设置，跨项目共享。</summary>
public sealed class DataSettings
{
    /// <summary>应用内作业保留天数，0 表示永久保留；创建时间超出该时间的作业项目会被清除。</summary>
    public int JobRetentionDays { get; set; } = 30;
    public BackupSettings Backup { get; set; } = new();
    public BackupSettings AutoBackup { get; set; } = new();
}

/// <summary>备份的范围与位置；手动备份只用 Scopes，自动备份还使用目录、间隔和保留份数。</summary>
public sealed class BackupSettings
{
    public BackupScopes Scopes { get; set; } = BackupScopes.All;
    public bool Enabled { get; set; }
    public string Directory { get; set; } = "";
    /// <summary>自动备份间隔小时数，至少 1。</summary>
    public double IntervalHours { get; set; } = 24;
    /// <summary>自动备份保留份数，超出后删除最旧的备份。</summary>
    public int KeepCount { get; set; } = 7;
    /// <summary>上次自动备份时间；为空表示尚未备份过，开启后立即执行一次。</summary>
    public DateTime? LastRunAt { get; set; }
}

/// <summary>备份范围：四类内容可任意组合，默认全开。</summary>
[Flags]
public enum BackupScopes
{
    None = 0,
    Jobs = 1,
    CurrentBackground = 2,
    RecentBackgrounds = 4,
    Settings = 8,
    All = Jobs | CurrentBackground | RecentBackgrounds | Settings
}

public static class BackupScopeExtensions
{
    public static bool Has(this BackupScopes scopes, BackupScopes flag) => (scopes & flag) != 0;
    /// <summary>存档里只保留合法位，旧版本或手改的数值不能带进新备份。</summary>
    public static BackupScopes Sanitize(this BackupScopes scopes) => scopes & BackupScopes.All;
}

/// <summary>
/// 锁定设置属于软件设置，跨项目共享；密码只保存 PBKDF2 哈希与盐，2FA 密钥落盘时由 DPAPI 加密。
/// 总开关开启但密码与 2FA 都未配置时不生效，具体判断见 LockService.IsEnforced。
/// </summary>
public sealed class LockSettings
{
    public bool Enabled { get; set; }
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string TotpSecret { get; set; } = "";
    /// <summary>编辑看板前要求验证；默认关闭。</summary>
    public bool RequireAuthForEditing { get; set; }
    /// <summary>打开设置前要求验证；默认开启。</summary>
    public bool RequireAuthForSettings { get; set; } = true;
}

/// <summary>自动填充设置属于软件设置，跨项目共享；默认全部关闭，升级后不改变原有输入行为。</summary>
public sealed class AutofillSettings
{
    public SubjectCompletionSettings Subject { get; set; } = new();
    public HomeworkCompletionSettings Homework { get; set; } = new();
}

/// <summary>学科补全：候选只来自内置清单与设置页手动添加项，程序不会自动收录项目科目。</summary>
public sealed class SubjectCompletionSettings
{
    public bool Enabled { get; set; } = true;
    // 匹配程度：Loose（1 个字符即提示且允许首字母跳字）、Normal、Strict（只认完整前缀）。
    public string MatchLevel { get; set; } = "Normal";
    public List<SubjectSuggestion> Subjects { get; set; } = [];
}

public sealed class SubjectSuggestion
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Color { get; set; } = "#818CF8";
    // 随机配色：开启后每次补全都从预设色板里随机取一个，不再固定使用 Color。
    public bool RandomColor { get; set; }
    // 来源：BuiltIn（内置清单）、Manual（设置页添加）；Learned 仅为兼容旧版本已自动登记的条目。
    public string Source { get; set; } = "Manual";
    public DateTime LastUsedAt { get; set; }
}

/// <summary>作业补全：统计常输入的作业名称，达到阈值后进入候选。</summary>
public sealed class HomeworkCompletionSettings
{
    public bool Enabled { get; set; }
    public bool AutoRecord { get; set; } = true;
    // 收录阈值档位，与学科补全共用 Loose/Normal/Strict 命名，但表示出现次数（2/3/5）。
    public string RecordLevel { get; set; } = "Normal";
    // 记录隔离：Global（全部记进全局桶）或 Subject（自动学习写入当前学科桶，全局桶始终可用）。
    public string Isolation { get; set; } = "Global";
    public List<HomeworkSuggestion> Items { get; set; } = [];
    public List<string> Blocked { get; set; } = [];
}

public sealed class HomeworkSuggestion
{
    public string Text { get; set; } = string.Empty;
    // 来源：Auto（自动学习）、Manual（设置页添加，永不过期）。
    public string Source { get; set; } = "Auto";
    public bool IsGlobal { get; set; } = true;
    public List<string> Subjects { get; set; } = [];
    public int Count { get; set; }
    public bool Promoted { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

public sealed class SubjectState
{
    /// <summary>科目稳定 Id。旧数据为空，读取时生成并随下次保存落盘；桌面顺序与位置按它匹配，同名不再串位。</summary>
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AccentHex { get; set; } = "#818CF8";
    public bool IsAccentExplicit { get; set; } = true;
    public int InkCoordinateVersion { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<HomeworkState> Entries { get; set; } = [];
    public List<InkStrokeState> InkStrokes { get; set; } = [];
}

public sealed class HomeworkState
{
    /// <summary>新增作业时编辑框里的灰色占位提示：只用于显示，不写进作业正文。</summary>
    public const string PlaceholderText = "在这里输入作业内容";

    public string Content { get; set; } = string.Empty;
    public string RtfContent { get; set; } = string.Empty;
    public bool HasHandwriting { get; set; }
    public List<AttachmentState> Attachments { get; set; } = [];
    public List<FontFallbackState> FontFallbacks { get; set; } = [];

    /// <summary>旧版本把占位提示当成正文写入；读取项目时按空内容还原，让它只作为灰色提示显示。</summary>
    public void ClearLegacyPlaceholder()
    {
        if (!string.Equals((Content ?? string.Empty).Trim(), PlaceholderText, StringComparison.Ordinal)) return;
        Content = string.Empty;
        RtfContent = string.Empty;
    }
}

/// <summary>背景样式属于软件设置，资源复制到 data 后不依赖原图片位置。</summary>
public sealed class BackgroundSettings
{
    public string Color { get; set; } = "";
    // 磁贴的颜色层独立于媒体和模糊；空颜色仍表示主题色，显式清除单独保存。
    public double ColorOpacity { get; set; } = 0.8;
    public bool ColorCleared { get; set; }
    public string ImagePath { get; set; } = "";
    public string ImageMode { get; set; } = "Zoom";
    public bool PlaylistEnabled { get; set; }
    public List<string> Playlist { get; set; } = [];
    public bool Shuffle { get; set; }
    public bool SwitchOnTimer { get; set; } = true;
    public double SwitchIntervalSeconds { get; set; } = 60;
    public bool SwitchOnMediaEnded { get; set; } = true;
    public bool Glass { get; set; }
    public double Blur { get; set; } = 20;
}

/// <summary>桌面磁贴可脱离主界面的外观。字段与主界面磁贴外观一一对应，不含粘贴行为。</summary>
public sealed class DesktopTileLook
{
    public double TitleSize { get; set; } = 29;
    public double BodyFontSize { get; set; } = 20;
    public string BodyFontFamily { get; set; } = BoardSettingsState.DefaultTileBodyFontFamily;
    public bool BodyBold { get; set; }
    public bool BodyItalic { get; set; }
    public bool ShowSubjectLabel { get; set; } = true;
    public double CornerRadius { get; set; } = 3;
    public BackgroundSettings Background { get; set; } = new();

    public static DesktopTileLook Capture(BoardSettingsState settings)
    {
        DesktopTileLook look = new()
        {
            TitleSize = settings.TileTitleSize,
            BodyFontSize = settings.TileBodyFontSize,
            BodyFontFamily = settings.TileBodyFontFamily,
            BodyBold = settings.TileBodyBold,
            BodyItalic = settings.TileBodyItalic,
            ShowSubjectLabel = settings.ShowTileSubjectLabel,
            CornerRadius = settings.TileCornerRadius,
            Background = new()
        };
        CopyBackground(look.Background, settings.TileBackground);
        return look;
    }

    /// <summary>把主界面磁贴外观抄进已有副本，不换掉背景对象，设置页编辑器仍持有同一份引用。</summary>
    public void CopyFrom(BoardSettingsState settings)
    {
        TitleSize = settings.TileTitleSize;
        BodyFontSize = settings.TileBodyFontSize;
        BodyFontFamily = settings.TileBodyFontFamily;
        BodyBold = settings.TileBodyBold;
        BodyItalic = settings.TileBodyItalic;
        ShowSubjectLabel = settings.ShowTileSubjectLabel;
        CornerRadius = settings.TileCornerRadius;
        Background ??= new();
        CopyBackground(Background, settings.TileBackground);
    }

    public static void CopyBackground(BackgroundSettings target, BackgroundSettings source)
    {
        target.Color = source.Color;
        target.ColorOpacity = source.ColorOpacity;
        target.ColorCleared = source.ColorCleared;
        target.ImagePath = source.ImagePath;
        target.ImageMode = source.ImageMode;
        target.PlaylistEnabled = source.PlaylistEnabled;
        target.Shuffle = source.Shuffle;
        target.SwitchOnTimer = source.SwitchOnTimer;
        target.SwitchIntervalSeconds = source.SwitchIntervalSeconds;
        target.SwitchOnMediaEnded = source.SwitchOnMediaEnded;
        target.Glass = source.Glass;
        target.Blur = source.Blur;
        target.Playlist ??= [];
        target.Playlist.Clear();
        if (source.Playlist is not null)
            target.Playlist.AddRange(source.Playlist);
    }
}

/// <summary>桌面控制窗可脱离主界面的外观。只包含桌面控制窗实际绘制的项。</summary>
public sealed class DesktopCommandLook
{
    public double Scale { get; set; } = 1;
    public double Radius { get; set; } = 14;
    public double BorderThickness { get; set; } = 1;
    public string BorderColor { get; set; } = "";
    public string BackgroundColor { get; set; } = "";
    public double BackgroundOpacity { get; set; } = 0.8;
    public bool BackgroundColorCleared { get; set; }
    public bool Glass { get; set; }
    public double Blur { get; set; } = 20;

    public static DesktopCommandLook Capture(BoardSettingsState settings) => new()
    {
        Scale = settings.ToolbarScale,
        Radius = settings.ToolbarRadius,
        BorderThickness = settings.ToolbarBorderThickness,
        BorderColor = settings.ToolbarBorderColor,
        BackgroundColor = settings.ToolbarBackgroundColor,
        BackgroundOpacity = settings.ToolbarBackgroundOpacity,
        BackgroundColorCleared = settings.ToolbarBackgroundColorCleared,
        Glass = settings.ToolbarGlass,
        Blur = settings.ToolbarBlur
    };

    public void CopyFrom(BoardSettingsState settings)
    {
        Scale = settings.ToolbarScale;
        Radius = settings.ToolbarRadius;
        BorderThickness = settings.ToolbarBorderThickness;
        BorderColor = settings.ToolbarBorderColor;
        BackgroundColor = settings.ToolbarBackgroundColor;
        BackgroundOpacity = settings.ToolbarBackgroundOpacity;
        BackgroundColorCleared = settings.ToolbarBackgroundColorCleared;
        Glass = settings.ToolbarGlass;
        Blur = settings.ToolbarBlur;
    }
}

/// <summary>桌面磁贴在工作区里的自由位置。宽高仍用科目自己的尺寸，这里只记左上角。</summary>
public sealed class DesktopTilePlacement
{
    /// <summary>科目稳定 Id。旧数据为空，按名匹配兜底；命中后回填 Id，之后按 Id 匹配。</summary>
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class RegionPlacement
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>缺失字体的原始名称与 UTF-16 范围，显示回退不能抹掉用户的字体选择。</summary>
public sealed class FontFallbackState
{
    public int Start { get; set; }
    public int Length { get; set; }
    public string Family { get; set; } = "";
}

public sealed class AttachmentState
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "文件";
    public string Path { get; set; } = string.Empty;
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double ViewportHeight { get; set; } = 180;
    public double FrameWidth { get; set; } = 360;
    public double AspectRatio { get; set; }
    public double Rotation { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
}

public sealed class InkStrokeState
{
    public string Color { get; set; } = "#FFF7F7F9";
    public double Thickness { get; set; }
    public double TipScaleX { get; set; } = 1;
    public double TipScaleY { get; set; } = 1;
    public List<PointState> Points { get; set; } = [];
}

public sealed class PointState
{
    public double X { get; set; }
    public double Y { get; set; }
}

