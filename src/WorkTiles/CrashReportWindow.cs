using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace WorkTiles;

/// <summary>未处理异常时留下来的窗口。外观与“已在运行”提示同一套云母底和底栏按钮。</summary>
internal sealed class CrashReportWindow : Window
{
    private static bool _open;
    private readonly Grid _root;
    private readonly nint _hwnd;
    private bool _positioned;
    private bool _loaded;
    private bool _fitted;

    public static void Present(Exception exception)
    {
        if (_open) return;
        _open = true;
        string text = exception.ToString();
        CrashReportWindow window = new(text);
        window.Closed += (_, _) => _open = false;
        window.Activate();
    }

    private CrashReportWindow(string text)
    {
        Title = "WorkTiles 遇到错误";
        SystemBackdrop = new MicaBackdrop();
        Grid root = new() { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 0, 0, 0)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel body = new() { Spacing = 12, Padding = new Thickness(28, 18, 28, 12) };
        body.Children.Add(new TextBlock
        {
            Text = "软件遇到一个错误，但没有直接退出。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16
        });
        body.Children.Add(new TextBlock
        {
            Text = "可以选择重启、退出、忽略，或复制下面的错误信息。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Opacity = 0.86
        });
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["BoardLineBrush"],
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 0, 0, 0)),
            Child = new ScrollViewer
            {
                Height = 180,
                Padding = new Thickness(10),
                Content = new TextBlock
                {
                    Text = text,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                }
            }
        });
        Grid.SetRow(body, 0);
        root.Children.Add(body);

        Border footer = new()
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["BoardLineBrush"],
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(36, 0, 0, 0)),
            Padding = new Thickness(16, 12, 16, 12)
        };
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        // 底栏按钮和“已在运行”提示同一套：低调深色圆角、无边框。悬停走统一主题画刷，重启用强调色样式悬停。
        Button FooterButton(string text, bool primary)
        {
            Brush idle = primary
                ? (Brush)Application.Current.Resources["BoardBrandBrush"]
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 48, 60));
            Button button = new()
            {
                Content = text,
                MinWidth = 96,
                MinHeight = 36,
                Padding = new Thickness(16, 8, 16, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                Background = idle,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 240, 244)),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            if (primary) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            return button;
        }
        Button copy = FooterButton("复制错误信息", false);
        Button ignore = FooterButton("忽略", false);
        Button exit = FooterButton("退出", false);
        Button restart = FooterButton("重启", true);
        copy.Click += (_, _) =>
        {
            DataPackage package = new();
            package.SetText(text);
            Clipboard.SetContent(package);
        };
        ignore.Click += (_, _) => Close();
        exit.Click += (_, _) => Application.Current.Exit();
        restart.Click += (_, _) =>
        {
            try
            {
                string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WorkTiles.exe");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(ex);
            }
            Application.Current.Exit();
        };
        actions.Children.Add(copy);
        actions.Children.Add(ignore);
        actions.Children.Add(exit);
        actions.Children.Add(restart);
        footer.Child = actions;
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;
        _root = root;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowChrome.Round(_hwnd);
        // 先按当前模板摆一次，Loaded 后再量。按钮模板展开后会更高；外框还含标题栏，只按内容高度设窗口会裁掉底栏。
        root.Loaded += (_, _) =>
        {
            _loaded = true;
            DispatcherQueue.TryEnqueue(PlaceOnScreen);
        };
        PlaceOnScreen();
    }

    private void PlaceOnScreen()
    {
        if (_fitted) return;
        double scale = WindowChrome.Dpi(_hwnd) / 96d;
        int width = (int)Math.Round(680 * scale);
        if (AppWindow.Size.Width != width)
            AppWindow.Resize(new SizeInt32(width, Math.Max(AppWindow.Size.Height, (int)Math.Round(360 * scale))));

        // 按客户区宽度测量，文字换行和最终窗口一致。
        double measureWidth = 680;
        if (scale > 0 && AppWindow.ClientSize.Width > 0)
        {
            double clientDip = AppWindow.ClientSize.Width / scale;
            if (clientDip > 480 && clientDip <= 680) measureWidth = clientDip;
        }
        _root.Measure(new Windows.Foundation.Size(measureWidth, double.PositiveInfinity));
        double content = _root.DesiredSize.Height;
        if (content < 1) return;

        int contentPx = (int)Math.Ceiling(Math.Min(content, 720) * scale);
        int frame = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        bool measuredFrame = frame > 0;
        if (!measuredFrame)
        {
            frame = WindowChrome.NonClientHeight(_hwnd);
            measuredFrame = frame > 0;
        }
        if (!measuredFrame) frame = (int)Math.Ceiling(32 * scale);
        int height = contentPx + frame;
        if (AppWindow.Size.Width != width || AppWindow.Size.Height != height)
            AppWindow.Resize(new SizeInt32(width, height));
        if (!_positioned)
        {
            DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            AppWindow.Move(new PointInt32(
                area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - width) / 2),
                area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - height) / 2)));
            _positioned = true;
        }
        // 模板还没展开时先不锁定，否则 Loaded 那次测量会被跳过，按钮仍然高出窗口。
        if (measuredFrame && _loaded) _fitted = true;
    }
}
