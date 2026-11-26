using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WorkTiles.Controls;
using WorkTiles.Services;

namespace WorkTiles;

public sealed partial class MainWindow
{
    private bool _desktopTileLookQueued;
    private bool _desktopCommandLookQueued;

    private void BuildDesktopTileSettings()
    {
        MirrorDesktopLooksIfSynced();
        StackPanel general = SettingsStack();
        general.Children.Add(Toggle("在桌面显示磁贴", _settings.DesktopTilesEnabled, value =>
            _settings.DesktopTilesEnabled = value, read: () => _settings.DesktopTilesEnabled));
        general.Children.Add(Note("把当前项目的科目显示在 Windows 桌面上。右下角控制窗可以编辑、添加、自动排列、缩放，或返回作业板。关掉自动排列后，磁贴停在拖放的位置。顺序和位置只保存在这台电脑上，不改变作业板上的坐标。"));
        RegisterSettingsPage("DesktopTiles", DesktopTilesSettingsPanel, general, "常规", "在桌面上显示当前项目的科目磁贴。");

        DesktopTileLook tileLook = _settings.DesktopTileLook;
        StackPanel tile = SettingsStack();
        tile.Children.Add(Toggle("与主界面同步外观", _settings.DesktopTileLookSynced, SetDesktopTileLookSynced, DesktopTileSyncChanged, () => _settings.DesktopTileLookSynced));
        tile.Children.Add(Note("开启后桌面磁贴沿用主界面磁贴外观，下面的项不能修改。关闭后可单独设置。正文默认样式只用于尚未单独设置格式的正文；写进作业后，主界面同一条作业也会看到这份格式。"));
        StackPanel tileAppearance = SettingsStack();
        tileAppearance.Children.Add(Range("标题大小", 16, 72, tileLook.TitleSize, value =>
        {
            if (DesktopTileLookLocked) return;
            tileLook.TitleSize = value;
        }, QueueDesktopTileLook, read: () => EffectiveDesktopTileLook().TitleSize));
        Slider bodySize = Range("正文默认字号", 12, 72, Math.Round(tileLook.BodyFontSize, MidpointRounding.AwayFromZero), value =>
        {
            if (DesktopTileLookLocked) return;
            tileLook.BodyFontSize = Math.Round(value, MidpointRounding.AwayFromZero);
        }, QueueDesktopTileBody, read: () => Math.Round(EffectiveDesktopTileLook().BodyFontSize, MidpointRounding.AwayFromZero));
        bodySize.StepFrequency = 1;
        bodySize.SmallChange = 1;
        bodySize.LargeChange = 4;
        tileAppearance.Children.Add(bodySize);
        tileAppearance.Children.Add(BodyFontPicker(() => EffectiveDesktopTileLook().BodyFontFamily, value =>
        {
            if (DesktopTileLookLocked) return;
            tileLook.BodyFontFamily = value;
        }, QueueDesktopTileBody));
        tileAppearance.Children.Add(Toggle("正文默认粗体", tileLook.BodyBold, value =>
        {
            if (DesktopTileLookLocked) return;
            tileLook.BodyBold = value;
        }, QueueDesktopTileBody, () => EffectiveDesktopTileLook().BodyBold));
        tileAppearance.Children.Add(Toggle("正文默认斜体", tileLook.BodyItalic, value =>
        {
            if (DesktopTileLookLocked) return;
            tileLook.BodyItalic = value;
        }, QueueDesktopTileBody, () => EffectiveDesktopTileLook().BodyItalic));
        tileAppearance.Children.Add(Toggle("右下角显示学科字样", tileLook.ShowSubjectLabel, value =>
        {
            if (DesktopTileLookLocked) return;
            tileLook.ShowSubjectLabel = value;
        }, QueueDesktopTileLook, () => EffectiveDesktopTileLook().ShowSubjectLabel));
        tileAppearance.Children.Add(Range("磁贴圆角", 0, 40, tileLook.CornerRadius, value =>
        {
            if (DesktopTileLookLocked) return;
            tileLook.CornerRadius = value;
        }, QueueDesktopTileLook, read: () => EffectiveDesktopTileLook().CornerRadius));
        tileAppearance.Children.Add(BackgroundEditor(tileLook.Background, () => true, () => true, surface: true, scope: MediaScope.Tile, surfaceChanged: QueueDesktopTileLook));
        ContentControl tileLock = new() { Content = tileAppearance, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        tile.Children.Add(tileLock);
        _refreshSettingAvailability.Add(() => tileLock.IsEnabled = !_settings.DesktopTileLookSynced);
        RegisterSettingsPage("DesktopTileLook", DesktopTilesSettingsPanel, tile, "磁贴", "调整桌面磁贴的标题、正文和背景。");

        DesktopCommandLook commandLook = _settings.DesktopCommandLook;
        StackPanel command = SettingsStack();
        command.Children.Add(Toggle("与主界面同步外观", _settings.DesktopCommandLookSynced, SetDesktopCommandLookSynced, DesktopCommandSyncChanged, () => _settings.DesktopCommandLookSynced));
        command.Children.Add(Note("开启后桌面控制窗沿用主界面控制窗的大小、圆角、边框和背景。关闭后只影响桌面上的控制窗。"));
        StackPanel commandAppearance = SettingsStack();
        commandAppearance.Children.Add(SurfaceColorEditor(commandLook.BackgroundColor, commandLook.BackgroundOpacity,
            () => commandLook.BackgroundColorCleared,
            (value, cleared) =>
            {
                if (DesktopCommandLookLocked) return;
                commandLook.BackgroundColor = value;
                commandLook.BackgroundColorCleared = cleared;
            },
            value =>
            {
                if (DesktopCommandLookLocked) return;
                commandLook.BackgroundOpacity = value;
            },
            QueueDesktopCommandChrome,
            () => EffectiveDesktopCommandLook().BackgroundColor,
            () => EffectiveDesktopCommandLook().BackgroundOpacity));
        commandAppearance.Children.Add(Range("大小", .6, 2, commandLook.Scale, value =>
        {
            if (DesktopCommandLookLocked) return;
            commandLook.Scale = value;
        }, QueueDesktopCommandChrome, read: () => EffectiveDesktopCommandLook().Scale));
        commandAppearance.Children.Add(Range("圆角", 0, ToolbarChrome.MaxRoundness, ToolbarChrome.NormalizeRoundness(commandLook.Radius), value =>
        {
            if (DesktopCommandLookLocked) return;
            commandLook.Radius = ToolbarChrome.NormalizeRoundness(value);
        }, QueueDesktopCommandChrome, read: () => ToolbarChrome.NormalizeRoundness(EffectiveDesktopCommandLook().Radius)));
        commandAppearance.Children.Add(Range("边框粗细", 0, 20, commandLook.BorderThickness, value =>
        {
            if (DesktopCommandLookLocked) return;
            commandLook.BorderThickness = value;
        }, QueueDesktopCommandChrome, read: () => EffectiveDesktopCommandLook().BorderThickness));
        ColorPicker borderColor = new()
        {
            IsAlphaEnabled = true, IsHexInputVisible = true,
            Color = GridAppearance.ParseColor(commandLook.BorderColor, BoardTheme.LineColorFor(BoardTheme.IsLight))
        };
        borderColor.ColorChanged += (_, args) =>
        {
            if (_refreshingSettingControls || DesktopCommandLookLocked) return;
            commandLook.BorderColor = GridAppearance.FormatColor(args.NewColor);
            QueueDesktopCommandChrome();
        };
        _refreshSettingControls.Add(() =>
        {
            Windows.UI.Color next = GridAppearance.ParseColor(EffectiveDesktopCommandLook().BorderColor, BoardTheme.LineColorFor(BoardTheme.IsLight));
            if (borderColor.Color != next) borderColor.Color = next;
        });
        commandAppearance.Children.Add(new Button { Content = "边框颜色", Flyout = new Flyout { Content = borderColor } });
        Button resetBorder = new() { Content = "恢复主题边框色" };
        resetBorder.Click += (_, _) =>
        {
            if (DesktopCommandLookLocked) return;
            commandLook.BorderColor = "";
            QueueDesktopCommandChrome();
        };
        commandAppearance.Children.Add(resetBorder);
        commandAppearance.Children.Add(Toggle("毛玻璃效果", commandLook.Glass, value =>
        {
            if (DesktopCommandLookLocked) return;
            commandLook.Glass = value;
        }, () =>
        {
            foreach (Action refresh in _refreshSettingAvailability) refresh();
            QueueDesktopCommandChrome();
        }, () => EffectiveDesktopCommandLook().Glass));
        Slider commandBlur = Range("模糊程度", 0, 100, commandLook.Blur, value =>
        {
            if (DesktopCommandLookLocked) return;
            commandLook.Blur = value;
        }, QueueDesktopCommandChrome, read: () => EffectiveDesktopCommandLook().Blur);
        commandAppearance.Children.Add(commandBlur);
        // 父面板禁用时子项已经不可点。这里只在可编辑时跟随毛玻璃开关。
        _refreshSettingAvailability.Add(() => commandBlur.IsEnabled = commandLook.Glass);
        ContentControl commandLock = new() { Content = commandAppearance, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        command.Children.Add(commandLock);
        _refreshSettingAvailability.Add(() => commandLock.IsEnabled = !_settings.DesktopCommandLookSynced);
        RegisterSettingsPage("DesktopCommandLook", DesktopTilesSettingsPanel, command, "控制窗", "调整桌面控制窗的大小、边框和背景。");
    }

    private bool DesktopTileLookLocked => _settings.DesktopTileLookSynced || _refreshingSettingControls;
    private bool DesktopCommandLookLocked => _settings.DesktopCommandLookSynced || _refreshingSettingControls;

    private DesktopTileLook EffectiveDesktopTileLook() => _settings.EffectiveDesktopTileLook();
    private DesktopCommandLook EffectiveDesktopCommandLook() => _settings.EffectiveDesktopCommandLook();

    private void SetDesktopTileLookSynced(bool synced)
    {
        if (!synced && _settings.DesktopTileLookSynced)
            _settings.DesktopTileLook.CopyFrom(_settings);
        _settings.DesktopTileLookSynced = synced;
    }

    private void SetDesktopCommandLookSynced(bool synced)
    {
        if (!synced && _settings.DesktopCommandLookSynced)
            _settings.DesktopCommandLook.CopyFrom(_settings);
        _settings.DesktopCommandLookSynced = synced;
    }

    private void DesktopTileSyncChanged()
    {
        RefreshSettingControls();
        foreach (Action refresh in _refreshSettingAvailability) refresh();
        ApplyDesktopTileSurfaces();
        ScheduleSave();
    }

    private void DesktopCommandSyncChanged()
    {
        RefreshSettingControls();
        foreach (Action refresh in _refreshSettingAvailability) refresh();
        ApplyDesktopCommandChrome();
        ScheduleSave();
    }

    private void ApplyDesktopTileSurfaces()
    {
        ForEachDesktopTile(tile => tile.ApplyAppearance(_settings));
        ApplyDesktopTileBackdrops();
    }

    private void QueueDesktopTileLook()
    {
        if (DesktopTileLookLocked) return;
        if (_desktopTileLookQueued) return;
        _desktopTileLookQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _desktopTileLookQueued = false;
            if (_settings.DesktopTileLookSynced) return;
            ApplyDesktopTileSurfaces();
            ScheduleSave();
        });
    }

