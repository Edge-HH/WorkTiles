namespace WorkTiles.Services;

/// <summary>组件布局的共同规则，后续组件只需注册稳定标识和视图。</summary>
public static class WidgetLayout
{
    /// <summary>自由拖动时，组件中心离分区中轴线多近就吸附过去。</summary>
    public const double CenterSnapDistance = 16;
    /// <summary>吸附后累计外移超过此距离才松开；与触发距离保持一致。</summary>
    public const double CenterSnapReleaseDistance = CenterSnapDistance;
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
    /// 一次等比缩放手势的状态。记录按下手柄时的尺寸与累计指针位移，
    /// 每帧都用完整手势重新计算缩放比例，避免增量误差积累导致尺寸突然加速。
    /// </summary>
    public sealed class AspectResizeState
    {
        internal bool Initialized;
        internal double StartX;
        internal double StartY;
        internal double StartWidth;
        internal double StartHeight;
        internal double TotalX;
        internal double TotalY;
    }

    /// <summary>开始一次等比缩放手势，记录初始锚点和尺寸。</summary>
    public static void BeginAspectResize(RegionPlacement placement, AspectResizeState state)
    {
        state.StartX = placement.X;
        state.StartY = placement.Y;
        state.StartWidth = placement.Width;
        state.StartHeight = placement.Height;
        state.TotalX = 0;
        state.TotalY = 0;
        state.Initialized = true;
    }

    /// <summary>单步等比缩放；没有持续拖动上下文时按一次完整手势处理。</summary>
    public static void ResizeAspect(RegionPlacement placement, double dx, double dy, double aspectRatio,
        double minimumWidth, double width, double height, bool centerX)
    {
        AspectResizeState state = new();
        BeginAspectResize(placement, state);
        ResizeAspect(placement, dx, dy, aspectRatio, minimumWidth, width, height, centerX, state);
    }

    /// <summary>
    /// 缩放组件时始终跟随内容宽高比；centerX 为真时保持水平居中，否则保持左上锚点。
    /// 把累计指针位移投影到组件原始对角线上，缩放框会沿比例线平滑跟随光标，
    /// 不会因为横向或纵向的小抖动被放大成突跳。
    /// </summary>
    public static void ResizeAspect(RegionPlacement placement, double dx, double dy, double aspectRatio,
        double minimumWidth, double width, double height, bool centerX, AspectResizeState state)
    {
        if (!state.Initialized) BeginAspectResize(placement, state);
        state.TotalX += dx;
        state.TotalY += dy;

        double safeAspect = Math.Max(.1, aspectRatio);
        double startWidth = Math.Max(1, state.StartWidth);
        double startHeight = Math.Max(1, state.StartHeight);
        double diagonalSquared = startWidth * startWidth + startHeight * startHeight;
        double scale = diagonalSquared > 1e-9
            ? 1 + (state.TotalX * startWidth + state.TotalY * startHeight) / diagonalSquared
            : 1;
        double targetWidth = startWidth * scale;
        double anchorX = centerX ? 0 : state.StartX;
        double availableWidth = centerX ? width : Math.Max(0, width - anchorX);
        double maximumWidth = Math.Max(40, Math.Min(availableWidth, Math.Max(0, height - state.StartY) * safeAspect));
        double lowerWidth = Math.Min(minimumWidth, maximumWidth);
        placement.Width = Math.Clamp(targetWidth, lowerWidth, maximumWidth);
        placement.Height = placement.Width / safeAspect;
        placement.X = centerX
            ? Math.Max(0, (width - placement.Width) / 2)
            : Math.Clamp(state.StartX, 0, Math.Max(0, width - placement.Width));
        placement.Y = Math.Clamp(state.StartY, 0, Math.Max(0, height - placement.Height));
        RememberContainer(placement, width, height);
    }

    /// <summary>
    /// 停靠组件一次拖动期间的状态。保留未吸附的原始指针位置和累计外移距离，
    /// 小幅抖动不会反复触发吸附/脱离，只有明确向外拖动一小段距离才松开中线。
    /// </summary>
    public sealed class DockedDragState
    {
        internal bool Initialized;
        internal double RawX;
        internal double RawY;
        internal bool SnappedX;
        internal bool SnappedY;
        internal bool CanSnapX;
        internal bool CanSnapY;
        internal double AwayX;
        internal double AwayY;
    }

