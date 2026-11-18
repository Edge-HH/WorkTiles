namespace WorkTiles.Services;

public readonly record struct LayoutRect(double X, double Y, double Width, double Height);
public readonly record struct SnapResult(double X, double Y, double? GuideX, double? GuideY);

/// <summary>看板与导出共用的逻辑坐标对齐；不依赖 UI，避免预览缩放影响吸附。</summary>
public static class BoardLayout
{
    /// <summary>不缩小内容的左上角装箱。对齐优先采用整行；无法容纳时再填补边缘空隙。</summary>
    public static List<LayoutRect> Arrange(IReadOnlyList<(double Width, double Height)> tiles,
        double width, double height, double gap = 0, bool align = true, double grid = 0)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidOperationException("画布没有足够的磁贴空间。");
        gap = double.IsFinite(gap) ? Math.Max(0, gap) : 0;
        double Round(double value)
        {
            // 间隔可以是半格。按整格向上取整会把 1.5 格收成 2 格。
            if (grid <= 0) return value;
            double step = grid / 2;
            return Math.Ceiling((value / step) - 1e-9) * step;
        }
        if (tiles.Any(t => !double.IsFinite(t.Width) || !double.IsFinite(t.Height) || t.Width <= 0 || t.Height <= 0))
            throw new InvalidOperationException("磁贴尺寸无效。");
        if (align)
        {
            List<LayoutRect> rows = [];
            double x = 0, y = 0, rowHeight = 0;
            foreach (var tile in tiles)
            {
                if (x > 0 && x + tile.Width > width) { x = 0; y = Round(y + rowHeight + gap); rowHeight = 0; }
                if (tile.Width > width || y + tile.Height > height) break;
                rows.Add(new(x, y, tile.Width, tile.Height));
                x = Round(x + tile.Width + gap); rowHeight = Math.Max(rowHeight, tile.Height);
            }
            if (rows.Count == tiles.Count) return rows;
        }
        List<LayoutRect> placed = [];
        LayoutRect[] result = new LayoutRect[tiles.Count];
        foreach (int i in Enumerable.Range(0, tiles.Count).OrderByDescending(i => tiles[i].Width * tiles[i].Height))
        {
            var tile = tiles[i];
            LayoutRect? found = null;
            // 候选边只随已放置磁贴变化，与当前 y 无关；提到循环外按磁贴算一次，避免每层 y 都重新枚举排序 X。
            double[] candidateY = placed.Select(p => Round(p.Y + p.Height + gap)).Append(0).Distinct().Order().ToArray();
            double[] candidateX = placed.Select(p => Round(p.X + p.Width + gap)).Append(0).Distinct().Order().ToArray();
            foreach (double y in candidateY)
            {
                foreach (double x in candidateX)
                {
                    LayoutRect candidate = new(x, y, tile.Width, tile.Height);
                    if (x + tile.Width > width + .001 || y + tile.Height > height + .001) continue;
                    if (placed.Any(p => x < p.X + p.Width + gap - .001 && x + tile.Width + gap > p.X + .001 &&
                        y < p.Y + p.Height + gap - .001 && y + tile.Height + gap > p.Y + .001)) continue;
                    found = candidate; break;
                }
                if (found is not null) break;
            }
            if (found is not { } rect) throw new InvalidOperationException("空间不足，无法完整显示所有磁贴；请扩大作业板、开启无限作业板或减小间隔。");
            placed.Add(rect); result[i] = rect;
        }
        return result.ToList();
    }

    // 只向上统一相差不超过 16 DIP 的尺寸，不能因对齐裁掉内容。
    public static List<(double Width, double Height)> AlignSizes(IReadOnlyList<(double Width, double Height)> sizes)
    {
        double[] widths = sizes.Select(s => s.Width).ToArray(), heights = sizes.Select(s => s.Height).ToArray();
        static void Align(double[] values)
        {
            var sorted = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
            for (int start = 0; start < sorted.Length;)
            {
                int end = start;
                while (end + 1 < sorted.Length && values[sorted[end + 1]] - values[sorted[start]] <= 16) end++;
                double size = values[sorted[end]];
                for (int j = start; j <= end; j++) values[sorted[j]] = size;
                start = end + 1;
            }
        }
        Align(widths); Align(heights);
        return widths.Select((w, i) => (w, heights[i])).ToList();
    }

    public static SnapResult Snap(LayoutRect moving, IEnumerable<LayoutRect> others,
        double width, double height, double top = 0, double tolerance = 8)
    {
        List<double> xs = [0, width / 2, width];
        List<double> ys = [top, top + (height - top) / 2, height];
        foreach (LayoutRect other in others)
        {
            xs.AddRange([other.X, other.X + other.Width / 2, other.X + other.Width]);
            ys.AddRange([other.Y, other.Y + other.Height / 2, other.Y + other.Height]);
        }
        static (double Value, double? Guide) Axis(double value, double size, double min, double max, List<double> targets, double threshold)
        {
            double best = threshold + .001;
            double result = Math.Clamp(value, min, Math.Max(min, max - size));
            double? guide = null;
            foreach (double target in targets)
            foreach (double offset in new[] { 0d, size / 2, size })
            {
                double next = target - offset, distance = Math.Abs(next - value);
                if (next < min || next + size > max + .001 || distance >= best) continue;
                best = distance; result = next; guide = target;
            }
            return (result, guide);
        }
        var x = Axis(moving.X, moving.Width, 0, width, xs, tolerance);
        var y = Axis(moving.Y, moving.Height, top, height, ys, tolerance);
        return new(x.Value, y.Value, x.Guide, y.Guide);
    }

    /// <summary>
    /// 固定作业板的画板尺寸：视口按当前缩放铺开后的范围，与全部磁贴外接矩形，取较大的那个。
    /// 窗口变小或全屏切换经过极小视口时，画板仍装得下原来的磁贴，调用方不必改磁贴坐标。
    /// </summary>
    public static (double Width, double Height) FixedBoardSize(
        double viewportWidth, double viewportHeight, double zoom, IReadOnlyList<LayoutRect> tiles)
    {
        double safeZoom = zoom > 0 && double.IsFinite(zoom) ? Math.Max(0.05, zoom) : 1;
        double width = double.IsFinite(viewportWidth) ? Math.Max(0, viewportWidth) : 0;
        double height = double.IsFinite(viewportHeight) ? Math.Max(0, viewportHeight) : 0;
        // 缩小后看得见的范围比 100% 视口大；放大时铺开尺寸不超过视口，避免把画板裁到比视口还小。
        width = Math.Max(width, width / safeZoom);
        height = Math.Max(height, height / safeZoom);
        foreach (LayoutRect tile in tiles)
        {
            if (!double.IsFinite(tile.X) || !double.IsFinite(tile.Width) || !double.IsFinite(tile.Y) || !double.IsFinite(tile.Height))
                continue;
            width = Math.Max(width, tile.X + tile.Width);
            height = Math.Max(height, tile.Y + tile.Height);
        }
        return (width, height);
    }

    /// <summary>
    /// 固定作业板允许滚出的距离，单位是屏幕像素。
    /// 放大后内容只比视口略大时，停在半个视口，避免把内容整个拖出画面。
    /// 磁贴超出视口时，上限放到内容尽头，缩小窗口后仍能滚到每一块磁贴。
    /// </summary>
    public static double FixedBoardPanLimit(double viewport, double content, double zoom)
    {
        if (!double.IsFinite(viewport) || viewport <= 0) return 0;
        double safeZoom = zoom > 0 && double.IsFinite(zoom) ? zoom : 1;
        double contentPixels = (double.IsFinite(content) ? Math.Max(0, content) : 0) * safeZoom;
        return Math.Max(viewport / 2, Math.Max(0, contentPixels - viewport));
    }

}
