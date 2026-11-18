using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WorkTiles.Services;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage.Streams;

namespace WorkTiles.Controls;

/// <summary>用系统 WebView2 渲染本地网页项目；不向页面提供原生对象或用户浏览器资料。</summary>
public sealed class WebWallpaperView : Grid, IDisposable
{
    private static Task<CoreWebView2Environment>? _environment;
    private readonly WebView2 _view = new() { IsHitTestVisible = false, IsTabStop = false };
    private bool _disposed;
    private bool _paused;
    private bool _ready;
    public event Action? Failed;
    internal CoreWebView2? Core => _view.CoreWebView2;

    public WebWallpaperView(string entry)
    {
        IsHitTestVisible = false;
        Children.Add(_view);
        Loaded += async (_, _) =>
        {
            if (_ready || _disposed) return;
            _ready = true;
            try
            {
                WebWallpaperPackage package = await Task.Run(() => WebWallpaperPackage.Load(entry));
                var environment = await GetEnvironmentAsync();
                if (_disposed) return;
                await _view.EnsureCoreWebView2Async(environment);
                if (_disposed) return;
                var core = _view.CoreWebView2;
                core.IsMuted = true;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.ProcessFailed += (_, _) => { if (!_disposed) Failed?.Invoke(); };
                string host = "w-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package.Directory)))[..16].ToLowerInvariant() + ".worktiles.invalid";
                core.SetVirtualHostNameToFolderMapping(host, package.Directory, CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += (_, args) =>
                {
                    if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != host) args.Cancel = true;
                };
                await core.AddScriptToExecuteOnDocumentCreatedAsync(CompatibilityScript);
                if (_disposed) return;
                core.NavigationCompleted += async (_, args) =>
                {
                    if (_disposed) return;
                    if (!args.IsSuccess) { Failed?.Invoke(); return; }
                    try
                    {
                        await core.ExecuteScriptAsync("window.wallpaperPropertyListener?.applyGeneralProperties?.({fps:60});window.wallpaperPropertyListener?.applyUserProperties?.(" + package.PropertiesJson + ");");
                        await ApplyPausedAsync();
                    }
                    catch (Exception) { if (!_disposed) Failed?.Invoke(); }
                };
                core.Navigate("https://" + host + "/" + string.Join("/", package.EntryPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Select(Uri.EscapeDataString)));
            }
            catch (Exception) { if (!_disposed) Failed?.Invoke(); }
        };
    }

    private static async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        // 共享在途的初始化任务，但失败时清空缓存：Runtime 缺失或目录被占用导致失败后，装好再导入应能重试，不必重启。
        Task<CoreWebView2Environment>? pending = _environment;
        if (pending is null)
        {
            pending = CreateEnvironmentAsync();
            _environment = pending;
        }
        try
        {
            return await pending;
        }
        catch
        {
            if (ReferenceEquals(_environment, pending)) _environment = null;
            throw;
        }
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "data", "webview-cache");
        Directory.CreateDirectory(directory);
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, directory, new CoreWebView2EnvironmentOptions());
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        _ = ApplyPausedAsync();
    }

    private async Task ApplyPausedAsync()
    {
        if (_disposed || Core is not { } core) return;
        try
        {
            // 先发送宿主暂停事件，再冻结隐藏页面；快速切页后须核对最新状态。
            core.Resume();
            await core.ExecuteScriptAsync("window.__worktilesSetPaused?.(" + (_paused ? "true" : "false") + ");");
            if (_disposed) return;
            _view.Visibility = _paused ? Visibility.Collapsed : Visibility.Visible;
            if (_paused) { await core.TrySuspendAsync(); if (!_disposed && !_paused) core.Resume(); }
        }
        catch (Exception) { /* 卸载或导航中的页面可能已释放，无需重启整个壁纸。 */ }
    }

    /// <summary>把当前画面存成静态图。毛玻璃不能采样网页合成层，之后改模糊这张图。</summary>
    public async Task<bool> TryCapturePreviewAsync(string destination)
    {
        if (_disposed || _view.CoreWebView2 is not { } core) return false;
        try
        {
            using InMemoryRandomAccessStream memory = new();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, memory);
            if (memory.Size < 32) return false;
            memory.Seek(0);
            using DataReader reader = new(memory);
            uint length = (uint)Math.Min(memory.Size, int.MaxValue);
            await reader.LoadAsync(length);
            byte[] bytes = new byte[length];
            reader.ReadBytes(bytes);
            string temporary = destination + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, destination, true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Close();
        Children.Clear();
    }

    private const string CompatibilityScript = """
        (() => {
          // 无系统音频/媒体会话授权的兼容占位；不把页面连接到原生宿主对象。
          window.wallpaperRegisterAudioListener = callback => callback(new Array(128).fill(0));
          for (const name of ['wallpaperRegisterMediaPropertiesListener', 'wallpaperRegisterMediaPlaybackListener',
            'wallpaperRegisterMediaTimelineListener', 'wallpaperRegisterMediaThumbnailListener']) window[name] = () => {};
          let playing = [];
          window.__worktilesSetPaused = paused => {
            window.wallpaperPropertyListener?.setPaused?.(paused);
            if (paused) {
              playing = Array.from(document.querySelectorAll('video,audio')).filter(media => !media.paused);
              playing.forEach(media => media.pause());
            } else {
              playing.forEach(media => media.play().catch(() => {})); playing = [];
            }
          };
        })();
        """;
}
