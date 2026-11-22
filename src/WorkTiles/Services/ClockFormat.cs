using System.Globalization;

namespace WorkTiles.Services;

/// <summary>时钟数字的两种秒数形态。大数字把秒写进主时间，小数字留在右上角。</summary>
public static class ClockFormat
{
    public const string Corner = "Corner";
    public const string Large = "Large";

    public static string NormalizeStyle(string? style) =>
        string.Equals(style, Large, StringComparison.OrdinalIgnoreCase) ? Large : Corner;

    public static bool ShowCornerSeconds(bool showSeconds, string? style) =>
        showSeconds && NormalizeStyle(style) != Large;

    public static string MainText(DateTime time, bool showSeconds, string? style) =>
        showSeconds && NormalizeStyle(style) == Large
            ? time.ToString("HH':'mm':'ss", CultureInfo.InvariantCulture)
            : time.ToString("HH':'mm", CultureInfo.InvariantCulture);
}
