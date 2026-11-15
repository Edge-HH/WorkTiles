using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WorkTiles.Controls;

namespace WorkTiles;

/// <summary>
/// 第二次启动时的提示。说明下面是两行操作：重启当前实例，以及返回已打开的实例。
/// 右下角取消和关闭窗口都只结束这次启动，不会把已经在运行的主界面叫到前面。
/// </summary>
internal sealed class InstancePromptWindow : Window
{
    private readonly TextBlock _message;
    private readonly Button _restart;
    private readonly Button _return;
    private readonly Button _cancel;
    private bool _restarting;
    private bool _dismissed;

    public InstancePromptWindow(Func<Task> replace, Action back)
    {
        Title = "WorkTiles 已在运行";
        SystemBackdrop = new MicaBackdrop();
        Grid root = new() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 0, 0, 0)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel body = new() { Spacing = 14, Padding = new Thickness(28, 18, 28, 8) };
        _message = new TextBlock
        {
            Text = "WorkTiles 已经启动，请通过任务栏托盘图标进行设置等操作。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15
        };
        TextBlock detail = new()
        {
            Text = "如果您无法看到主界面，可能是因为开机静默启动后没有打开主窗口。可在托盘菜单中选择「打开主界面」。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            Opacity = 0.86
        };
        _restart = ActionRow("ArrowSync", "重启当前实例", "结束正在运行的 WorkTiles 实例，然后再次启动本实例。");
        _return = ActionRow("Window", "返回已打开的实例", "回到已经在运行的 WorkTiles，这次启动不会继续。");
        body.Children.Add(_message);
        body.Children.Add(detail);
        body.Children.Add(_restart);
        body.Children.Add(_return);
        Grid.SetRow(body, 0);
        root.Children.Add(body);

        Border footer = new()
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["BoardLineBrush"],
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(36, 0, 0, 0)),
            Padding = new Thickness(16, 12, 16, 12)
        };
        _cancel = new Button
        {
            Content = "取消",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 140,
            Style = (Style)Application.Current.Resources["BoardSecondaryButtonStyle"]
        };
        _cancel.Click += (_, _) => CloseQuietly();
        _return.Click += (_, _) =>
        {
            if (_dismissed || _restarting) return;
            _dismissed = true;
            back();
            Close();
        };
        _restart.Click += async (_, _) =>
        {
            if (_restarting) return;
            _restarting = true;
            _restart.IsEnabled = false;
            _return.IsEnabled = false;
            _cancel.IsEnabled = false;
            await replace();
        };
        footer.Child = _cancel;
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;

        Closed += (_, _) => _dismissed = true;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowChrome.Round(hwnd);
        PlaceOnScreen();
    }

    public void SetBusy(string message)
    {
        _message.Text = message;
        _restart.IsEnabled = false;
        _return.IsEnabled = false;
    }

    public void AllowBack()
    {
        _restarting = false;
        _restart.IsEnabled = true;
        _return.IsEnabled = true;
        _cancel.IsEnabled = true;
    }

    private void CloseQuietly()
    {
        if (_dismissed || _restarting) return;
        _dismissed = true;
        Close();
    }

    private static Button ActionRow(string symbol, string title, string subtitle)
    {
        Button button = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 8, 6)
        };
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new FluentIcon
        {
            Symbol = symbol,
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)Application.Current.Resources["BoardBrandBrush"]
        });
        StackPanel text = new() { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            Foreground = (Brush)Application.Current.Resources["BoardBrandBrush"]
        });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(text);
        button.Content = content;
        return button;
    }

    private void PlaceOnScreen()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = WindowChrome.Dpi(hwnd) / 96d;
        int width = (int)Math.Round(640 * scale);
        int height = (int)Math.Round(360 * scale);
        AppWindow.Resize(new SizeInt32(width, height));
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        AppWindow.Move(new PointInt32(
            area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - height) / 2)));
    }
}
