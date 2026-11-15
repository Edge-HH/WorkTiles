using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WorkTiles.Controls;
using WorkTiles.Services;

namespace WorkTiles;

/// <summary>
/// 托盘菜单。单击或右键都打开它，不把主窗口抬到前面，也不在任务栏再占一个按钮。
/// 版式对齐常见的深色半透明菜单：左侧小图标、紧凑行距、圆角。
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private static TrayMenuWindow? _current;
    private readonly DispatcherTimer _outside = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool _pointerWasDown;

    public static void Show(bool tilesVisible, Action<int> chosen)
    {
        _current?.Close();
        TrayMenuWindow window = new(tilesVisible, chosen);
        _current = window;
        window.Closed += (_, _) => { if (_current == window) _current = null; };
    }

    private TrayMenuWindow(bool tilesVisible, Action<int> chosen)
    {
        Title = "WorkTiles";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        Border shell = new()
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(170, 32, 32, 32)),
            Padding = new Thickness(4),
            MinWidth = 220
        };
        StackPanel root = new() { Spacing = 1 };
        void Item(string symbol, string text, int command)
        {
            Button button = new()
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 7, 10, 7),
                CornerRadius = new CornerRadius(4)
            };
            StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
            line.Children.Add(new FluentIcon
            {
                Symbol = symbol,
                FontSize = 16,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 236, 236, 236)),
                VerticalAlignment = VerticalAlignment.Center
            });
            line.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 244, 244))
            });
            button.Content = line;
            SolidColorBrush idle = new(Microsoft.UI.Colors.Transparent);
            SolidColorBrush hover = new(Windows.UI.Color.FromArgb(255, 58, 58, 62));
            button.PointerEntered += (_, _) => button.Background = hover;
            button.PointerExited += (_, _) => button.Background = idle;
            button.Click += (_, _) =>
            {
                Close();
                chosen(command);
            };
            root.Children.Add(button);
        }
        void Rule() => root.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 255, 255, 255))
        });
        Item("Window", "打开主界面", TrayIcon.CommandOpen);
        Item(tilesVisible ? "Board" : "Grid", tilesVisible ? "隐藏桌面磁贴" : "显示桌面磁贴",
            tilesVisible ? TrayIcon.CommandHideTiles : TrayIcon.CommandShowTiles);
        Rule();
        Item("Settings", "应用设置", TrayIcon.CommandSettings);
        Rule();
        Item("ArrowSync", "重启", TrayIcon.CommandRestart);
        Item("Dismiss", "退出", TrayIcon.CommandExit);
        shell.Child = root;
        Content = shell;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowChrome.Round(hwnd);
        WindowChrome.AsToolWindow(hwnd);
        double scale = WindowChrome.Dpi(hwnd) / 96d;
        root.Measure(new Windows.Foundation.Size(280, 800));
        double contentWidth = root.DesiredSize.Width;
        double contentHeight = root.DesiredSize.Height;
        if (contentWidth < 160 || contentWidth > 360) contentWidth = 220;
        if (contentHeight < 120 || contentHeight > 320) contentHeight = 204;
        int width = (int)Math.Round((contentWidth + 8) * scale);
        int height = (int)Math.Round((contentHeight + 8) * scale);
        AppWindow.Resize(new SizeInt32(width, height));
        GetCursorPos(out Point cursor);
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        int x = Math.Clamp(cursor.X, area.WorkArea.X, Math.Max(area.WorkArea.X, area.WorkArea.X + area.WorkArea.Width - width));
        int y = Math.Clamp(cursor.Y - height, area.WorkArea.Y, Math.Max(area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - height));
        AppWindow.Move(new PointInt32(x, y));
        _outside.Tick += (_, _) => WatchOutsideClick(hwnd);
        _outside.Start();
        Closed += (_, _) => _outside.Stop();
        WindowChrome.ShowWithoutActivating(hwnd);
    }

    private void WatchOutsideClick(nint hwnd)
    {
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        bool escape = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
        if (escape || (down && !_pointerWasDown && !PointerInside(hwnd)))
            Close();
        _pointerWasDown = down;
    }

    private static bool PointerInside(nint hwnd)
    {
        if (!GetCursorPos(out Point cursor) || !GetWindowRect(hwnd, out Rect rect)) return true;
        return cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
}
