using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace WorkTiles.Services;

/// <summary>把界面品牌色和 WinUI 强调色改成当前 Windows 主题色。学科预设色板不在这里。</summary>
public static class SystemAccent
{
    private static readonly UISettings Settings = new();
    private static readonly AccessibilitySettings Accessibility = new();
    private static DispatcherQueue? _queue;
    private static bool? _light;
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        _queue = DispatcherQueue.GetForCurrentThread();
        Settings.ColorValuesChanged += (_, _) => _queue?.TryEnqueue(ApplyCurrent);
    }

    public static void Apply() => ApplyCurrent();

    public static void Apply(bool light)
    {
        _light = light;
        ApplyCurrent();
    }

    private static void ApplyCurrent()
    {
        try
        {
            if (Application.Current is null) return;
            bool light = _light ?? SystemIsLight();
            Color accent = Settings.GetColorValue(UIColorType.Accent);
            Color light1 = Settings.GetColorValue(UIColorType.AccentLight1);
            Color light2 = Settings.GetColorValue(UIColorType.AccentLight2);
            Color light3 = Settings.GetColorValue(UIColorType.AccentLight3);
            Color dark1 = Settings.GetColorValue(UIColorType.AccentDark1);
            Color dark2 = Settings.GetColorValue(UIColorType.AccentDark2);
            Color dark3 = Settings.GetColorValue(UIColorType.AccentDark3);
            // 高对比度继续用系统指定的颜色，只把写死的品牌紫换成主题色。
            if (!Accessibility.HighContrast)
            {
                PaintTree(Application.Current.Resources, accent, light1, light2, light3, dark1, dark2, dark3);
                Color fill = light ? dark1 : light2;
                PaintBrush(Application.Current.Resources, "AccentFillColorDefaultBrush", fill);
                PaintBrush(Application.Current.Resources, "AccentFillColorSecondaryBrush", fill);
                PaintBrush(Application.Current.Resources, "AccentFillColorTertiaryBrush", fill);
                PaintBrush(Application.Current.Resources, "SystemFillColorAttentionBrush", fill);
                PaintBrush(Application.Current.Resources, "AccentFillColorSelectedTextBackgroundBrush", accent);
            }
            Color brand = light ? Settings.GetColorValue(UIColorType.AccentDark1) : Settings.GetColorValue(UIColorType.AccentLight2);
            PaintBrush(Application.Current.Resources, "BoardBrandBrush", brand);
            PaintBrush(Application.Current.Resources, "BoardBrandSoftBrush", Soften(brand, light));
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x8000FFFF))
        {
            // 应用还在启动，或窗口正在关闭，资源字典暂时拿不到。下一次主题变化会再涂一次。
        }
    }

    private static void PaintTree(ResourceDictionary dictionary, Color accent, Color light1, Color light2, Color light3, Color dark1, Color dark2, Color dark3)
    {
        HashSet<ResourceDictionary> seen = new(ReferenceEqualityComparer.Instance);
        PaintTree(dictionary, accent, light1, light2, light3, dark1, dark2, dark3, seen);
    }

    private static void PaintTree(ResourceDictionary dictionary, Color accent, Color light1, Color light2, Color light3, Color dark1, Color dark2, Color dark3, HashSet<ResourceDictionary> seen)
    {
        if (!seen.Add(dictionary)) return;
        PaintTheme(dictionary, "Dark", accent, light1, light2, light3, dark1, dark2, dark3, light: false);
        PaintTheme(dictionary, "Default", accent, light1, light2, light3, dark1, dark2, dark3, light: false);
        PaintTheme(dictionary, "Light", accent, light1, light2, light3, dark1, dark2, dark3, light: true);
        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
            PaintTree(merged, accent, light1, light2, light3, dark1, dark2, dark3, seen);
    }

    private static void PaintTheme(ResourceDictionary dictionary, string name, Color accent, Color light1, Color light2, Color light3, Color dark1, Color dark2, Color dark3, bool light)
    {
        if (!dictionary.ThemeDictionaries.TryGetValue(name, out object? value) || value is not ResourceDictionary theme) return;
        Color fill = light ? dark1 : light2;
        PaintBrush(theme, "AccentFillColorDefaultBrush", fill);
        PaintBrush(theme, "AccentFillColorSecondaryBrush", fill);
        PaintBrush(theme, "AccentFillColorTertiaryBrush", fill);
        PaintBrush(theme, "SystemFillColorAttentionBrush", fill);
        PaintBrush(theme, "AccentFillColorSelectedTextBackgroundBrush", accent);
        PaintBrush(theme, "AccentTextFillColorPrimaryBrush", light ? dark2 : light3);
        PaintBrush(theme, "AccentTextFillColorSecondaryBrush", light ? dark3 : light3);
        PaintBrush(theme, "AccentTextFillColorTertiaryBrush", light ? dark1 : light2);
        PaintBrush(theme, "AccentAAFillColorDefaultBrush", fill);
        PaintBrush(theme, "AccentAAFillColorSecondaryBrush", light ? dark2 : light3);
        PaintBrush(theme, "AccentAAFillColorTertiaryBrush", light ? dark3 : light1);
        PaintBrush(theme, "ProgressBarForegroundThemeBrush", fill);
        PaintBrush(theme, "ProgressBarIndeterminateForegroundThemeBrush", fill);
        PaintBrush(theme, "SliderTrackDecreaseBackgroundThemeBrush", fill);
        PaintBrush(theme, "BoardBrandBrush", fill);
        PaintBrush(theme, "BoardBrandSoftBrush", Soften(fill, light));
        PaintAcrylic(theme, "AccentAcrylicBackgroundFillColorDefaultBrush", light ? light3 : dark1);
        PaintAcrylic(theme, "AccentAcrylicInAppFillColorDefaultBrush", light ? light3 : dark1);
        PaintAcrylic(theme, "AccentAcrylicBackgroundFillColorBaseBrush", light ? light2 : dark2);
        PaintAcrylic(theme, "AccentAcrylicInAppFillColorBaseBrush", light ? light2 : dark2);
        PaintFocusBorder(theme, fill);
    }

    private static void PaintBrush(ResourceDictionary dictionary, string key, Color color)
    {
        if (dictionary.TryGetValue(key, out object? value) && value is SolidColorBrush brush)
            brush.Color = color;
        else
            dictionary[key] = new SolidColorBrush(color);
    }

    private static void PaintAcrylic(ResourceDictionary dictionary, string key, Color color)
    {
        if (dictionary.TryGetValue(key, out object? value) && value is AcrylicBrush brush)
        {
            brush.TintColor = color;
            brush.FallbackColor = color;
        }
    }

    private static void PaintFocusBorder(ResourceDictionary dictionary, Color accent)
    {
        if (dictionary.TryGetValue("TextControlElevationBorderFocusedBrush", out object? value) && value is LinearGradientBrush gradient
            && dictionary.TryGetValue("ControlStrokeColorDefault", out object? strokeValue) && strokeValue is Color stroke)
        {
            foreach (GradientStop stop in gradient.GradientStops)
                if (stop.Color != stroke) stop.Color = accent;
        }
    }

    private static Color Soften(Color accent, bool light)
    {
        Color surface = light ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 22, 22, 30);
        const float amount = 0.22f;
        return Color.FromArgb(255,
            Mix(surface.R, accent.R, amount),
            Mix(surface.G, accent.G, amount),
            Mix(surface.B, accent.B, amount));
    }

    private static byte Mix(byte from, byte to, float amount) => (byte)(from + (to - from) * amount);

    private static bool SystemIsLight()
    {
        Color background = Settings.GetColorValue(UIColorType.Background);
        return background.R + background.G + background.B > 384;
    }
}
