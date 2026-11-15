using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WorkTiles.Controls;

namespace WorkTiles;

/// <summary>
/// 第二次启动时的提示。控制栏和窗口同一底色，大字标题写明已经在运行，下面是两行操作。
/// 右下角取消和关闭窗口都只结束这次启动，不会把已经在运行的主界面叫到前面。
/// </summary>
internal sealed class InstancePromptWindow : Window
{
    private readonly Grid _root;
    private readonly TextBlock _message;
    private readonly Button _restart;
    private readonly Button _return;
    private readonly Button _cancel;
    private bool _restarting;
    private bool _dismissed;
    private bool _positioned;
    private bool _placing;
    private double _placedScale;

    public InstancePromptWindow(Func<Task> replace, Action back)
    {
        Title = "WorkTiles 已在运行";
        SystemBackdrop = new MicaBackdrop();
        _root = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 0, 0, 0)) };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 系统标题栏换成 WinUI 控制栏，底色跟窗口同一块，按钮浮在右上角。
        Grid drag = new()
        {
            Height = 32,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        Grid.SetRow(drag, 0);
        _root.Children.Add(drag);

        StackPanel body = new() { Spacing = 10, Padding = new Thickness(28, 4, 28, 8) };
        body.Children.Add(new TextBlock
        {
            Text = "WorkTiles 已在运行",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        });
        // 平时不显示。只有正在替换实例、或替换失败时，才把这一行说明放出来。
        _message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            Visibility = Visibility.Collapsed
        };
        _restart = ActionRow("ArrowSync", "重启当前实例", "结束正在运行的 WorkTiles 实例，然后再次启动本实例。");
        _return = ActionRow("Window", "返回已打开的实例", "回到已经在运行的 WorkTiles，这次启动不会继续。");
        body.Children.Add(_message);
        body.Children.Add(_restart);
        body.Children.Add(_return);
        Grid.SetRow(body, 1);
        _root.Children.Add(body);

        // 取消用 WinUI 默认按钮，不再自绘底色和描边，上面也不加分隔线。
        Grid footer = new() { Padding = new Thickness(16, 8, 16, 16) };
        _cancel = new Button
        {
            Content = "取消",
            HorizontalAlignment = HorizontalAlignment.Right
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
        footer.Children.Add(_cancel);
        Grid.SetRow(footer, 2);
        _root.Children.Add(footer);
        Content = _root;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(drag);
        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 200, 200, 204);
        titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;

        Closed += (_, _) => _dismissed = true;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowChrome.Round(hwnd);
        // 显示之前窗口 DPI 常常还是 96。缩放稳定后再量一次，否则客户区装不下取消按钮。
        _root.Loaded += (_, _) =>
        {
            if (_root.XamlRoot is not null)
                _root.XamlRoot.Changed += (_, _) => PlaceOnScreen();
            DispatcherQueue.TryEnqueue(PlaceOnScreen);
        };
        DispatcherQueueTimer fitTimer = DispatcherQueue.CreateTimer();
        fitTimer.Interval = TimeSpan.FromMilliseconds(80);
        int fitTicks = 0;
        fitTimer.Tick += (_, _) =>
        {
            PlaceOnScreen();
            fitTicks++;
            uint dpi = WindowChrome.Dpi(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (fitTicks >= 8 || (dpi >= 96 && Math.Abs(_placedScale - dpi / 96d) < 0.01 && fitTicks >= 2))
                fitTimer.Stop();
        };
        fitTimer.Start();
        PlaceOnScreen();
    }

    public void SetBusy(string message)
    {
        _message.Text = message;
        _message.Visibility = Visibility.Visible;
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
            // 悬停底向左伸出一截，图标仍对齐原来的文字起点。
            Margin = new Thickness(-16, 0, 0, 0),
            Padding = new Thickness(16, 6, 8, 6)
        };
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new FluentIcon
        {
            Symbol = symbol,
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        });
        StackPanel text = new() { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
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
        if (_placing) return;
        _placing = true;
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = _root.XamlRoot?.RasterizationScale ?? 0;
            if (scale < 1) scale = WindowChrome.Dpi(hwnd) / 96d;
            if (scale < 1) scale = 1;
            int width = (int)Math.Round(640 * scale);
            if (AppWindow.Size.Width != width)
                AppWindow.Resize(new SizeInt32(width, Math.Max(AppWindow.Size.Height, (int)Math.Round(320 * scale))));

            // 按目标客户区宽度测量，文字换行和最终窗口一致。
            double measureWidth = 640;
            if (AppWindow.ClientSize.Width > 0)
            {
                double clientDip = AppWindow.ClientSize.Width / scale;
                if (clientDip > 480 && clientDip <= 640) measureWidth = clientDip;
            }
            _root.Measure(new Windows.Foundation.Size(measureWidth, double.PositiveInfinity));
            double content = _root.DesiredSize.Height;
            if (content < 1) return;

            int contentPx = (int)Math.Ceiling(content * scale);
            // 控制栏画在客户区里，高度已经算进内容。只补很薄的窗口边，不能再加一截系统标题栏。
            int frame = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
            if (frame > (int)Math.Round(16 * scale)) frame = 0;
            int height = contentPx + frame;
            bool same = AppWindow.Size.Width == width && AppWindow.Size.Height == height && Math.Abs(_placedScale - scale) < 0.01;
            if (!same)
                AppWindow.Resize(new SizeInt32(width, height));
            if (!_positioned)
            {
                DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                AppWindow.Move(new PointInt32(
                    area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - width) / 2),
                    area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - height) / 2)));
                _positioned = true;
            }
            _placedScale = scale;
        }
        finally
        {
            _placing = false;
        }
    }
}
