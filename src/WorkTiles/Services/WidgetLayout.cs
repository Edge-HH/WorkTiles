namespace WorkTiles.Services;

/// <summary>组件布局的共同规则，后续组件只需注册稳定标识和视图。</summary>
public static class WidgetLayout
{
    public static void Move(RegionPlacement placement, double dx, double dy, double width, double height)
    {
        placement.X = Math.Clamp(placement.X + dx, 0, Math.Max(0, width - 40));
        placement.Y = Math.Clamp(placement.Y + dy, 0, Math.Max(0, height - 40));
    }
    public static void Resize(RegionPlacement placement, double dx, double dy, double width, double height)
    {
        placement.Width = Math.Clamp(placement.Width + dx, 80, Math.Max(80, width - placement.X));
        placement.Height = Math.Clamp(placement.Height + dy, 48, Math.Max(48, height - placement.Y));
    }
    public static void MoveVerticallyCentered(RegionPlacement placement, double dy, double width, double height)
    {
        placement.X = Math.Max(0, (width - placement.Width) / 2);
        placement.Y = Math.Clamp(placement.Y + dy, 0, Math.Max(0, height - placement.Height));
    }
    public static void ResizeCentered(RegionPlacement placement, double dx, double dy, double aspectRatio, double minimumWidth, double width, double height)
    {
        double safeAspect = Math.Max(.1, aspectRatio);
        double maximumWidth = Math.Max(40, Math.Min(width, Math.Max(0, height - placement.Y) * safeAspect));
        double lowerWidth = Math.Min(minimumWidth, maximumWidth);
        double widthDelta = Math.Abs(dx) >= Math.Abs(dy) ? dx : dy * safeAspect;
        placement.Width = Math.Clamp(placement.Width + widthDelta, lowerWidth, maximumWidth);
        placement.Height = placement.Width / safeAspect;
        MoveVerticallyCentered(placement, 0, width, height);
    }
    /// <summary>
    /// 分隔条拖到两端时返回剩余的单区模式。比例始终表示第一块区域的占比；
    /// clockFirst 为 false 时第一块是作业板，因此两端的单区结果也要对调。
    /// </summary>
    public static string CompleteSplit(double ratio, bool clockFirst = true)
    {
        string completed = ratio <= .04 ? "Board" : ratio >= .96 ? "Clock" : "Split";
        if (clockFirst) return completed;
        return completed switch { "Board" => "Clock", "Clock" => "Board", _ => "Split" };
    }
    public static Dictionary<string, RegionPlacement> Copy(IReadOnlyDictionary<string, RegionPlacement> placements) => placements.ToDictionary(p => p.Key,
        p => new RegionPlacement { X = p.Value.X, Y = p.Value.Y, Width = p.Value.Width, Height = p.Value.Height });
}
