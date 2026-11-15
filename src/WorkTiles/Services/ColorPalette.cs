using System.Text.RegularExpressions;

namespace WorkTiles.Services;

/// <summary>固定的一一对应色板，允许反复切换而不累计颜色误差。</summary>
public static class ColorPalette
{
    private static readonly string[] Vivid = ["#4ADE80", "#818CF8", "#60A5FA", "#FBBF24", "#F472B6", "#2DD4BF", "#F87171", "#65D46E", "#7567FF"];
    private static readonly string[] Macaron = ["#A8D5BA", "#BCBCE3", "#AECBE8", "#E8D5A5", "#E3B6D0", "#A5D5CF", "#E5B3B3", "#B0D5B4", "#BEB5E8"];
    public static bool IsMacaron { get; private set; }
    public static bool IsCustom { get; private set; }
    private static string[] _custom = [];

    /// <summary>当前色系。自定义色系用调用方传入的 9 个颜色，数量不对时按鲜明色系显示。</summary>
    public static void Use(string? palette, IReadOnlyList<string>? custom)
    {
        IsMacaron = string.Equals(palette, "Macaron", StringComparison.OrdinalIgnoreCase);
        if (custom is { Count: 9 } && custom.All(color => color.Length is 7 or 9 && color[0] == '#'))
            _custom = custom.ToArray();
        IsCustom = string.Equals(palette, "Custom", StringComparison.OrdinalIgnoreCase) && _custom.Length == Vivid.Length;
    }

    public static IReadOnlyList<string> ActiveSwatches =>
        IsCustom ? _custom : IsMacaron ? Macaron : Vivid;

    /// <summary>可选预设色（保存的是鲜明色系原值，显示与套用时按当前色系解析）。</summary>
    public static IReadOnlyList<string> Presets => Vivid;

    public static string Resolve(string hex) => Map(hex, IsCustom ? "Custom" : IsMacaron ? "Macaron" : "Vivid");

    public static bool IsPreset(string hex) => Vivid.Any(value => value.Equals(hex, StringComparison.OrdinalIgnoreCase))
        || Macaron.Any(value => value.Equals(hex, StringComparison.OrdinalIgnoreCase));

    public static string ResolveAccent(string hex, bool isExplicit, bool macaron)
    {
        if (isExplicit && !IsPreset(hex)) return hex;
        return Map(hex, IsCustom ? "Custom" : macaron ? "Macaron" : "Vivid");
    }

    public static string ResolveForMode(string hex, bool isExplicit, string mode)
    {
        if (isExplicit && !IsPreset(hex)) return hex;
        return Map(hex, mode);
    }

    public static string ConvertHex(string hex, bool macaron) => Map(hex, macaron ? "Macaron" : "Vivid");

    private static string Map(string hex, string mode)
    {
        int index = Array.FindIndex(Vivid, value => value.Equals(hex, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = Array.FindIndex(Macaron, value => value.Equals(hex, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return hex;
        if (mode == "Custom" && _custom.Length == Vivid.Length) return _custom[index];
        return (mode == "Macaron" ? Macaron : Vivid)[index];
    }

    // 只替换 RTF 颜色表中的 RGB 项，保留正文、选区格式及自定义颜色。
    public static string ConvertRtf(string rtf, bool macaron) => Regex.Replace(rtf,
        @"\{\\colortbl[^{}]*\}", table => ConvertColorTable(table.Value, macaron));

    private static string ConvertColorTable(string table, bool macaron) => Regex.Replace(table,
        @"\\red(\d+)\\green(\d+)\\blue(\d+)", match =>
        {
            string hex = $"#{int.Parse(match.Groups[1].Value):X2}{int.Parse(match.Groups[2].Value):X2}{int.Parse(match.Groups[3].Value):X2}";
            string mapped = ConvertHex(hex, macaron);
            return $"\\red{Convert.ToInt32(mapped.Substring(1, 2), 16)}\\green{Convert.ToInt32(mapped.Substring(3, 2), 16)}\\blue{Convert.ToInt32(mapped.Substring(5, 2), 16)}";
        });
}
