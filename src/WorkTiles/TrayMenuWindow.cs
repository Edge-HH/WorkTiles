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
    private readonly Border _shell;
    private readonly Action<int> _chosen;
    private int _anchorX;
    private int _anchorY;
    private int _fitAttempts;
    private bool _pointerWasDown;
    private bool _fitting;
    private bool _anchored;
    private bool _chose;

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
            RequestedTheme = ElementTheme.Dark,
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
            // 样式还没套上时，按钮会被量成只有一行字那么高。窗口就矮了大约两行，重启和退出落在外面。
            button.MinHeight = 36;
            button.Content = line;
            button.Click += (_, _) => Choose(command);
            root.Children.Add(button);
        }
        void Rule() => root.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 255, 255, 255))
        });
        // 参考图顶部有一行应用图标 + 名称，先放标题再分组菜单项。
        StackPanel header = new() { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(10, 8, 10, 8) };
        header.Children.Add(new FluentIcon
        {
            Symbol = "Board", FontSize = 18,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 236, 236, 236)),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = "WorkTiles", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 244, 244))
        });
        root.Children.Add(header);
        Rule();
        Item("Window", "打开主界面", TrayIcon.CommandOpen);
        Item(tilesVisible ? "Board" : "Grid", tilesVisible ? "隐藏桌面磁贴" : "显示桌面磁贴",
            tilesVisible ? TrayIcon.CommandHideTiles : TrayIcon.CommandShowTiles);
        Rule();
        Item("Settings", "应用设置", TrayIcon.CommandSettings);
        Rule();
        Item("ArrowSync", "重启", TrayIcon.CommandRestart);
        Item("Dismiss", "退出", TrayIcon.CommandExit);
        shell.HorizontalAlignment = HorizontalAlignment.Stretch;
        shell.VerticalAlignment = VerticalAlignment.Stretch;
        shell.Child = root;
        _shell = shell;
        _chosen = chosen;
        Content = shell;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowChrome.Round(hwnd);
        WindowChrome.AsToolWindow(hwnd);
        GetCursorPos(out Point cursor);
        _anchorX = cursor.X;
        _anchorY = cursor.Y;
        // 先按托盘所在屏幕的 DPI 撑开，排版完成后再补一次。无边框窗口的客户区比外框矮，
        // 弹出前量到的高度也经常偏小，底部两行（重启、退出）会留在窗口外面。
        _shell.SizeChanged += (_, _) => FitToContent(hwnd);
        _shell.Loaded += (_, _) => FitToContent(hwnd);
        FitToContent(hwnd);
        _outside.Tick += (_, _) => WatchOutsideClick(hwnd);
        _outside.Start();
        Closed += (_, _) =>
        {
            _chose = true;
            _outside.Stop();
        };
        WindowChrome.ShowWithoutActivating(hwnd);
        DispatcherQueue.TryEnqueue(() => FitToContent(hwnd));
    }

    private void Choose(int command)
    {
        if (_chose) return;
        _chose = true;
        _outside.Stop();
        _chosen(command);
        Close();
    }

    private void FitToContent(nint hwnd)
    {
        if (_chose || _fitting || _fitAttempts >= 4) return;
        _fitting = true;
        try
        {
            if (_anchored) _shell.UpdateLayout();
            _shell.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double desiredW = Math.Max(_shell.MinWidth, _shell.DesiredSize.Width);
            double desiredH = _shell.DesiredSize.Height;
            if (desiredH < 1) return;

            bool laidOut = _shell.ActualWidth > 1 && _shell.ActualHeight > 1;
            double scale = laidOut && _shell.XamlRoot is { RasterizationScale: > 0 } root
                ? root.RasterizationScale
                : ScaleAt(_anchorX, _anchorY, hwnd);
            int width = (int)Math.Ceiling(desiredW * scale);
            int contentPx = (int)Math.Ceiling(desiredH * scale);
            // 外框含标题栏时，只按内容设高度会让客户区更矮，底部的重启和退出被裁掉。
            int frameW = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
            int frameH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
            if (frameH <= 0) frameH = WindowChrome.NonClientHeight(hwnd);
            if (frameH <= 0 && !laidOut) frameH = (int)Math.Ceiling(32 * scale);
            if (frameW > 0 && frameW <= (int)Math.Ceiling(48 * scale)) width += frameW;
            int height = contentPx + Math.Max(0, frameH);
            if (laidOut && desiredH > _shell.ActualHeight + 1)
                height = Math.Max(height, AppWindow.Size.Height + (int)Math.Ceiling((desiredH - _shell.ActualHeight) * scale));
            else if (laidOut && frameH <= 0 && _shell.ActualHeight > desiredH + 1)
                height = contentPx;

            bool resize = Math.Abs(AppWindow.Size.Width - width) > 1 || Math.Abs(AppWindow.Size.Height - height) > 1;
            bool clipped = laidOut && desiredH > _shell.ActualHeight + 1;
            if (!resize && _anchored && !clipped) return;

            _fitAttempts++;
            if (resize) AppWindow.Resize(new SizeInt32(Math.Max(1, width), Math.Max(1, height)));
            MoveAboveAnchor(Math.Max(1, width), Math.Max(1, height));
            _anchored = true;
            if (!laidOut || resize || clipped)
                DispatcherQueue.TryEnqueue(() => FitToContent(hwnd));
        }
        finally { _fitting = false; }
    }

    private void MoveAboveAnchor(int width, int height)
    {
        DisplayArea area = DisplayArea.GetFromPoint(new PointInt32(_anchorX, _anchorY), DisplayAreaFallback.Nearest);
        int x = Math.Clamp(_anchorX, area.WorkArea.X, Math.Max(area.WorkArea.X, area.WorkArea.X + area.WorkArea.Width - width));
        int y = Math.Clamp(_anchorY - height, area.WorkArea.Y, Math.Max(area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - height));
        AppWindow.Move(new PointInt32(x, y));
    }

    private static double ScaleAt(int x, int y, nint hwnd)
    {
        nint monitor = MonitorFromPoint(new Point { X = x, Y = y }, 2);
        if (monitor != 0 && GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi >= 96)
            return dpi / 96d;
        return WindowChrome.Dpi(hwnd) / 96d;
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

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
}
