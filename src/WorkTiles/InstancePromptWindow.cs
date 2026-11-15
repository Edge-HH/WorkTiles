using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WorkTiles.Controls;

namespace WorkTiles;

/// <summary>
/// 第二次启动时的提示。版式对齐常见的“已在运行”窗口：说明、重启当前实例，以及右下角取消。
/// 尺寸按显示器 DPI 换算，避免高缩放下客户区比内容小、按钮被裁掉。
/// </summary>
internal sealed class InstancePromptWindow : Window
{
    private readonly TextBlock _message;
    private readonly Button _restart;
    private readonly Button _cancel;
    private bool _restarting;
    private bool _dismissed;

    public InstancePromptWindow(Func<Task> replace, Action back)
    {
        Title = "WorkTiles 已在运行";
        Grid root = new() { Background = (Brush)Application.Current.Resources["BoardBackgroundBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel body = new() { Spacing = 18, Padding = new Thickness(28, 22, 28, 12) };
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
            Opacity = 0.9
        };
        _restart = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 8, 12, 8)
        };
        StackPanel restartContent = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        restartContent.Children.Add(new FluentIcon
        {
            Symbol = "ArrowSync",
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)Application.Current.Resources["BoardBrandBrush"]
        });
        StackPanel restartText = new() { Spacing = 2 };
        restartText.Children.Add(new TextBlock
        {
            Text = "重启当前实例",
            FontSize = 15,
            Foreground = (Brush)Application.Current.Resources["BoardBrandBrush"]
        });
        restartText.Children.Add(new TextBlock
        {
            Text = "结束正在运行的 WorkTiles 实例，然后再次启动本实例。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });
        restartContent.Children.Add(restartText);
        _restart.Content = restartContent;
        body.Children.Add(_message);
        body.Children.Add(detail);
        body.Children.Add(_restart);
        Grid.SetRow(body, 0);
        root.Children.Add(body);

        Border footer = new()
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["BoardLineBrush"],
            Background = (Brush)Application.Current.Resources["BoardSurfaceBrush"],
            Padding = new Thickness(20, 14, 20, 14)
        };
        _cancel = new Button
        {
            Content = "取消",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 120,
            Style = (Style)Application.Current.Resources["BoardSecondaryButtonStyle"]
        };
        _cancel.Click += (_, _) => Dismiss(back);
        _restart.Click += async (_, _) =>
        {
            if (_restarting) return;
            _restarting = true;
            _restart.IsEnabled = false;
            _cancel.IsEnabled = false;
            await replace();
        };
        footer.Child = _cancel;
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;

        Closed += (_, _) =>
        {
            if (_restarting || _dismissed) return;
            _dismissed = true;
            back();
        };
        PlaceOnScreen();
    }

    public void SetBusy(string message)
    {
        _message.Text = message;
        _restart.IsEnabled = false;
    }

    public void AllowBack()
    {
        _restarting = false;
        _restart.IsEnabled = true;
        _cancel.IsEnabled = true;
    }

    private void Dismiss(Action back)
    {
        if (_dismissed || _restarting) return;
        _dismissed = true;
        back();
        Close();
    }

    private void PlaceOnScreen()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96d;
        int width = (int)Math.Round(760 * scale);
        int height = (int)Math.Round(420 * scale);
        AppWindow.Resize(new SizeInt32(width, height));
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        AppWindow.Move(new PointInt32(
            area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - height) / 2)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
