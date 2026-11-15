namespace WorkTiles.Services;

/// <summary>
/// 视频在背景区域里的摆放，和图片的三种模式一致：缩放铺满并居中裁切，适应完整显示并居中留边，拉伸拉满。
/// 留边区域不画内容，底下的背景色才能露出来。
/// </summary>
public static class VideoPlacement
{
    public readonly record struct Rect(double X, double Y, double Width, double Height);

    public static Rect Place(double sourceWidth, double sourceHeight, double boxWidth, double boxHeight, string mode)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || boxWidth <= 0 || boxHeight <= 0)
            return new Rect(0, 0, Math.Max(0, boxWidth), Math.Max(0, boxHeight));
        if (mode == "Stretch") return new Rect(0, 0, boxWidth, boxHeight);
        double scale = mode == "Fit"
            ? Math.Min(boxWidth / sourceWidth, boxHeight / sourceHeight)
            : Math.Max(boxWidth / sourceWidth, boxHeight / sourceHeight);
        double width = sourceWidth * scale;
        double height = sourceHeight * scale;
        return new Rect((boxWidth - width) / 2, (boxHeight - height) / 2, width, height);
    }
}
