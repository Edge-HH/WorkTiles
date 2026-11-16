namespace WorkTiles.Services;

public static class InkGeometry
{
    public static bool HitTest(IReadOnlyList<(double X, double Y)> points, double x, double y, double radius)
    {
        var buffer = new (double X, double Y)[points.Count];
        for (int i = 0; i < points.Count; i++) buffer[i] = points[i];
        return HitTest(buffer.AsSpan(), x, y, radius);
    }

    public static bool HitTest(ReadOnlySpan<(double X, double Y)> points, double x, double y, double radius)
    {
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[Math.Min(i + 1, points.Length - 1)];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = dx * dx + dy * dy;
            double t = length == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
            double ex = x - (a.X + t * dx), ey = y - (a.Y + t * dy);
            if (ex * ex + ey * ey <= radius * radius) return true;
        }
        return false;
    }
}
