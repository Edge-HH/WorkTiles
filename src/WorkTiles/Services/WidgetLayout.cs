namespace WorkTiles.Services;

/// <summary>组件布局的共同规则，后续组件只需注册稳定标识和视图。</summary>
public static class WidgetLayout
{
    /// <summary>自由拖动时，组件中心离分区中轴线多近就吸附过去。</summary>
    public const double CenterSnapDistance = 12;
    /// <summary>等比缩放后的最小可操作尺寸；窗口极小时仍保留一点抓取面积。</summary>
    public const double MinimumScaledWidth = 80;
    public const double MinimumScaledHeight = 32;

    public static void Move(RegionPlacement placement, double dx, double dy, double width, double height)
    {
        placement.X = Math.Clamp(placement.X + dx, 0, Math.Max(0, width - 40));
        placement.Y = Math.Clamp(placement.Y + dy, 0, Math.Max(0, height - 40));
        RememberContainer(placement, width, height);
    }
    public static void Resize(RegionPlacement placement, double dx, double dy, double width, double height)
    {
        placement.Width = Math.Clamp(placement.Width + dx, 80, Math.Max(80, width - placement.X));
        placement.Height = Math.Clamp(placement.Height + dy, 48, Math.Max(48, height - placement.Y));
        RememberContainer(placement, width, height);
    }
    public static void MoveVerticallyCentered(RegionPlacement placement, double dy, double width, double height)
    {
        placement.X = Math.Max(0, (width - placement.Width) / 2);
        placement.Y = Math.Clamp(placement.Y + dy, 0, Math.Max(0, height - placement.Height));
        RememberContainer(placement, width, height);
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
    /// 停靠组件的拖动：锁定中轴线时只沿竖直中轴线上下移动；关闭后自由拖动，
    /// 但组件中心靠近纵横中轴线时仍会吸附过去，并通过 out 参数告诉界面画辅助线。
    /// </summary>
    public static void MoveDocked(RegionPlacement placement, double dx, double dy, double width, double height,
        bool lockToCenterAxis, out bool snappedX, out bool snappedY)
    {
        placement.X = Math.Clamp(placement.X + dx, 0, Math.Max(0, width - placement.Width));
        placement.Y = Math.Clamp(placement.Y + dy, 0, Math.Max(0, height - placement.Height));
        double midX = width / 2, midY = height / 2;
        if (lockToCenterAxis)
        {
            placement.X = Math.Max(0, midX - placement.Width / 2);
            snappedX = true;
        }
        else
        {
            snappedX = Math.Abs(placement.X + placement.Width / 2 - midX) <= CenterSnapDistance;
            if (snappedX) placement.X = Math.Max(0, midX - placement.Width / 2);
        }
        snappedY = !lockToCenterAxis && Math.Abs(placement.Y + placement.Height / 2 - midY) <= CenterSnapDistance;
        if (snappedY) placement.Y = Math.Max(0, midY - placement.Height / 2);
        RememberContainer(placement, width, height);
    }

    /// <summary>
    /// 窗口或分屏尺寸变化时，按新容器与上次记录尺寸的比例同步缩放组件的位置和大小，
    /// 让组件始终停留在原来的相对位置，而不是被挤到上边或保持旧坐标。
    /// </summary>
    public static bool ScaleToContainer(RegionPlacement placement, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1) return false;
        if (!double.IsFinite(placement.LayoutWidth) || !double.IsFinite(placement.LayoutHeight)
            || placement.LayoutWidth < 1 || placement.LayoutHeight < 1)
        {
            RememberContainer(placement, width, height);
            return false;
        }
        double scaleX = width / placement.LayoutWidth, scaleY = height / placement.LayoutHeight;
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY)) return false;
        bool moved = Math.Abs(scaleX - 1) > 1e-6 || Math.Abs(scaleY - 1) > 1e-6;
        if (moved)
        {
            placement.X = Math.Max(0, placement.X * scaleX);
            placement.Y = Math.Max(0, placement.Y * scaleY);
            placement.Width = Math.Max(MinimumScaledWidth, placement.Width * scaleX);
            placement.Height = Math.Max(MinimumScaledHeight, placement.Height * scaleY);
        }
        RememberContainer(placement, width, height);
        return moved;
    }

    /// <summary>
    /// 只把组件收进分区并保持内容宽高比；<paramref name="centerX"/> 为假时保留用户拖出来的水平位置，
    /// 供关闭中轴线锁定后自由摆放使用。缩放窗口时不会再把组件向上挤。
    /// </summary>
    public static void FitAspect(RegionPlacement placement, double aspectRatio, double minimumWidth,
        double width, double height, bool centerX)
    {
        double safeAspect = Math.Max(.1, aspectRatio);
        double maximumWidth = Math.Max(40, Math.Min(width, Math.Max(0, height - placement.Y) * safeAspect));
        double lowerWidth = Math.Min(minimumWidth, maximumWidth);
        placement.Width = Math.Clamp(placement.Width, lowerWidth, maximumWidth);
        placement.Height = placement.Width / safeAspect;
        placement.X = centerX
            ? Math.Max(0, (width - placement.Width) / 2)
            : Math.Clamp(placement.X, 0, Math.Max(0, width - placement.Width));
        placement.Y = Math.Clamp(placement.Y, 0, Math.Max(0, height - placement.Height));
        RememberContainer(placement, width, height);
    }

    private static void RememberContainer(RegionPlacement placement, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1) return;
        placement.LayoutWidth = width;
        placement.LayoutHeight = height;
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
        p => new RegionPlacement
        {
            X = p.Value.X, Y = p.Value.Y, Width = p.Value.Width, Height = p.Value.Height,
            LayoutWidth = p.Value.LayoutWidth, LayoutHeight = p.Value.LayoutHeight
        });
}
