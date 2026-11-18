using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WorkTiles.Services;
using WorkTiles.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace WorkTiles.Controls;

/// <summary>背景媒体、模糊与前景分层；重复应用外观不会重新开始当前视频或计时。</summary>
public sealed class BackgroundVisual : Grid
{
    private readonly Grid _media = new();
    private readonly Grid _overlays = new();
    // 效果层常驻在视觉树中；重复应用设置时只替换 Brush，避免清空/重挂载造成闪烁。
    private readonly Border _surfaceOverlay = new() { IsHitTestVisible = false, Name = "SurfaceTintOverlay" };
    private readonly Border _blurOverlay = new() { IsHitTestVisible = false };
    private readonly Border _glassTintOverlay = new() { IsHitTestVisible = false };
    private readonly BackgroundPlaylist _playlist = new();
    private readonly DispatcherTimer _timer = new();
    // 折叠的祖先不再触发布局/视口事件，媒体启停靠低频兜底定时器重新评估，不依赖事件必然到达。
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private BackgroundSettings _style = new();
    private string _path = "";
    private MediaPlayer? _player;
    private VideoFrameImage? _videoFrames;
    private Image? _image;
    private WebWallpaperView? _web;
    private bool _shared;
    private bool _failed;
    private bool _inViewport = true;
    private bool _active;
    private bool _viewportKnown;
    private bool _overlaySurface;
    private string _overlayKey = "";
    private string _backgroundKey = "";
    private int _mediaRestoreEpoch;
    private int _imageLoadToken;
    private string _snapshotPath = "";
    private bool _snapshotRunning;
    /// <summary>别的表面开了毛玻璃时，网页不能再占着合成层，否则那些毛玻璃会采到一块黑。</summary>
    internal static bool BlockLiveWeb { get; set; }

    // 局部外观预览可要求铺满；不改变项目保存的图片显示模式。
    internal Stretch? MediaStretchOverride { get; init; }

