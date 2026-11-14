namespace WorkTiles.Services;

/// <summary>
/// 悬浮岛滚动条。内容放得下时不显示滚动条：Auto 会在差一两个像素时冒出来，
/// 并挤占固定高度，把缩放滑条从百分比文字的中线上挤开。
/// </summary>
public static class IslandScroll
{
    /// <summary>自然尺寸刚好放下时多留的像素，用来吸收布局取整。</summary>
    public const double FitSlack = 2;

    /// <summary>
    /// 自然尺寸放得进可用空间时，槽位比内容多留 <see cref="FitSlack"/>。
    /// 放不进时用可用空间，调用方再决定要不要滚动。
    /// </summary>
    public static double Slot(double extent, double available)
    {
        if (!double.IsFinite(extent) || extent <= 0) return Math.Max(0, double.IsFinite(available) ? available : 0);
        if (double.IsPositiveInfinity(available)) return extent + FitSlack;
        if (!double.IsFinite(available) || available <= 0) return 0;
        if (extent <= available) return Math.Min(available, extent + FitSlack);
        return available;
    }

    /// <summary>槽位比内容窄，才需要滚动条。</summary>
    public static bool Clipped(double extent, double slot) =>
        double.IsFinite(extent) && extent > Math.Max(0, slot) + 0.5;
}
