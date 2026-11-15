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
        Grid root = new() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 0, 0, 0)) };
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
        Button copy = new() { Content = "复制错误信息", Style = (Style)Application.Current.Resources["BoardSecondaryButtonStyle"] };
        Button ignore = new() { Content = "忽略", Style = (Style)Application.Current.Resources["BoardSecondaryButtonStyle"] };
        Button exit = new() { Content = "退出", Style = (Style)Application.Current.Resources["BoardSecondaryButtonStyle"] };
        Button restart = new() { Content = "重启", Style = (Style)Application.Current.Resources["BoardPrimaryButtonStyle"] };
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

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowChrome.Round(hwnd);
        double scale = WindowChrome.Dpi(hwnd) / 96d;
        int width = (int)Math.Round(680 * scale);
        int height = (int)Math.Round(420 * scale);
        AppWindow.Resize(new SizeInt32(width, height));
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        AppWindow.Move(new PointInt32(
            area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - height) / 2)));
    }
}