    public BackgroundVisual()
    {
        IsHitTestVisible = false;
        Children.Add(_media);
        Children.Add(_overlays);
        _overlays.Children.Add(_blurOverlay);
        _overlays.Children.Add(_glassTintOverlay);
        _overlays.Children.Add(_surfaceOverlay);
        _timer.Tick += (_, _) => Advance();
        _activityTimer.Tick += (_, _) => EvaluateActivity();
        Loaded += (_, _) =>
        {
            // 刚挂载时 EffectiveViewport 可能仍是空矩形；先按可见恢复媒体，错误暂停由后续视口事件纠正。
            _inViewport = true;
            QueueMediaRestore();
            EvaluateActivity();
            _activityTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _activityTimer.Stop();
            _timer.Stop();
            ReleaseMedia();
            _active = false;
            _viewportKnown = false;
            _inViewport = true;
            QueueMediaRestore();
        };
        SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width <= 1 || args.NewSize.Height <= 1) return;
            if (_media.Children.Count == 0 && !string.IsNullOrEmpty(_playlist.Current))
                _path = "";
            if (IsLoaded) ShowCurrent();
        };
        EffectiveViewportChanged += (_, args) =>
        {
            // 刚挂上时有效视口经常是空的，不能据此把图片停掉，否则磁贴背景会一直不出现。
            bool visible = args.EffectiveViewport.Width > 1 && args.EffectiveViewport.Height > 1;
            if (!visible && !_viewportKnown) return;
            if (visible) _viewportKnown = true;
            if (_inViewport == visible) return;
            _inViewport = visible;
            EvaluateActivity();
        };
    }

    /// <summary>可见性综合判断后的唯一启停入口；隐藏的管线必须停下解码、定时器与帧拷贝。</summary>
    private void EvaluateActivity()
    {
        bool active = IsLoaded && _inViewport && ShownThroughTree(this);
        if (active == _active) return;
        _active = active;
        if (_active) { ShowCurrent(); _player?.Play(); _web?.SetPaused(false); UpdateTimer(); }
        else { _player?.Pause(); _web?.SetPaused(true); _timer.Stop(); }
    }

    /// <summary>WinUI 3 没有有效可见性事件；沿父链检查 Visibility，祖先折叠时子树不参与布局。</summary>
    internal static bool ShownThroughTree(UIElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private void QueueMediaRestore()
    {
        // 搬动预览时 Loaded 和 Unloaded 会交错。只认最后一次，避免先排队的卸载把后来的加载吃掉。
        int epoch = ++_mediaRestoreEpoch;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (epoch != _mediaRestoreEpoch || !IsLoaded) return;
            ShowCurrent();
            UpdateTimer();
        });
    }

    public void Apply(BackgroundSettings style, Brush fallback, bool shared = false, bool surface = false, double blurScale = 1)
    {
        bool wasShared = _shared;
        _style = style;
        _shared = shared;
        blurScale = double.IsFinite(blurScale) ? Math.Max(0, blurScale) : 1;
        double effectiveBlur = style.Blur * blurScale;
        // SurfaceBrush 每次都是新实例，不能用哈希参与缓存键，否则每次 Apply 都会重置底色。
        string backgroundKey = $"{shared}|{surface}|{style.Glass}|{style.Color}|{style.ColorOpacity:0.####}|{style.ColorCleared}|{style.Blur:0.####}|{BoardTheme.IsLight}";
        if (_backgroundKey != backgroundKey)
        {
            _backgroundKey = backgroundKey;
            Background = shared || (style.Glass && string.IsNullOrWhiteSpace(style.Color)) ? null : string.IsNullOrWhiteSpace(style.Color) ? fallback : SafeColor(style.Color, fallback);
            if (surface) Background = style.Glass ? null : SurfaceBackground.Create(
                style.Color, style.ColorOpacity, style.ColorCleared, false, style.Blur);
        }
        bool changed = _playlist.Configure(style);
        if (changed) { _failed = false; _timer.Stop(); }
        bool hasMedia = !shared && !string.IsNullOrEmpty(_playlist.Current);
        // 共享区域本身不承载媒体；只有从独立媒体切换到共享层时才需要释放旧播放器。
        if (shared && !wasShared) ReleaseMedia();
        else if (IsLoaded && !_failed) ShowCurrent();
        bool liveWeb = WebIsLive;
        string overlayKey = $"{surface}|{style.Glass}|{liveWeb}|{hasMedia}|{style.Color}|{style.ColorOpacity:0.####}|{style.ColorCleared}|{effectiveBlur:0.####}|{BoardTheme.IsLight}";
        if (_overlayKey != overlayKey)
        {
            _overlayKey = overlayKey;
            _overlaySurface = surface;
            if (surface && style.Glass)
                _surfaceOverlay.Background = SurfaceBackground.Apply(_surfaceOverlay.Background, style.Color, style.ColorOpacity, style.ColorCleared, true, style.Blur);
            else if (surface && hasMedia && !style.ColorCleared)
                _surfaceOverlay.Background = SurfaceBackground.Apply(_surfaceOverlay.Background, style.Color, style.ColorOpacity, false, false, 0);
            else _surfaceOverlay.Background = null;
            // 网页还挂在树上时，背景模糊采到的是黑，这时不铺模糊。网页拿掉之后模糊的是预览图。
            if (!surface && style.Glass && !liveWeb)
            {
                if (_blurOverlay.Background is BlurBackdropBrush blur) blur.Update(effectiveBlur);
                else _blurOverlay.Background = new BlurBackdropBrush(effectiveBlur);
            }
            else _blurOverlay.Background = null;
            if (!surface && style.Glass)
            {
                Windows.UI.Color tint = BoardTheme.IsLight
                    ? Windows.UI.Color.FromArgb(36, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(36, 0, 0, 0);
                if (_glassTintOverlay.Background is SolidColorBrush solid) solid.Color = tint;
                else _glassTintOverlay.Background = new SolidColorBrush(tint);
            }
            else _glassTintOverlay.Background = null;
        }
        _surfaceOverlay.Visibility = _overlaySurface && (style.Glass || (hasMedia && !style.ColorCleared)) ? Visibility.Visible : Visibility.Collapsed;
        _blurOverlay.Visibility = !_overlaySurface && style.Glass && !liveWeb ? Visibility.Visible : Visibility.Collapsed;
        _glassTintOverlay.Visibility = !_overlaySurface && style.Glass ? Visibility.Visible : Visibility.Collapsed;
        if (_web is not null)
            _web.Visibility = Visibility.Visible;
        UpdateTimer();
    }

    private void ShowCurrent()
    {
        if (_shared || !IsLoaded || _failed) return;
        string path = _playlist.Current;
        if (_path != path)
        {
            ReleaseMedia();
            if (string.IsNullOrEmpty(path)) _path = path;
            else if (File.Exists(path))
            {
                _path = path;
                try
                {
                    if (MediaLibrary.IsWeb(path))
                    {
                        string? preview = MediaLibrary.PreviewFile(path) ?? WebPreview(path);
                        if (preview is not null)
                        {
                            _image = new Image
                            {
                                Stretch = Stretch.UniformToFill,
                                Source = new BitmapImage(new Uri(preview)) { DecodePixelWidth = 1280, AutoPlay = false }
                            };
                            _media.Children.Add(_image);
                        }
                        bool allowLive = !_style.Glass && !BlockLiveWeb;
                        if (allowLive || preview is null)
                        {
                            WebWallpaperView web = new(path);
                            _web = web;
                            web.Failed += () => { if (ReferenceEquals(_web, web)) Failed(); };
                            _media.Children.Add(web);
                            if (!allowLive && preview is null && !_snapshotRunning && _snapshotPath != path)
                                _ = SnapshotWebAsync(web, path);
                        }
                    }
                    else if (MediaLibrary.IsVideo(path))
                    {
                        MediaPlayer player = new() { IsMuted = true, AutoPlay = true };
                        _player = player;
                        player.CommandManager.IsEnabled = false;
                        // 播放器自己的画面会按适应方式填黑边，而且合成层盖在帧图像上面、不听 Opacity。
                        // 帧服务不需要把 MediaPlayerElement 放进界面。
                        _videoFrames = new VideoFrameImage(player);
                        _videoFrames.Failed += () => { if (ReferenceEquals(_player, player)) Failed(); };
                        _media.Children.Add(_videoFrames);
                        // 回调不在 UI 线程，并可能晚于换曲或卸载，必须核对播放器身份。
                        player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
                        {
                            if (ReferenceEquals(_player, player) && _style.PlaylistEnabled && _style.SwitchOnMediaEnded) Advance();
                        });
                        player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
                        {
                            if (ReferenceEquals(_player, player)) Failed();
                        });
                        player.Source = MediaSource.CreateFromUri(new Uri(path));
                    }
                    else
                    {
                        _image = new Image();
                        Image current = _image;
                        current.ImageFailed += (_, _) => { if (ReferenceEquals(_image, current)) Failed(); };
                        _media.Children.Add(current);
                        ShowImage(current, path);
                    }
                }
                catch (Exception) { Failed(); }
            }
        }
        Stretch stretch = MediaStretchOverride ?? (_style.ImageMode switch { "Stretch" => Stretch.Fill, "Fit" => Stretch.Uniform, _ => Stretch.UniformToFill });
        if (_image is not null) _image.Stretch = stretch;
        if (_videoFrames is not null) _videoFrames.Stretch = stretch;
        if (MediaLibrary.IsWeb(_path) && _web is null && !_style.Glass && !BlockLiveWeb && File.Exists(_path))
        {
            WebWallpaperView web = new(_path);
            _web = web;
            web.Failed += () => { if (ReferenceEquals(_web, web)) Failed(); };
            _media.Children.Add(web);
        }
        if (_web is not null)
        {
            bool showWeb = (!_style.Glass && !BlockLiveWeb) || _image is null;
            bool inTree = _media.Children.Contains(_web);
            if (showWeb && !inTree) _media.Children.Add(_web);
            else if (!showWeb && inTree) _media.Children.Remove(_web);
            if (!showWeb && _image is null && !_snapshotRunning && _snapshotPath != _path)
                _ = SnapshotWebAsync(_web, _path);
        }
        if (_player is not null)
        {
            _player.IsLoopingEnabled = !_style.PlaylistEnabled || !_style.SwitchOnMediaEnded;
            if (_active) _player.Play();
            else _player.Pause();
        }
    }

    private bool WebIsLive => _web is not null && _media.Children.Contains(_web);

    private async Task SnapshotWebAsync(WebWallpaperView web, string path)
    {
        _snapshotRunning = true;
        try
        {
            for (int attempt = 0; attempt < 25 && web.Core is null; attempt++)
                await Task.Delay(200);
            if (web.Core is null) { _snapshotPath = path; return; }
            await Task.Delay(500);
            string destination = path + ".preview.png";
            if (!await web.TryCapturePreviewAsync(destination)) { _snapshotPath = path; return; }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsLoaded || _playlist.Current != path) return;
                _path = "";
                ShowCurrent();
                _overlayKey = "";
                if (!_overlaySurface && _style.Glass && !WebIsLive)
                {
                    if (_blurOverlay.Background is not BlurBackdropBrush blur)
                        _blurOverlay.Background = new BlurBackdropBrush(_style.Blur);
                    else blur.Update(_style.Blur);
                    _blurOverlay.Visibility = Visibility.Visible;
                    _glassTintOverlay.Visibility = Visibility.Visible;
                }
            });
        }
        finally { _snapshotRunning = false; }
    }

    private static string? WebPreview(string path)
    {
        try
        {
            WebWallpaperPackage package = WebWallpaperPackage.Load(path);
            WallpaperProject? project = WallpaperEngineLibrary.ReadProject(package.Directory);
            if (project is not null && MediaLibrary.IsImage(project.PreviewPath) && File.Exists(project.PreviewPath))
                return project.PreviewPath;
            foreach (string name in new[] { "preview.jpg", "preview.jpeg", "preview.png", "preview.gif" })
            {
                string candidate = Path.Combine(package.Directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private void Advance()
    {
        _timer.Stop();
        if (_playlist.Advance(_style.Shuffle)) { _failed = false; ShowCurrent(); }
        else if (!_failed && _player is not null) { _player.PlaybackSession.Position = TimeSpan.Zero; _player.Play(); }
        UpdateTimer();
    }

    private void Failed()
    {
        _timer.Stop();
        if (_playlist.Advance(_style.Shuffle, failed: true)) { ShowCurrent(); UpdateTimer(); }
        else
        {
            _failed = true;
            ReleaseMedia();
            _media.Children.Add(new TextBlock { Text = "背景媒体无法播放，请重新选择文件或检查视频编码。",
                TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
    }

    private void UpdateTimer()
    {
        if (!IsLoaded || !_active || _shared || _failed || !_style.PlaylistEnabled || !_style.SwitchOnTimer || string.IsNullOrEmpty(_playlist.Current)) { _timer.Stop(); return; }
        TimeSpan interval = TimeSpan.FromSeconds(BackgroundPlaylist.NormalizeInterval(_style.SwitchIntervalSeconds));
        if (_timer.Interval != interval) { _timer.Stop(); _timer.Interval = interval; }
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void ReleaseMedia()
    {
        _imageLoadToken++;
        MediaPlayer? player = _player;
        _player = null;
        _videoFrames?.Dispose();
        _videoFrames = null;
        player?.Dispose();
        _web?.Dispose();
        _web = null;
        _image = null;
        _path = "";
        _media.Children.Clear();
    }

    /// <summary>先用 Uri 立即贴图，再看守解码是否真正完成。切换或搬动预览时旧图会在解码途中被拆下，
    /// 同一路径的 Uri 解码可能被静默取消（不开图也不报错），画面就停在空白上；超时未开图改用独立文件流重解码。</summary>
    private void ShowImage(Image target, string path)
    {
        int token = ++_imageLoadToken;
        bool opened = false;
        target.ImageOpened += (_, _) => { if (ReferenceEquals(_image, target)) opened = true; };
        target.Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = BackgroundDecodeWidth() };
        _ = RecoverAsync();

        async Task RecoverAsync()
        {
            await Task.Delay(1200);
            // 令牌与引用核对：切换、卸载或已经正常开图后，这次兜底都不能再盖回画面。
            if (opened || token != _imageLoadToken || !ReferenceEquals(_image, target)) return;
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                BitmapImage bitmap = new() { DecodePixelWidth = BackgroundDecodeWidth() };
                await bitmap.SetSourceAsync(stream);
                if (opened || token != _imageLoadToken || !ReferenceEquals(_image, target)) return;
                target.Source = bitmap;
            }
            catch
            {
                if (token == _imageLoadToken && ReferenceEquals(_image, target)) Failed();
            }
        }
    }

    // 背景铺满窗口，按窗口宽度留少量余量解码；尚未布局时退回常见宽度，上限 4096 防全景图撑爆内存。
    private int BackgroundDecodeWidth()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        double width = ActualSize.X > 0 ? ActualSize.X : 1920;
        return Math.Clamp((int)Math.Ceiling(width * scale * 1.5), 1280, 4096);
    }

    private static Brush SafeColor(string value, Brush fallback)
    {
        try { return MainViewModel.BrushFromHex(value); }
        catch { return fallback; }
    }
}
