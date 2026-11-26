using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WorkTiles.Controls;
using WorkTiles.Services;

namespace WorkTiles;

/// <summary>
/// 托盘菜单。单击或右键都打开它，不把主窗口抬到前面，也不在任务栏再占一个按钮。
/// 不用 WinUI Window：那层系统底板会沿圆角留一条浅色边，拐角跟着发虚。
/// 圆角只画在透明弹出窗口的内容上，圆角外是真正的透明。
/// </summary>
internal static class TrayMenuWindow
{
    private const float Corner = 8;
    private static Session? _current;

    public static void Show(bool tilesVisible, Action<int> chosen)
    {
        _current?.Close();
        _current = new Session(tilesVisible, chosen);
    }

    private sealed class Session
    {
        private readonly BorderlessPopupHost _host = new();
        private readonly DispatcherTimer _outside = new() { Interval = TimeSpan.FromMilliseconds(60) };
        private readonly Border _shell;
        private readonly Action<int> _chosen;
        private readonly int _anchorX;
        private readonly int _anchorY;
        private CompositionRoundedRectangleGeometry? _clip;
        private int _fitAttempts;
        private bool _pointerWasDown;
        private bool _fitting;
        private bool _placed;
        private bool _shown;
        private bool _chose;
        private bool _closed;

        public Session(bool tilesVisible, Action<int> chosen)
        {
            _chosen = chosen;
            GetCursorPos(out Point cursor);
            _anchorX = cursor.X;
            _anchorY = cursor.Y;

            Grid root = new()
            {
                RequestedTheme = ElementTheme.Dark,
                Background = null,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Windows.UI.Color tint = Windows.UI.Color.FromArgb(220, 32, 32, 32);
            Border shell = new()
            {
                RequestedTheme = ElementTheme.Dark,
                CornerRadius = new CornerRadius(Corner),
                Background = new DesktopGlassBrush(24, tint) { FallbackColor = tint },
                Padding = new Thickness(4),
                MinWidth = 220,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            StackPanel panel = new() { Spacing = 1 };
            void Item(string symbol, string text, int command)
            {
                Button button = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    // 完全透明的白会让悬停第一帧铺成浅灰。静止色用深灰的 RGB、透明度为 0。
                    Background = BoardTheme.ButtonClearBrushFor(false),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 7, 10, 7),
                    CornerRadius = new CornerRadius(4),
                    MinHeight = 36
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
                button.Click += (_, _) => Choose(command);
                panel.Children.Add(button);
            }
            void Rule() => panel.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 255, 255, 255))
            });
            StackPanel header = new() { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(10, 8, 10, 8) };
            header.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/Logo/logo.png")),
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = "WorkTiles", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 244, 244))
            });
            panel.Children.Add(header);
            Rule();
            Item("Window", "打开主界面", TrayIcon.CommandOpen);
            Item(tilesVisible ? "EyeOff" : "Eye", tilesVisible ? "隐藏桌面磁贴" : "显示桌面磁贴",
                tilesVisible ? TrayIcon.CommandHideTiles : TrayIcon.CommandShowTiles);
            Rule();
            Item("Settings", "应用设置", TrayIcon.CommandSettings);
            Rule();
            Item("ArrowSync", "重启", TrayIcon.CommandRestart);
            Item("Dismiss", "退出", TrayIcon.CommandExit);
            shell.Child = panel;
            root.Children.Add(shell);
            _shell = shell;

            double scale = ScaleAt(_anchorX, _anchorY);
            int width = (int)Math.Ceiling(shell.MinWidth * scale);
            int height = (int)Math.Ceiling(280 * scale);
            Place(width, height, out int x, out int y);
            shell.Loaded += (_, _) =>
            {
                UpdateClip();
                Fit();
                HoverTransition.ApplyTo(root);
            };
            shell.SizeChanged += (_, _) =>
            {
                UpdateClip();
                Fit();
            };
            _outside.Tick += (_, _) => WatchOutsideClick();
            _host.Create(root, x, y, width, height);
            // 等宿主把透明底板接上再显示，避免第一帧还是系统白底。
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
            {
                if (_closed || _shown || _host.Handle == 0) return;
                UpdateClip();
                Fit();
                HoverTransition.ApplyTo(root);
                _shown = true;
                _host.Show();
                _outside.Start();
            });
        }

        private void Choose(int command)
        {
            if (_chose) return;
            _chose = true;
            _outside.Stop();
            _chosen(command);
            Close();
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _chose = true;
            _outside.Stop();
            _host.Dispose();
            if (_current == this) _current = null;
        }

        private void Fit()
        {
            if (_closed || _fitting || _fitAttempts >= 4 || _host.Handle == 0) return;
            _fitting = true;
            try
            {
                _shell.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double desiredW = Math.Max(_shell.MinWidth, _shell.DesiredSize.Width);
                double desiredH = _shell.DesiredSize.Height;
                if (desiredH < 1) return;
                double scale = _shell.XamlRoot is { RasterizationScale: > 0 } root
                    ? root.RasterizationScale
                    : ScaleAt(_anchorX, _anchorY);
                int width = Math.Max(1, (int)Math.Ceiling(desiredW * scale));
                int height = Math.Max(1, (int)Math.Ceiling(desiredH * scale));
                if (_placed && Math.Abs(_host.Width - width) <= 1 && Math.Abs(_host.Height - height) <= 1)
                    return;
                _fitAttempts++;
                Place(width, height, out int x, out int y);
                _host.MoveAndResize(x, y, width, height);
                _placed = true;
                if (_shell.ActualHeight < 1 || desiredH > _shell.ActualHeight + 1)
                    _shell.DispatcherQueue.TryEnqueue(Fit);
            }
            finally { _fitting = false; }
        }

        private void Place(int width, int height, out int x, out int y)
        {
            DisplayArea area = DisplayArea.GetFromPoint(new PointInt32(_anchorX, _anchorY), DisplayAreaFallback.Nearest);
            x = Math.Clamp(_anchorX, area.WorkArea.X, Math.Max(area.WorkArea.X, area.WorkArea.X + area.WorkArea.Width - width));
            y = Math.Clamp(_anchorY - height, area.WorkArea.Y, Math.Max(area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - height));
        }

        private void UpdateClip()
        {
            if (_shell.ActualWidth < 1 || _shell.ActualHeight < 1) return;
            Visual visual = ElementCompositionPreview.GetElementVisual(_shell);
            if (_clip is null)
            {
                Compositor compositor = visual.Compositor;
                _clip = compositor.CreateRoundedRectangleGeometry();
                _clip.CornerRadius = new Vector2(Corner, Corner);
                visual.Clip = compositor.CreateGeometricClip(_clip);
            }
            _clip.Size = new Vector2((float)_shell.ActualWidth, (float)_shell.ActualHeight);
        }

        private void WatchOutsideClick()
        {
            if (_closed || _host.Handle == 0) return;
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            bool escape = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (escape || (down && !_pointerWasDown && !PointerInside()))
                Close();
            _pointerWasDown = down;
        }

        private bool PointerInside()
        {
            if (!GetCursorPos(out Point cursor) || !GetWindowRect(_host.Handle, out Rect rect)) return true;
            return cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        }

        private static double ScaleAt(int x, int y)
        {
            nint monitor = MonitorFromPoint(new Point { X = x, Y = y }, 2);
            if (monitor != 0 && GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi >= 96)
                return dpi / 96d;
            return 1;
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
}
