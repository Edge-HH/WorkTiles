namespace WorkTiles.Services;

/// <summary>网格和自动排列间隔的显示规则，不依赖界面。</summary>
public static class LayoutMetrics
{
    public const double MinimumGridSize = 32;
    public const double MaximumGridSize = 160;
    /// <summary>一次要画的格子超过这个数就先不画，避免缩放后网格把界面撑满。</summary>
    public const int MaximumGridCells = 4000;

    public static bool GridTooDense(double width, double height, double gridSize)
    {
        if (width <= 0 || height <= 0 || gridSize <= 0 || !double.IsFinite(width) || !double.IsFinite(height)) return false;
        long columns = (long)Math.Floor(width / gridSize) + 1;
        long rows = (long)Math.Floor(height / gridSize) + 1;
        if (columns < 1 || rows < 1) return false;
        return columns > int.MaxValue / Math.Max(1, rows) || columns * rows > MaximumGridCells;
    }

    /// <summary>吸附开启时按半格显示，例如 0.5 格；关闭时仍用像素。</summary>
    public static string FormatArrangementGap(double pixels, double gridSize, bool snapToGrid)
    {
        double gap = double.IsFinite(pixels) ? Math.Max(0, pixels) : 0;
        if (!snapToGrid) return $"{Math.Round(gap):0} px";
        return FormatCells(PixelsToCells(gap, gridSize));
    }

    public const double ArrangementCellStep = 0.5;
    public const double MaximumArrangementCells = 8;

    public static double PixelsToCells(double pixels, double gridSize)
    {
        double grid = gridSize > 0 && double.IsFinite(gridSize) ? gridSize : 1;
        double gap = double.IsFinite(pixels) ? Math.Max(0, pixels) : 0;
        return SnapCells(gap / grid);
    }

    public static double CellsToPixels(double cells, double gridSize)
    {
        double grid = gridSize > 0 && double.IsFinite(gridSize) ? gridSize : 1;
        return SnapCells(cells) * grid;
    }

    public static double SnapCells(double cells)
    {
        if (!double.IsFinite(cells)) return 0;
        double snapped = Math.Round(Math.Max(0, cells) / ArrangementCellStep, MidpointRounding.AwayFromZero) * ArrangementCellStep;
        return Math.Min(MaximumArrangementCells, snapped);
    }

    public static string FormatCells(double cells) =>
        SnapCells(cells).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " 格";
}
