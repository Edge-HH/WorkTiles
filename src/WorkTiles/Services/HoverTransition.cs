using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WorkTiles.Services;

/// <summary>
/// 去掉控件模板里的画刷过渡。
/// WinUI 的 BrushTransition 不插值透明度：静止色若是 #0FFFFFFF 或透明白，
/// 悬停第一帧会按白色铺满，然后才落到主题里的深灰。
/// 把普通按钮的静止色改成悬停灰会抹掉它原本的淡填充，所以这里直接拿掉过渡，颜色立刻切过去。
/// </summary>
public static class HoverTransition
{
    public static readonly DependencyProperty SuppressProperty =
        DependencyProperty.RegisterAttached("Suppress", typeof(bool), typeof(HoverTransition),
            new PropertyMetadata(false, OnSuppressChanged));

    public static void SetSuppress(DependencyObject element, bool value) =>
        element.SetValue(SuppressProperty, value);

    public static bool GetSuppress(DependencyObject element) =>
        (bool)element.GetValue(SuppressProperty);

    private static void OnSuppressChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element || args.NewValue is not true) return;
        element.Loaded += (_, _) => StripWhenReady(element);
        element.ActualThemeChanged += (_, _) =>
            element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => StripWhenReady(element));
        if (element.IsLoaded) StripWhenReady(element);
    }

    /// <summary>给已经在树上、但用了自带样式的按钮补一次。数字框的微调按钮就是这种。</summary>
    public static void ApplyTo(DependencyObject root)
    {
        if (root is Button button)
        {
            StripWhenReady(button);
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
            ApplyTo(VisualTreeHelper.GetChild(root, index));
    }

    private static readonly HashSet<FrameworkElement> Waiting = [];

    private static void StripWhenReady(FrameworkElement element)
    {
        if (TryStrip(element) || !Waiting.Add(element)) return;
        // 下拉和托盘里的按钮经常在模板展开前就 Loaded。只排一次队会扑空，过渡还留着。
        void OnLayout(object? sender, object args)
        {
            if (!TryStrip(element)) return;
            element.LayoutUpdated -= OnLayout;
            Waiting.Remove(element);
        }
        element.LayoutUpdated += OnLayout;
        element.Unloaded += (_, _) =>
        {
            element.LayoutUpdated -= OnLayout;
            Waiting.Remove(element);
        };
    }

    private static bool TryStrip(FrameworkElement element)
    {
        if (element is Control control) control.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(element) == 0) return false;
        Strip(element);
        return true;
    }

    private static void Strip(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            switch (child)
            {
                case ContentPresenter presenter:
                    presenter.BackgroundTransition = null;
                    break;
                case Panel panel:
                    panel.BackgroundTransition = null;
                    break;
                case Border border:
                    border.BackgroundTransition = null;
                    break;
            }
            Strip(child);
        }
    }
}
