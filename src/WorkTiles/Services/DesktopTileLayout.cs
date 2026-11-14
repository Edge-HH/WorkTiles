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

    /// <summary>把顺序表指定位置上的旧名字换成新名字。位置对不上时不动，避免改错另一个同名科目。</summary>
    public static bool RenameAt(IList<string> order, int index, string previousName, string nextName)
    {
        if (index < 0 || index >= order.Count) return false;
        if (!string.Equals(order[index], previousName, StringComparison.Ordinal)) return false;
        order[index] = nextName;
        return true;
    }
}
