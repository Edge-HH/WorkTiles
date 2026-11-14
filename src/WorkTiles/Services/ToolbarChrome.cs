namespace WorkTiles.Services;

/// <summary>
/// 控制窗圆角。滑块 0 到 20：0 是直角，20 是短边一半的胶囊。
/// 同一档位按短边比例换算，控制窗变大变小后圆角看起来保持同一形状。
/// </summary>
public static class ToolbarChrome
{
    public const double MaxRoundness = 20;

    public static double NormalizeRoundness(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, MaxRoundness) : 0;

    public static double CornerRadius(double roundness, double width, double height)
    {
        double level = NormalizeRoundness(roundness);
        double shortSide = Math.Min(Positive(width), Positive(height));
        return shortSide / 2 * (level / MaxRoundness);
    }

    private static double Positive(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
