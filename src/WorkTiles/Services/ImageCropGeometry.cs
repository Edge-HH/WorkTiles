namespace WorkTiles.Services;

/// <summary>图片裁切偏移在旋转时怎么跟着画面走。纯计算，方便单独验证。</summary>
public static class ImageCropGeometry
{
    /// <summary>
    /// 屏幕坐标 Y 向下，正角度是顺时针。只处理 90 度这一档。
    /// 横竖互换时偏移必须跟着转，否则取景会滑到照片另一侧。
    /// </summary>
    public static (double X, double Y) RotateOffset(double x, double y, double degrees)
    {
        if (degrees > 0) return (-y, x);
        if (degrees < 0) return (y, -x);
        return (x, y);
    }
}
