using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;
using WorkTiles.Services;

namespace WorkTiles.Controls;

/// <summary>把视频帧绘制进 XAML 图像层，供前方控件的 CompositionBackdropBrush 采样。</summary>
internal sealed class VideoFrameImage : Grid, IDisposable
{
    private readonly Image _image = new();
    private readonly MediaPlayer _player;
    private CanvasRenderTarget? _frame;
    private CanvasImageSource? _source;
    private int _pending;
    private bool _disposed;
    internal event Action? Failed;
    internal int PresentedFrames { get; private set; }
    // UI 线程帧拷贝累计耗时（Stopwatch 滴答），供性能回归测试差分每帧开销。
    private long _workTicks;
    internal long FrameWorkTicks => Interlocked.Read(ref _workTicks);
    internal int SurfaceWidth => (int)(_frame?.SizeInPixels.Width ?? 0);
    internal int SurfaceHeight => (int)(_frame?.SizeInPixels.Height ?? 0);
    internal Microsoft.UI.Xaml.Media.Stretch Stretch { get; set; } = Microsoft.UI.Xaml.Media.Stretch.UniformToFill;

    internal VideoFrameImage(MediaPlayer player)
    {
        _player = player;
        IsHitTestVisible = false;
        Children.Add(_image);
        _image.Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill;
        _image.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        _image.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
        player.IsVideoFrameServerEnabled = true;
        player.VideoFrameAvailable += FrameAvailable;
    }

    private void FrameAvailable(MediaPlayer sender, object args)
    {
        // 解码回调来自后台线程；最多保留一个 UI 更新，避免繁忙时积压过期帧。
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            var work = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_disposed) return;
                // 隐藏管线的帧直接丢弃：折叠祖先不触发布局/视口事件，播放暂停前到来的帧不该再占用 UI 线程。
                if (!BackgroundVisual.ShownThroughTree(this)) return;
                int nativeWidth = (int)sender.PlaybackSession.NaturalVideoWidth;
                int nativeHeight = (int)sender.PlaybackSession.NaturalVideoHeight;
                if (nativeWidth <= 0 || nativeHeight <= 0) return;
                // 显示面按控件像素来，画面先按原比例拷下来再自己摆放。
                // 系统的 CopyFrameToVideoSurface 会把画面“适应”进目标并填黑，缩放模式就会带上视频里本来没有的黑边，
                // 预览和真实窗口的目标比例不同时，黑边位置也不一样。
                double rasterScale = XamlRoot?.RasterizationScale ?? 1;
                double boxWidth = ActualSize.X * rasterScale, boxHeight = ActualSize.Y * rasterScale;
                if (boxWidth < 8 || boxHeight < 8) return;
                int boxW = Even(boxWidth);
                int boxH = Even(boxHeight);
                string mode = Stretch switch
                {
                    Microsoft.UI.Xaml.Media.Stretch.Uniform => "Fit",
                    Microsoft.UI.Xaml.Media.Stretch.Fill => "Stretch",
                    _ => "Zoom"
                };
                VideoPlacement.Rect place = VideoPlacement.Place(nativeWidth, nativeHeight, boxW, boxH, mode);
                double copyScale = Math.Min(1, Math.Max(Math.Abs(place.Width) / nativeWidth, Math.Abs(place.Height) / nativeHeight));
                double copyPixels = nativeWidth * copyScale * nativeHeight * copyScale;
                double pixelLimit = boxW * (double)boxH * 4;
                if (copyPixels > pixelLimit && copyPixels > 0) copyScale *= Math.Sqrt(pixelLimit / copyPixels);
                int width = Even(nativeWidth * copyScale);
                int height = Even(nativeHeight * copyScale);
                if (_frame is null || _frame.SizeInPixels.Width != width || _frame.SizeInPixels.Height != height
                    || _source is null || _source.SizeInPixels.Width != boxW || _source.SizeInPixels.Height != boxH)
                {
                    ReleaseSurfaces();
                    var device = CanvasDevice.GetSharedDevice();
                    _frame = new CanvasRenderTarget(device, width, height, 96);
                    _source = new CanvasImageSource(device, boxW, boxH, 96);
                    _image.Source = _source;
                }
                sender.CopyFrameToVideoSurface(_frame);
                using var drawing = _source!.CreateDrawingSession(Microsoft.UI.Colors.Transparent);
                drawing.DrawImage(_frame, new Windows.Foundation.Rect(place.X, place.Y, place.Width, place.Height));
                PresentedFrames++;
            }
            catch (Exception)
            {
                // 与普通解码失败走同一跳过/报错路径，不让异步绘制异常终止应用。
                if (!_disposed) Failed?.Invoke();
            }
            finally { Interlocked.Add(ref _workTicks, work.ElapsedTicks); Interlocked.Exchange(ref _pending, 0); }
        })) Interlocked.Exchange(ref _pending, 0);
    }

    private void ReleaseSurfaces()
    {
        _image.Source = null;
        _source = null;
        _frame?.Dispose();
        _frame = null;
    }

    private static int Even(double value)
    {
        int size = Math.Max(2, (int)Math.Round(value));
        return (size & 1) == 0 ? size : size + 1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.VideoFrameAvailable -= FrameAvailable;
        ReleaseSurfaces();
    }
}
