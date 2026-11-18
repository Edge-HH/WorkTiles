namespace WorkTiles.Services;

/// <summary>桌面磁贴占用的一块屏幕区域，坐标与传入的工作区使用同一单位。</summary>
public readonly record struct DesktopWorkArea(double X, double Y, double Width, double Height);

/// <summary>一块桌面磁贴的位置。宽度是磁贴本身的宽度，高度可能被工作区夹短。</summary>
public readonly record struct DesktopTileSlot(double X, double Y, double Width, double Height);

/// <summary>
/// 桌面磁贴的排列和顺序。从工作区右上角起，下一块优先填进已有磁贴旁边更靠右的空隙；
/// 不再按一列里最宽的那块把后面的磁贴整列推开。顺序单独保存，不改作业板上的坐标。
/// </summary>
public static class DesktopTileLayout
{
    public const double Gap = 12;

    /// <summary>
    /// 按给定顺序把磁贴排进工作区。候选位置取工作区右上角，以及每块已排磁贴的左侧和底边；
    /// 在不重叠、不超出工作区的位置里选最靠右的，同样靠右时选更靠上的。
    /// 单块高于工作区时，槽位高度收到工作区高度，不改调用方传入的尺寸。
    /// 屏幕里实在放不下时仍给出槽位，贴在已有磁贴左侧，避免丢块。
    /// </summary>
    public static IReadOnlyList<DesktopTileSlot> Arrange(IReadOnlyList<(double Width, double Height)> tiles, DesktopWorkArea work, double gap = Gap)
    {
        gap = double.IsFinite(gap) ? Math.Max(0, gap) : Gap;
        if (tiles.Count == 0) return [];
        if (!double.IsFinite(work.X) || !double.IsFinite(work.Y) || work.Width <= 0 || work.Height <= 0)
            return tiles.Select(_ => new DesktopTileSlot(double.IsFinite(work.X) ? work.X : 0, double.IsFinite(work.Y) ? work.Y : 0, 0, 0)).ToList();

        List<DesktopTileSlot> slots = new(tiles.Count);
        double rightEdge = work.X + work.Width;
        double bottom = work.Y + work.Height;
        foreach ((double Width, double Height) tile in tiles)
        {
            double width = double.IsFinite(tile.Width) && tile.Width > 0 ? tile.Width : 1;
            double naturalHeight = double.IsFinite(tile.Height) && tile.Height > 0 ? tile.Height : 1;
            double height = Math.Min(naturalHeight, work.Height);
            DesktopTileSlot? best = null;
            List<double> tops = [work.Y];
            List<double> rights = [rightEdge];
            foreach (DesktopTileSlot slot in slots)
            {
                tops.Add(slot.Y + slot.Height + gap);
                rights.Add(slot.X - gap);
            }
            foreach (double top in tops.Distinct())
            foreach (double right in rights.Distinct())
            {
                double x = right - width;
                double y = top;
                if (x < work.X - 0.001 || x + width > rightEdge + 0.001) continue;
                if (y < work.Y - 0.001 || y + height > bottom + 0.001) continue;
                if (Overlaps(x, y, width, height, slots, gap)) continue;
                if (best is not { } chosen || x > chosen.X + 0.001 || (Math.Abs(x - chosen.X) <= 0.001 && y < chosen.Y - 0.001))
                    best = new DesktopTileSlot(x, y, width, height);
            }
            if (best is null)
            {
                double left = rightEdge;
                foreach (DesktopTileSlot slot in slots) left = Math.Min(left, slot.X);
                double right = slots.Count == 0 ? rightEdge : left - gap;
                best = new DesktopTileSlot(right - width, work.Y, width, height);
            }
            slots.Add(best.Value);
        }
        return slots;
    }

