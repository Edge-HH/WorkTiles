using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace WorkTiles;

/// <summary>第二次启动时的选择窗口。没有主窗口，所以不能用依赖 XamlRoot 的对话框。</summary>
internal sealed class InstancePromptWindow : Window
{
    private readonly TextBlock _message;
    private readonly Button _replace;
    private readonly Button _back;

    public InstancePromptWindow(Func<Task> replace, Action back)
    {
        Title = "WorkTiles";
        StackPanel root = new() { Spacing = 20, Padding = new Thickness(28), Width = 560 };
        _message = new TextBlock
        {
            Text = "检测到正在运行的WorkTiles实例",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 18
        };
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _replace = new Button { Content = "退出正在运行的实例并启动", Style = (Style)Application.Current.Resources["BoardPrimaryButtonStyle"] };
        _back = new Button { Content = "返回已启动的实例", Style = (Style)Application.Current.Resources["BoardSecondaryButtonStyle"] };
        _replace.Click += async (_, _) =>
        {
            _replace.IsEnabled = false;
            _back.IsEnabled = false;
            await replace();
        };
        _back.Click += (_, _) => back();
        actions.Children.Add(_replace);
        actions.Children.Add(_back);
        root.Children.Add(_message);
        root.Children.Add(actions);
        Content = root;
        const int width = 640, height = 240;
        AppWindow.Resize(new SizeInt32(width, height));
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        AppWindow.Move(new PointInt32(
            area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - height) / 2)));
    }

    public void SetBusy(string message)
    {
        _message.Text = message;
        _replace.IsEnabled = false;
    }

    public void AllowBack() => _back.IsEnabled = true;
}
