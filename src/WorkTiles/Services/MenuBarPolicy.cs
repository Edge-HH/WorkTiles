namespace WorkTiles.Services;

/// <summary>菜单栏显隐。预览下关闭时，品牌区和项目、文件菜单一起收起。</summary>
public static class MenuBarPolicy
{
    public static bool ShowChrome(bool editing, bool showInPreview) => editing || showInPreview;

    public static bool ShowCommands(bool showChrome, bool overlayOpen) => showChrome && !overlayOpen;
}
