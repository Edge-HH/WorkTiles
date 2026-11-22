using Microsoft.UI.Xaml;
using WorkTiles.Services;

namespace WorkTiles;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        SystemAccent.Start();
        UnhandledException += (_, args) =>
        {
            Exception exception = args.Exception ?? new Exception(args.Message);
            WriteCrashLog(exception);
            if (IsAutomationLaunch()) return;
            args.Handled = true;
            try { CrashReportWindow.Present(exception); }
            catch (Exception presentFailed) { WriteCrashLog(presentFailed); }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
    }

    internal static void WriteCrashLog(Exception? exception)
    {
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "crash.log");
            // 崩溃日志无限追加会撑大便携目录；超过 1 MB 滚存一份旧日志，总量封顶约 2 MB。
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {exception}\n");
        }
        catch
        {
            // 崩溃记录不能覆盖原始异常。
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        SystemAccent.Apply();
        LaunchArgs launchArgs = LaunchArgs.Parse(Environment.GetCommandLineArgs().Skip(1));
        // 发布流水线使用真实窗口及资源加载路径；隔离复制目录由验证脚本负责。
        if (Environment.GetCommandLineArgs().Contains("--smoke-test"))
        {
            _window = new MainWindow(false, "verification");
            ((FrameworkElement)_window.Content).Loaded += (_, _) => _window.DispatcherQueue.TryEnqueue(() =>
            {
                ((FrameworkElement)_window.Content).UpdateLayout();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-ok.txt"), "WINDOW_LOADED");
                _window.Close();
            });
            _window.Activate();
            return;
        }
        // --fullscreen 与默认行为一致，显式写出是为了让参数表完整；--windowed 才是切换窗口模式。
        bool startFullScreen = launchArgs.Windowed is not true;
#if WORKTILES_UI_TESTS
        // LaunchArgs 只解析常规启动开关；验证分支仍按子串匹配 --verify-*，这里保留原始命令行供其使用。
        string commandLine = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
        if (commandLine.Contains("--verify-media-performance", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleMediaPerformanceVerification();
            _window.Activate();
            return;
        }
        if (commandLine.Contains("--verify-background-media", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleBackgroundMediaVerification();
            _window.Activate();
            return;
        }
        if (commandLine.Contains("--verify-fullscreen-hint", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleFullScreenHintVerification();
            _window.Activate();
            return;
        }
        if (commandLine.Contains("--verify-font-audit", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleFontAuditVerification();
            _window.Activate();
            return;
        }
        if (commandLine.Contains("--verify-grid-coverage", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleGridCoverageVerification();
            _window.Activate();
            return;
        }
        if (commandLine.Contains("--verify-image-drag", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleImageDragVerification();
            _window.Activate();
            return;
        }
        if (commandLine.Contains("--verify-selection", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleSelectionVerification();
            _window.Activate();
            return;
        }
        if (commandLine.Contains("--verify-ui", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleUiVerification();
            _window.Activate();
            return;
        }
        if (commandLine.Contains("--verify-autofill-keys", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleAutofillKeyVerification();
            _window.Activate();
            return;
        }
        if (commandLine.Contains("--verify-autofill-ime", StringComparison.OrdinalIgnoreCase))
        {
            _window = new MainWindow(false, "verification");
            _window.ScheduleAutofillImeDiagnostic();
            _window.Activate();
            return;
        }
#endif
        // 同一安装目录只开一个窗口。已经有实例时按“重复打开操作”处理。
        // 冒烟与验证模式已在上面提前返回，各自跑隔离副本，不参与互斥。
        SingleInstanceService instanceGate = SingleInstanceService.Start();
        if (!instanceGate.IsFirstInstance)
        {
            HandleSecondInstance(instanceGate, startFullScreen, launchArgs);
            return;
        }
        StartMainWindow(instanceGate, startFullScreen, launchArgs);
    }

    private void HandleSecondInstance(SingleInstanceService instanceGate, bool startFullScreen, LaunchArgs launchArgs)
    {
        string action = SingleInstanceService.ReadLaunchSettings().SecondLaunchAction;
        // 带项目、视图或窗口模式参数时不弹提示，交给已经打开的窗口执行。
        if (SingleInstanceService.ShouldPrompt(action, launchArgs))
        {
            ShowInstancePrompt(instanceGate, startFullScreen, launchArgs);
            return;
        }
        instanceGate.ForwardToRunningInstance(action, launchArgs);
        instanceGate.Dispose();
        Exit();
    }

    private void StartMainWindow(SingleInstanceService instanceGate, bool startFullScreen, LaunchArgs launchArgs)
    {
        _window = new MainWindow(startFullScreen && !launchArgs.Silent, launchArgs.View, launchArgs.Project, launchArgs.Silent);
        if (launchArgs.LayoutProbe) _window.EnableLayoutProbe();
        _window.AttachSingleInstanceGate(instanceGate);
        _window.Activate();
        if (launchArgs.Silent) _window.AppWindow.Hide();
    }

    private void ShowInstancePrompt(SingleInstanceService instanceGate, bool startFullScreen, LaunchArgs launchArgs)
    {
        InstancePromptWindow? prompt = null;
        bool becameMain = false;
        prompt = new InstancePromptWindow(async () =>
        {
            prompt!.SetBusy("正在退出正在运行的实例…");
            SingleInstanceService? owned = await SingleInstanceService.ReplaceRunningInstanceAsync(instanceGate);
            if (owned is null)
            {
                prompt.SetBusy("没能退出正在运行的实例，请先手动关掉它。");
                prompt.AllowBack();
                return;
            }
            becameMain = true;
            StartMainWindow(owned, startFullScreen, launchArgs);
            prompt.Close();
        }, () =>
        {
            instanceGate.ForwardToRunningInstance(SingleInstanceService.ForegroundAction);
            instanceGate.Dispose();
            prompt!.Close();
        });
        prompt.Closed += (_, _) =>
        {
            if (becameMain) return;
            instanceGate.Dispose();
            Exit();
        };
        prompt.Activate();
    }

    private static bool IsAutomationLaunch()
    {
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (argument.Contains("--smoke-test", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("--verify-", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
