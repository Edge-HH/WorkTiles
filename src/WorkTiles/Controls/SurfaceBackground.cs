using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WorkTiles.Services;

namespace WorkTiles.Controls;

/// <summary>控制窗、磁贴和预览共用颜色合成规则，不降低前景或模糊的透明度。</summary>
internal static class SurfaceBackground
{
    internal static Brush Create(string color, double opacity, bool cleared, bool glass, double blur) =>
        Apply(null, color, opacity, cleared, glass, blur);

    /// <summary>同一支画笔只更新颜色和模糊。滑块连续变化时不再新建毛玻璃，避免预览闪黑。</summary>
    internal static Brush Apply(Brush? current, string color, double opacity, bool cleared, bool glass, double blur)
    {
        Windows.UI.Color tint = Tint(color, opacity, cleared);
        if (glass)
        {
            if (current is BlurBackdropBrush backdrop)
            {
                backdrop.Update(blur, tint);
                return backdrop;
            }
            return new BlurBackdropBrush(blur, tint) { FallbackColor = tint };
        }
        if (current is SolidColorBrush solid)
        {
            solid.Color = tint;
            return solid;
        }
        return new SolidColorBrush(tint);
    }

    private static Windows.UI.Color Tint(string color, double opacity, bool cleared)
    {
        Windows.UI.Color tint = GridAppearance.ParseColor(color, BoardTheme.SurfaceBrush.Color);
        double alpha = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 0.8;
        tint.A = cleared ? (byte)0 : (byte)Math.Round(tint.A * alpha);
        return tint;
    }
}
