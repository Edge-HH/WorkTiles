using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WorkTiles.Services;

namespace WorkTiles.Controls;

/// <summary>图片入口共用一行图片历史；磁贴与背景板的最近媒体按使用面分开显示，互不串扰。</summary>
public sealed class RecentImagesView : StackPanel
{
    private readonly MediaLibrary _library;
    private readonly Func<string, Task> _select;
    private readonly MediaScope _scope;
    private readonly bool _imagesOnly;
    private readonly StackPanel _images = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    public RecentImagesView(MediaLibrary library, Func<string, Task> select, MediaScope scope = MediaScope.Images, bool imagesOnly = false)
    {
        _library = library;
        _select = select;
        _scope = scope;
        _imagesOnly = imagesOnly;
        Spacing = 8;
        Children.Add(new TextBlock { Text = scope == MediaScope.Images || imagesOnly ? "最近使用的图像" : "最近媒体" });
        Children.Add(new ScrollViewer { Content = _images, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled });
        Loaded += (_, _) => { _library.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => _library.Changed -= Refresh;
    }
    private void Refresh()
    {
        _images.Children.Clear();
        foreach (string path in _library.RecentMedia(_scope).Where(path => !_imagesOnly || MediaLibrary.IsImage(path)))
        {
            string title = _library.DisplayTitle(path);
            string? preview = MediaLibrary.PreviewFile(path);
            bool image = MediaLibrary.IsImage(path) || preview is not null;
            object content = image
                ? new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Source = new BitmapImage(new Uri(preview ?? path)) { DecodePixelWidth = 176, AutoPlay = false }
                }
                : new TextBlock
                {
                    Text = title,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 3,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(6),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            Button button = new() { Width = 88, Height = 88, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
                Content = content };
            AutomationProperties.SetName(button, "使用最近媒体 " + title);
            ToolTipService.SetToolTip(button, title);
            button.Click += async (_, _) => await _select(path);
            _images.Children.Add(button);
        }
        if (_images.Children.Count == 0) _images.Children.Add(new TextBlock
        {
            Text = _scope == MediaScope.Images || _imagesOnly ? "选择图片后会显示在这里" : "导入图片、视频或动态壁纸后会显示在这里",
            Opacity = .6
        });
    }
}
