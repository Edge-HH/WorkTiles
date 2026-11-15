using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace WorkTiles;

/// <summary>未处理异常时留下来的窗口。可以重启、退出、忽略，或把错误文本复制出去。</summary>
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
        StackPanel root = new() { Spacing = 14, Padding = new Thickness(24), Width = 640 };
        root.Children.Add(new TextBlock
        {
            Text = "软件遇到一个错误，但没有直接退出。可以选择重启、退出、忽略，或复制下面的错误信息。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15
        });
        root.Children.Add(new ScrollViewer
        {
            Height = 220,
            Content = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true
            }
        });
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Button restart = new() { Content = "重启", Style = (Style)Application.Current.Resources["BoardPrimaryButtonStyle"] };
        Button exit = new() { Content = "退出" };
        Button ignore = new() { Content = "忽略" };
        Button copy = new() { Content = "复制错误信息" };
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
        exit.Click += (_, _) => Application.Current.Exit();
        ignore.Click += (_, _) => Close();
        copy.Click += (_, _) =>
        {
            DataPackage package = new();
            package.SetText(text);
            Clipboard.SetContent(package);
        };
        actions.Children.Add(copy);
        actions.Children.Add(ignore);
        actions.Children.Add(exit);
        actions.Children.Add(restart);
        root.Children.Add(actions);
        Content = root;
    }
}