    /// <summary>开始一次停靠组件拖动；重置原始位置和吸附状态。</summary>
    public static void BeginDockedDrag(RegionPlacement placement, double width, double height, DockedDragState state)
    {
        state.RawX = placement.X;
        state.RawY = placement.Y;
        state.SnappedX = Math.Abs(placement.X + placement.Width / 2 - width / 2) <= 0.01;
        state.SnappedY = Math.Abs(placement.Y + placement.Height / 2 - height / 2) <= 0.01;
        state.CanSnapX = !state.SnappedX;
        state.CanSnapY = !state.SnappedY;
        state.AwayX = 0;
        state.AwayY = 0;
        state.Initialized = true;
    }

    /// <summary>
    /// 停靠组件的拖动：锁定中轴线时只沿竖直中轴线上下移动；关闭后自由拖动，
    /// 但组件中心靠近纵横中轴线时仍会吸附过去，并通过 out 参数告诉界面画辅助线。
    /// 单步调用没有持续拖动上下文，按一次完整拖动处理。
    /// </summary>
    public static void MoveDocked(RegionPlacement placement, double dx, double dy, double width, double height,
        bool lockToCenterAxis, out bool snappedX, out bool snappedY)
    {
        DockedDragState state = new();
        BeginDockedDrag(placement, width, height, state);
        MoveDocked(placement, dx, dy, width, height, lockToCenterAxis, state, out snappedX, out snappedY);
    }

    /// <summary>带拖动状态的停靠组件移动；连续拖动应复用同一个 state。</summary>
    public static void MoveDocked(RegionPlacement placement, double dx, double dy, double width, double height,
        bool lockToCenterAxis, DockedDragState state, out bool snappedX, out bool snappedY)
    {
        if (!state.Initialized) BeginDockedDrag(placement, width, height, state);
        double previousRawCenterX = state.RawX + placement.Width / 2;
        double previousRawCenterY = state.RawY + placement.Height / 2;
        state.RawX = Math.Clamp(state.RawX + dx, 0, Math.Max(0, width - placement.Width));
        state.RawY = Math.Clamp(state.RawY + dy, 0, Math.Max(0, height - placement.Height));
        double midX = width / 2, midY = height / 2;
        if (lockToCenterAxis)
        {
            placement.X = Math.Max(0, midX - placement.Width / 2);
            snappedX = true;
            state.SnappedX = true;
            state.AwayX = 0;
        }
        else
        {
            snappedX = UpdateDockedAxis(previousRawCenterX, state.RawX + placement.Width / 2, midX,
                ref state.SnappedX, ref state.CanSnapX, ref state.AwayX);
            placement.X = snappedX ? Math.Max(0, midX - placement.Width / 2) : state.RawX;
        }
        snappedY = !lockToCenterAxis && UpdateDockedAxis(previousRawCenterY, state.RawY + placement.Height / 2, midY,
            ref state.SnappedY, ref state.CanSnapY, ref state.AwayY);
        placement.Y = snappedY ? Math.Max(0, midY - placement.Height / 2) : state.RawY;
        RememberContainer(placement, width, height);
    }

    /// <summary>
    /// 靠近中线时吸附；吸附后累计向外移动超过一个小阈值才松开。沿另一根轴移动不会解除吸附。
    /// </summary>
    private static bool UpdateDockedAxis(double previousCenter, double nextCenter, double target,
        ref bool snapped, ref bool canSnap, ref double awayDistance)
    {
        double previousDistance = Math.Abs(previousCenter - target);
        double nextDistance = Math.Abs(nextCenter - target);
        if (snapped)
        {
            if (nextDistance > previousDistance + 1e-6)
                awayDistance += nextDistance - previousDistance;
            else
                awayDistance = Math.Max(0, awayDistance - (previousDistance - nextDistance));
            if (awayDistance >= CenterSnapReleaseDistance - 1e-6)
            {
                snapped = false;
                canSnap = nextDistance > CenterSnapDistance;
                awayDistance = 0;
                return false;
            }
            return true;
        }
        // 松开后继续向外拖不会立刻被拉回；反向靠近到内圈时可以再次吸附。
        if (!canSnap)
        {
            if (nextDistance > CenterSnapDistance)
            {
                canSnap = true;
                return false;
            }
            if (nextDistance <= CenterSnapDistance && nextDistance < previousDistance - 1e-6)
            {
                snapped = true;
                canSnap = false;
                awayDistance = 0;
                return true;
            }
            return false;
        }
        if (nextDistance <= CenterSnapDistance)
        {
            snapped = true;
            canSnap = false;
            awayDistance = 0;
            return true;
        }
        return false;
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
