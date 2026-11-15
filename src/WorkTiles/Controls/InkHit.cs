using WorkTiles.Services;
using Windows.Foundation;

namespace WorkTiles.Controls;

/// <summary>橡皮擦命中。复用栈上的点缓冲，避免每一帧为每一笔分配一份坐标列表。</summary>
internal static class InkHit
{
    public static bool Contains(IReadOnlyList<Point> points, double x, double y, double radius)
    {
        int count = points.Count;
        if (count == 0) return false;
        if (count <= 256)
        {
            Span<(double X, double Y)> buffer = stackalloc (double X, double Y)[count];
            for (int i = 0; i < count; i++)
            {
                Point point = points[i];
                buffer[i] = (point.X, point.Y);
            }
            return InkGeometry.HitTest(buffer, x, y, radius);
        }

        var copy = new (double X, double Y)[count];
        for (int i = 0; i < count; i++) copy[i] = (points[i].X, points[i].Y);
        return InkGeometry.HitTest(copy, x, y, radius);
    }
}