    private void QueueDesktopTileBody()
    {
        if (DesktopTileLookLocked) return;
        ForEachDesktopTile(tile => tile.ApplyBodyDefaults(_settings));
        ScheduleSave();
    }

    private void QueueDesktopCommandChrome()
    {
        if (DesktopCommandLookLocked) return;
        if (_desktopCommandLookQueued) return;
        _desktopCommandLookQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _desktopCommandLookQueued = false;
            if (_settings.DesktopCommandLookSynced) return;
            ApplyDesktopCommandChrome();
            ScheduleSave();
        });
    }

    private FrameworkElement BodyFontPicker(Func<string> read, Action<string> write, Action changed)
    {
        AutoSuggestBox input = new()
        {
            Header = "正文默认字体",
            Text = FontService.NormalizeFamilyName(read()),
            ItemsSource = FontService.AvailableFamilies,
            MaxSuggestionListHeight = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        input.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                input.ItemsSource = FontService.AvailableFamilies
                    .Where(font => font.Contains(input.Text, StringComparison.CurrentCultureIgnoreCase)).Take(80).ToArray();
        };
        void Apply(string? family)
        {
            if (_refreshingSettingControls) return;
            string normalized = FontService.NormalizeFamilyName(family);
            input.Text = normalized;
            if (read() == normalized) return;
            write(normalized);
            changed();
        }
        input.QuerySubmitted += (_, args) => Apply(args.ChosenSuggestion as string ?? args.QueryText);
        input.SuggestionChosen += (_, args) => Apply(args.SelectedItem as string);
        _refreshSettingControls.Add(() =>
        {
            string current = FontService.NormalizeFamilyName(read());
            if (input.Text != current) input.Text = current;
        });
        return input;
    }
}