    private static bool Overlaps(double x, double y, double width, double height, List<DesktopTileSlot> slots, double gap)
    {
        foreach (DesktopTileSlot slot in slots)
        {
            if (x < slot.X + slot.Width + gap - 0.001 && x + width + gap > slot.X + 0.001 &&
                y < slot.Y + slot.Height + gap - 0.001 && y + height + gap > slot.Y + 0.001)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 用已保存的科目名顺序套到当前科目上：还在的名字保持原顺序，新科目接在末尾，已经没有的名字丢掉。
    /// 同名科目按出现次数一一对应，多出来的保存项忽略，多出来的当前项按当前顺序补上。
    /// </summary>
    public static List<string> MergeOrder(IReadOnlyList<string>? saved, IReadOnlyList<string> current)
    {
        Dictionary<string, int> remaining = new(StringComparer.Ordinal);
        foreach (string name in current)
            remaining[name] = remaining.GetValueOrDefault(name) + 1;
        List<string> result = new(current.Count);
        if (saved is not null)
        {
            foreach (string name in saved)
            {
                if (!remaining.TryGetValue(name, out int count) || count == 0) continue;
                result.Add(name);
                remaining[name] = count - 1;
            }
        }
        foreach (string name in current)
        {
            if (!remaining.TryGetValue(name, out int count) || count == 0) continue;
            result.Add(name);
            remaining[name] = count - 1;
        }
        return result;
    }

    /// <summary>
    /// 用稳定 Id 套当前科目顺序。优先用已保存的 Id 顺序；没有 Id 顺序时按旧的名字顺序迁移，
    /// 同名按出现次数一一对应。还在的 Id 保持原顺序，新科目接末尾，已删的丢掉。
    /// </summary>
    public static List<Guid> MergeOrderIds(IReadOnlyList<Guid>? savedIds, IReadOnlyList<string>? savedNames,
        IReadOnlyList<(Guid Id, string Name)> current)
    {
        HashSet<Guid> available = new(current.Select(item => item.Id));
        HashSet<Guid> used = [];
        List<Guid> result = new(current.Count);
        if (savedIds is { Count: > 0 })
        {
            foreach (Guid id in savedIds)
                if (available.Contains(id) && used.Add(id)) result.Add(id);
        }
        else if (savedNames is { Count: > 0 })
        {
            List<(Guid Id, string Name)> pool = current.ToList();
            foreach (string name in savedNames)
            {
                int index = pool.FindIndex(item => string.Equals(item.Name, name, StringComparison.Ordinal));
                if (index < 0) continue;
                (Guid id, string _) = pool[index];
                pool.RemoveAt(index);
                if (used.Add(id)) result.Add(id);
            }
        }
        foreach ((Guid id, string _) in current)
            if (used.Add(id)) result.Add(id);
        return result;
    }

    /// <summary>把顺序表指定位置上的旧名字换成新名字。位置对不上时不动，避免改错另一个同名科目。</summary>
    public static bool RenameAt(IList<string> order, int index, string previousName, string nextName)
    {
        if (index < 0 || index >= order.Count) return false;
        if (!string.Equals(order[index], previousName, StringComparison.Ordinal)) return false;
        order[index] = nextName;
        return true;
    }

    /// <summary>
    /// 拖动自动排列的磁贴时，光标会落到哪一次插入。下标是把正在拖的那一块从顺序里拿掉之后的位置。
    /// 光标落在另一块磁贴内部时，下标就是这一块，松手后占住它。
    /// 光标在缝隙里，或仍在自己原来的槽上时，比较这块上方和下方两条交界，取离光标更近的那一条。
    /// 插入线始终画在更近的那条交界上。输入槽位只提供顺序和尺寸，位置会按工作区重新排。
    /// </summary>
    public static DesktopInsertionGuide? InsertionGuide(IReadOnlyList<DesktopTileSlot> slots, int dragging,
        double cursorX, double cursorY, DesktopWorkArea work, double gap = Gap)
    {
        if (slots.Count < 2 || dragging < 0 || dragging >= slots.Count) return null;
        if (!double.IsFinite(cursorX) || !double.IsFinite(cursorY)) return null;
        if (!double.IsFinite(work.X) || !double.IsFinite(work.Y) || work.Width <= 0 || work.Height <= 0) return null;
        gap = double.IsFinite(gap) ? Math.Max(0, gap) : Gap;

        List<(double Width, double Height)> sizes = new(slots.Count);
        foreach (DesktopTileSlot slot in slots)
            sizes.Add((slot.Width, slot.Height));
        IReadOnlyList<DesktopTileSlot> arranged = Arrange(sizes, work, gap);
        if (arranged.Count != slots.Count) return null;

        int slotIndex = NearestSlot(arranged, cursorX, cursorY);
        GuideSegment above = HorizontalBoundary(arranged, slotIndex, above: true, work, gap);
        GuideSegment below = HorizontalBoundary(arranged, slotIndex, above: false, work, gap);
        bool useBelow = DistanceToSegment(cursorX, cursorY, below) < DistanceToSegment(cursorX, cursorY, above) - 0.001;
        int index = useBelow ? IndexAfter(slotIndex, dragging) : IndexBefore(slotIndex, dragging);
        // 标题拖动手停在磁贴上沿，落到下一块的上半部分时，上下沿判断会得到原序号，松手就被当成没移动。
        if (slotIndex != dragging && DistanceToRect(cursorX, cursorY, arranged[slotIndex]) <= 0.001)
            index = slotIndex;
        GuideSegment line = useBelow ? below : above;
        return new DesktopInsertionGuide(index, line.X1, line.Y1, line.X2, line.Y2);
    }

    /// <summary>
    /// 用正在拖的磁贴窗口决定插入位置。窗口盖住哪一块槽的比例最高、且超过它自己原槽的残留比例时，松手就占住那一块。
    /// 原槽在拖动期间一直保留，作为换位的比较基准：窗口离开原槽越多、压住目标越多，就越倾向换位。
    /// 插入线画在目标槽上下两条交界里离鼠标更近的那条；没有鼠标坐标时退回窗口中心。
    /// </summary>
    public static DesktopInsertionGuide? InsertionGuideForTile(IReadOnlyList<DesktopTileSlot> slots, int dragging,
        double tileX, double tileY, double tileWidth, double tileHeight, DesktopWorkArea work, double gap = Gap,
        double cursorX = double.NaN, double cursorY = double.NaN)
    {
        if (slots.Count < 2 || dragging < 0 || dragging >= slots.Count) return null;
        if (!double.IsFinite(tileX) || !double.IsFinite(tileY) || tileWidth <= 0 || tileHeight <= 0) return null;
        if (!double.IsFinite(tileWidth) || !double.IsFinite(tileHeight)) return null;
        if (!double.IsFinite(work.X) || !double.IsFinite(work.Y) || work.Width <= 0 || work.Height <= 0) return null;
        gap = double.IsFinite(gap) ? Math.Max(0, gap) : Gap;

        List<(double Width, double Height)> sizes = new(slots.Count);
        foreach (DesktopTileSlot slot in slots)
            sizes.Add((slot.Width, slot.Height));
        IReadOnlyList<DesktopTileSlot> arranged = Arrange(sizes, work, gap);
        if (arranged.Count != slots.Count) return null;

        int index = dragging;
        double best = Coverage(tileX, tileY, tileWidth, tileHeight, arranged[dragging]);
        for (int i = 0; i < arranged.Count; i++)
        {
            if (i == dragging) continue;
            double coverage = Coverage(tileX, tileY, tileWidth, tileHeight, arranged[i]);
            if (coverage <= best + 0.001) continue;
            best = coverage;
            index = i;
        }
        double lineX = double.IsFinite(cursorX) ? cursorX : tileX + tileWidth / 2;
        double lineY = double.IsFinite(cursorY) ? cursorY : tileY + tileHeight / 2;
        GuideSegment above = HorizontalBoundary(arranged, index, above: true, work, gap);
        GuideSegment below = HorizontalBoundary(arranged, index, above: false, work, gap);
        bool useBelow = DistanceToSegment(lineX, lineY, below) < DistanceToSegment(lineX, lineY, above) - 0.001;
        GuideSegment line = useBelow ? below : above;
        return new DesktopInsertionGuide(index, line.X1, line.Y1, line.X2, line.Y2);
    }

    private readonly record struct GuideSegment(double X1, double Y1, double X2, double Y2);

    /// <summary>插到这块之前。正在拖的那一块，上下两条边都还是原来的位置。</summary>
    private static int IndexBefore(int slot, int dragging) =>
        slot == dragging ? dragging : slot < dragging ? slot : slot - 1;

    /// <summary>插到这块之后。正在拖的那一块拿掉以后，它原来的下沿和上沿是同一个空位。</summary>
    private static int IndexAfter(int slot, int dragging) =>
        slot == dragging ? dragging : slot < dragging ? slot + 1 : slot;

    private static int NearestSlot(IReadOnlyList<DesktopTileSlot> slots, double x, double y)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < slots.Count; i++)
        {
            double distance = DistanceToRect(x, y, slots[i]);
            if (distance >= bestDistance - 0.001) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    /// <summary>这块磁贴上沿或下沿的横线。紧挨着另一块时画在间隔正中，否则贴着外缘再让出半个间隔。</summary>
    private static GuideSegment HorizontalBoundary(IReadOnlyList<DesktopTileSlot> slots, int index, bool above, DesktopWorkArea work, double gap)
    {
        DesktopTileSlot slot = slots[index];
        double right = slot.X + slot.Width;
        double bottom = slot.Y + slot.Height;
        if (above && index > 0 && Stacked(slots[index - 1], slot))
        {
            double y = (slots[index - 1].Y + slots[index - 1].Height + slot.Y) / 2;
            return new GuideSegment(slot.X, y, right, y);
        }
        if (!above && index + 1 < slots.Count && Stacked(slot, slots[index + 1]))
        {
            double y = (bottom + slots[index + 1].Y) / 2;
            return new GuideSegment(slot.X, y, right, y);
        }
        double shift = gap / 2;
        if (above)
        {
            double outward = Math.Min(shift, Math.Max(0, slot.Y - work.Y));
            double y = slot.Y - outward;
            return new GuideSegment(slot.X, y, right, y);
        }
        double room = Math.Max(0, work.Y + work.Height - bottom);
        double yEdge = bottom + Math.Min(shift, room);
        return new GuideSegment(slot.X, yEdge, right, yEdge);
    }

    private static bool Stacked(DesktopTileSlot upper, DesktopTileSlot lower)
    {
        if (upper.Y + upper.Height > lower.Y + 0.5) return false;
        double overlap = Math.Min(upper.X + upper.Width, lower.X + lower.Width) - Math.Max(upper.X, lower.X);
        return overlap > 0.5;
    }

    private static double Coverage(double x, double y, double width, double height, DesktopTileSlot slot)
    {
        double area = slot.Width * slot.Height;
        if (area <= 0.001) return 0;
        double left = Math.Max(x, slot.X);
        double right = Math.Min(x + width, slot.X + slot.Width);
        double top = Math.Max(y, slot.Y);
        double bottom = Math.Min(y + height, slot.Y + slot.Height);
        if (right - left <= 0.001 || bottom - top <= 0.001) return 0;
        return (right - left) * (bottom - top) / area;
    }

    private static double DistanceToRect(double x, double y, DesktopTileSlot slot)
    {
        double dx = x < slot.X ? slot.X - x : x > slot.X + slot.Width ? x - (slot.X + slot.Width) : 0;
        double dy = y < slot.Y ? slot.Y - y : y > slot.Y + slot.Height ? y - (slot.Y + slot.Height) : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceToSegment(double px, double py, GuideSegment line)
    {
        double dx = line.X2 - line.X1;
        double dy = line.Y2 - line.Y1;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-8)
            return Math.Sqrt((px - line.X1) * (px - line.X1) + (py - line.Y1) * (py - line.Y1));
        double t = Math.Clamp(((px - line.X1) * dx + (py - line.Y1) * dy) / lengthSquared, 0, 1);
        double x = line.X1 + t * dx;
        double y = line.Y1 + t * dy;
        return Math.Sqrt((px - x) * (px - x) + (py - y) * (py - y));
    }
}

/// <summary>自动排列拖动时的插入位置，以及那条交界线的两个端点。坐标与槽位使用同一单位。</summary>
public readonly record struct DesktopInsertionGuide(int Index, double X1, double Y1, double X2, double Y2);
