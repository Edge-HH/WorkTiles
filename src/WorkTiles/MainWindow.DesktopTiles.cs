using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WorkTiles.Controls;
using WorkTiles.Models;
using WorkTiles.Services;

namespace WorkTiles;

public sealed partial class MainWindow
{
    private readonly Dictionary<SubjectBoard, DesktopTileWindow> _desktopWindows = new(SubjectIdentity.Instance);
    private readonly Dictionary<SubjectBoard, string> _desktopKnownNames = new(SubjectIdentity.Instance);
    private readonly List<SubjectBoard> _desktopSubjects = [];
    private readonly List<RectInt32> _desktopSlots = [];
    private DesktopCommandWindow? _desktopCommands;
    private SubjectBoard? _desktopDragSubject;
    private SubjectBoard? _desktopResizeSubject;
    private DesktopResizeEdge _desktopResizeEdge;
    private int _resizeOriginX;
    private int _resizeOriginY;
    private int _resizeOriginWidth;
    private int _resizeOriginHeight;
    private int _resizeCursorX;
    private int _resizeCursorY;
    private bool _desktopEditing;
    private Guid? _desktopEditingProject;
    private SubjectTileControl? _tileContentSource;
    private bool _desktopClosed;
    private bool _syncingDesktop;
    private bool _desktopSyncPosted;
    private bool _desktopSyncAgain;
    private double _desktopScale = 1;
    private int _desktopWorkX = int.MinValue;
    private int _desktopWorkY = int.MinValue;
    private int _desktopWorkWidth;
    private int _desktopWorkHeight;
    private uint _desktopDpi;
    private readonly DispatcherTimer _desktopCommandAnimTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _desktopCommandHidden;
    private long _desktopCommandActivity = Environment.TickCount64;
    private long _desktopCommandAnimStart;
    private int _desktopCommandCursorX = int.MinValue;
    private int _desktopCommandCursorY = int.MinValue;
    private int _desktopCommandFromX;
    private int _desktopCommandFromY;
    private int _desktopCommandToX;
    private int _desktopCommandToY;
    private double _desktopCommandFromOpacity = 1;
    private double _desktopCommandToOpacity = 1;

    private void InitializeDesktopTiles()
    {
        if (_appWindow is not null)
            _appWindow.Changed += (_, args) =>
            {
                if (_desktopClosed || !_settings.DesktopTilesEnabled) return;
                if (!args.DidPositionChange && !args.DidSizeChange) return;
                DispatcherQueue.TryEnqueue(ArrangeDesktopTilesIfWorkAreaChanged);
            };
        Closed += (_, _) =>
        {
            _desktopClosed = true;
            CloseDesktopSurfaces();
        };
        _desktopCommandAnimTimer.Tick += (_, _) => AdvanceDesktopCommandAnimation();
    }

    /// <summary>右下角控制窗点了编辑，并且编辑锁已经通过。</summary>
    private bool DesktopEditActive =>
        _desktopEditing && _settings.DesktopTilesEnabled && CurrentProject is not null && !NeedsEditAuth() && !NeedsCloudEditLock();

    private void ToggleDesktopEdit()
    {
        if (_desktopEditing) EndDesktopEdit();
        else BeginDesktopEdit();
    }

    private void BeginDesktopEdit(Action? then = null)
    {
        if (_desktopClosed || _initialView == "verification") return;
        void Enable()
        {
            _desktopEditing = true;
            ApplyDesktopEditing();
            if (_editLockGranted is not null) StartEditLockHeartbeat();
            then?.Invoke();
        }
        if (NeedsCloudEditLock())
        {
            RequestDesktopLockThen(() =>
            {
                if (NeedsEditAuth()) RequestDesktopUnlockThen(Enable);
                else Enable();
            });
            return;
        }
        if (NeedsEditAuth()) { RequestDesktopUnlockThen(Enable); return; }
        Enable();
    }

    private void EndDesktopEdit()
    {
        _desktopEditing = false;
        ApplyDesktopEditing();
        if (!_isEditing) ReleaseCloudEditLock();
    }

    private void ApplyDesktopEditing()
    {
        bool editing = DesktopEditActive;
        foreach (DesktopTileWindow window in _desktopWindows.Values)
            window.SetEditing(editing);
        _desktopCommands?.SetEditing(editing);
        StackDesktopCommand();
        _desktopCommandActivity = Environment.TickCount64;
        UpdateDesktopCommandAutoHide();
    }

    private void RequestDesktopUnlockThen(Action then)
    {
        if (_editAuthPending) return;
        _editAuthPending = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (await UnlockAsync())
                {
                    _editAuthGranted = true;
                    then();
                }
            }
            finally { _editAuthPending = false; }
        });
    }

    private void RequestDesktopLockThen(Action then)
    {
        if (_editLockPending || CurrentProject is not { } project) return;
        _editLockPending = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                CloudSyncService.EditLockResult result = await CloudSync.AcquireEditLockAsync(_settings.CloudSync, project.Id);
                if (CurrentProject?.Id != project.Id)
                {
                    if (result.Acquired) _ = CloudSync.ReleaseEditLockAsync(_settings.CloudSync, project.Id);
                    return;
                }
                if (!result.Acquired)
                {
                    ShowCloudNotice(EditLockedText(project, result.HeldBy), InfoBarSeverity.Warning, "云端修改中");
                    return;
                }
                _editLockGranted = project.Id;
                then();
            }
            finally { _editLockPending = false; }
        });
    }

    private void AddDesktopSubject()
    {
        if (_desktopEditing && DesktopEditActive) { AddSubjectCore(); return; }
        BeginDesktopEdit(AddSubjectCore);
    }

    private void ReturnToBoard()
    {
        ShowBoard();
        SingleInstanceService.BringToForeground(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void NoteTileContentChanged(SubjectTileControl source)
    {
        _tileContentSource = source;
        ScheduleSave();
    }

    /// <summary>保存节拍上刷新另一边没有正在改的那份磁贴。</summary>
    private void ReloadIdleTileContent()
    {
        if (!_settings.DesktopTilesEnabled) return;
        SubjectTileControl? source = _tileContentSource;
        _tileContentSource = null;
        if (BoardCanvas is not null)
        {
            foreach (SubjectTileControl tile in BoardCanvas.Children.OfType<SubjectTileControl>())
                if (!ReferenceEquals(tile, source)) tile.ReloadFromModel();
        }
        foreach (DesktopTileWindow window in _desktopWindows.Values)
        {
            if (window.Tile is { } tile && !ReferenceEquals(tile, source)) tile.ReloadFromModel();
        }
    }

    private void ForEachDesktopTile(Action<SubjectTileControl> action)
    {
        foreach (DesktopTileWindow window in _desktopWindows.Values)
            if (window.Tile is { } tile) action(tile);
    }

    private void SyncDesktopTiles()
    {
        if (_desktopClosed || _initialView == "verification") return;
        if (_syncingDesktop)
        {
            _desktopSyncAgain = true;
            return;
        }
        // 主题切换和主窗口加载过程中同步创建 RichEditBox 会触发 WinUI 的访问冲突。等这一轮消息结束再做。
        if (_desktopSyncPosted) return;
        _desktopSyncPosted = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RunDesktopSync);
    }

    private void RunDesktopSync()
    {
        _desktopSyncPosted = false;
        if (_desktopClosed || !_isLoaded || _initialView == "verification") return;
        _syncingDesktop = true;
        try
        {
        if (!_settings.DesktopTilesEnabled || CurrentProject is null)
        {
            if (_desktopEditing) EndDesktopEdit();
            CloseDesktopSurfaces();
            _desktopEditingProject = null;
            return;
        }

        List<SubjectBoard> ordered = OrderDesktopSubjects();
        HashSet<SubjectBoard> keep = new(ordered, SubjectIdentity.Instance);
        foreach (SubjectBoard stale in _desktopWindows.Keys.Where(subject => !keep.Contains(subject)).ToList())
        {
            stale.PropertyChanged -= DesktopSubject_PropertyChanged;
            _desktopKnownNames.Remove(stale);
            _desktopWindows[stale].CloseSurface();
            _desktopWindows.Remove(stale);
        }
        _desktopSubjects.Clear();
        _desktopSubjects.AddRange(ordered);

        if (CurrentProject.Id != _desktopEditingProject)
        {
            _desktopEditingProject = CurrentProject.Id;
            if (_desktopEditing) EndDesktopEdit();
        }
        ElementTheme theme = RootShell.RequestedTheme;
        bool editing = DesktopEditActive;
        foreach (SubjectBoard subject in ordered)
        {
            if (!_desktopWindows.TryGetValue(subject, out DesktopTileWindow? window))
            {
                window = CreateDesktopWindow(subject);
                _desktopWindows[subject] = window;
            }
            WatchDesktopSubject(subject);
            window.ApplyTheme(theme);
            window.ApplyBackgroundSettings(_settings.TileBackground);
            window.SetEditing(editing);
            window.TitleText = subject.Name;
        }
        _desktopCommands ??= CreateDesktopCommands();
        _desktopCommands.ApplyTheme(theme);
        _desktopCommands.SetEditing(editing);
        ApplyDesktopCommandChrome();
        ArrangeDesktopTiles();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
        }
        finally { _syncingDesktop = false; }
        if (_desktopSyncAgain)
        {
            _desktopSyncAgain = false;
            SyncDesktopTiles();
        }
    }

    private DesktopTileWindow CreateDesktopWindow(SubjectBoard subject)
    {
        DesktopTileWindow window = new() { TitleText = subject.Name, AboveTiles = false };
        window.ApplyTheme(RootShell.RequestedTheme);
        window.ContentReady += () => AttachDesktopTile(window, subject);
        window.EnsureShown();
        return window;
    }

    private void AttachDesktopTile(DesktopTileWindow window, SubjectBoard subject)
    {
        if (window.Tile is not null) return;
        AutofillPopup popup = new();
        SubjectTileControl? tile = null;
        tile = new SubjectTileControl(
            subject,
            DeleteSubject,
            _ => { },
            _ => { },
            _ => { },
            AddAttachmentAsync,
            () => { if (tile is not null) NoteTileContentChanged(tile); },
            _settings,
            _autofill,
            popup)
        {
            DataContext = subject,
            LockBoardGeometry = true
        };
        tile.ApplyAppearance(_settings);
        tile.DesktopDragStarted += () =>
        {
            _desktopDragSubject = subject;
            if (_desktopWindows.TryGetValue(subject, out DesktopTileWindow? dragging)) dragging.BeginExternalDrag();
        };
        tile.DesktopDragDelta += (_, _) =>
        {
            if (_desktopWindows.TryGetValue(subject, out DesktopTileWindow? dragging)) dragging.FollowCursor();
        };
        tile.DesktopDragCompleted += () =>
        {
            if (_desktopWindows.TryGetValue(subject, out DesktopTileWindow? dragging)) dragging.EndExternalDrag();
            SubjectBoard? dragged = _desktopDragSubject;
            _desktopDragSubject = null;
            if (dragged is not null) FinishDesktopDrag(dragged);
        };
        tile.DesktopResizeStarted += edge => BeginDesktopResize(subject, edge);
        tile.DesktopResizeDelta += FollowDesktopResize;
        tile.DesktopResizeCompleted += FinishDesktopResize;
        tile.FormattingToolbarChanged += (toolbar, active) =>
        {
            if (active) _desktopCommands?.ShowFormatting(toolbar);
            else _desktopCommands?.HideFormatting(toolbar);
        };
        window.HostDoubleClick += () => BeginDesktopEdit();
        window.Attach(tile, popup);
        window.ApplyBackgroundSettings(_settings.TileBackground);
        window.SetEditing(DesktopEditActive);
        ArrangeDesktopTiles();
    }

    private DesktopCommandWindow CreateDesktopCommands()
    {
        DesktopCommandWindow window = new();
        window.ApplyTheme(RootShell.RequestedTheme);
        window.EditInvoked += ToggleDesktopEdit;
        window.AddInvoked += AddDesktopSubject;
        window.AboveTiles = true;
        window.AutoArrangeInvoked += SetDesktopAutoArrange;
        window.ZoomChanged += SetDesktopZoom;
        window.ReturnInvoked += ReturnToBoard;
        window.ChromeChanged += PlaceDesktopCommands;
        window.SetAutoArrange(_settings.DesktopTilesAutoArrange);
        window.SetZoom(DesktopZoom);
        window.SetEditing(DesktopEditActive);
        ApplyDesktopCommandChrome();
        return window;
    }

    private void WatchDesktopSubject(SubjectBoard subject)
    {
        if (!_desktopKnownNames.TryAdd(subject, subject.Name)) return;
        subject.PropertyChanged += DesktopSubject_PropertyChanged;
    }

    private void DesktopSubject_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SubjectBoard.Name) || sender is not SubjectBoard subject) return;
        if (!_desktopKnownNames.TryGetValue(subject, out string? previous) || string.Equals(previous, subject.Name, StringComparison.Ordinal))
            return;
        if (CurrentProject is { } project)
        {
            _settings.DesktopTileOrder ??= new();
            string key = project.Id.ToString();
            if (!_settings.DesktopTileOrder.TryGetValue(key, out List<string>? order) || order is null)
            {
                order = [];
                _settings.DesktopTileOrder[key] = order;
            }
            int index = _desktopSubjects.FindIndex(item => ReferenceEquals(item, subject));
            if (!DesktopTileLayout.RenameAt(order, index, previous, subject.Name))
            {
                order.Clear();
                order.AddRange(_desktopSubjects.Select(item => item.Name));
            }
            if (_desktopWindows.TryGetValue(subject, out DesktopTileWindow? window)) window.TitleText = subject.Name;
            foreach (DesktopTilePlacement placement in Placements())
            {
                if (!string.Equals(placement.Name, previous, StringComparison.Ordinal)) continue;
                placement.Name = subject.Name;
                break;
            }
            ScheduleSave();
        }
        _desktopKnownNames[subject] = subject.Name;
    }

    private List<SubjectBoard> OrderDesktopSubjects()
    {
        _settings.DesktopTileOrder ??= new();
        string key = CurrentProject!.Id.ToString();
        if (!_settings.DesktopTileOrder.TryGetValue(key, out List<string>? saved) || saved is null) saved = [];
        List<string> merged = DesktopTileLayout.MergeOrder(saved, ViewModel.Subjects.Select(subject => subject.Name).ToList());
        if (!saved.SequenceEqual(merged))
        {
            _settings.DesktopTileOrder[key] = merged;
            ScheduleSave();
        }
        List<SubjectBoard> remaining = ViewModel.Subjects.ToList();
        List<SubjectBoard> ordered = new(remaining.Count);
        foreach (string name in merged)
        {
            int index = remaining.FindIndex(subject => string.Equals(subject.Name, name, StringComparison.Ordinal));
            if (index < 0) continue;
            ordered.Add(remaining[index]);
            remaining.RemoveAt(index);
        }
        ordered.AddRange(remaining);
        return ordered;
    }

    private double DesktopZoom => _settings.DesktopTilesZoom is >= 0.5 and <= 2 ? _settings.DesktopTilesZoom : 1;

    private double DesktopLayoutGap => double.IsFinite(_settings.AutoLayoutGap) ? Math.Max(0, _settings.AutoLayoutGap) : 0;

    private void SetDesktopAutoArrange(bool enabled)
    {
        if (enabled == _settings.DesktopTilesAutoArrange) return;
        // 先按屏幕上的当前位置记下布局，再关开关。不能在关了之后按旧的自由坐标重摆。
        if (!enabled) SnapshotFreePlacements();
        _settings.DesktopTilesAutoArrange = enabled;
        _desktopCommands?.SetAutoArrange(enabled);
        ScheduleSave();
        if (enabled) ArrangeDesktopTiles();
        else
        {
            PlaceDesktopCommands();
            StackDesktopCommand();
        }
    }

    private void ApplyDesktopCommandChrome()
    {
        if (_desktopCommands is null) return;
        double scale = Math.Clamp(_settings.ToolbarScale, .6, 2);
        double padding = 7 * scale;
        double button = 40 * scale;
        double outer = button + padding * 2 + IslandBorderThickness * 2;
        double pixelRadius = ToolbarChrome.CornerRadius(_settings.ToolbarRadius, outer, outer);
        double radius = MatchToolbarButtonRadius(pixelRadius, padding, button, button).TopLeft;
        _desktopCommands.ApplyToolbarChrome(
            CreateToolbarBackground(),
            CreateToolbarBorder(_settings.ToolbarBorderColor),
            IslandBorderThickness,
            pixelRadius,
            radius,
            button,
            16 * scale,
            _settings.ToolbarGlass,
            _settings.ToolbarBackgroundColor,
            _settings.ToolbarBackgroundOpacity,
            _settings.ToolbarBackgroundColorCleared,
            _settings.ToolbarBlur);
    }

    private void ApplyDesktopTileBackdrops()
    {
        foreach (DesktopTileWindow window in _desktopWindows.Values)
            window.ApplyBackgroundSettings(_settings.TileBackground);
    }

    private void SetDesktopZoom(double zoom)
    {
        _settings.DesktopTilesZoom = Math.Clamp(zoom, 0.5, 2);
        _desktopCommands?.SetZoom(_settings.DesktopTilesZoom);
        ScheduleSave();
        ArrangeDesktopTiles();
    }

    private void FinishDesktopDrag(SubjectBoard subject)
    {
        if (!DesktopEditActive || CurrentProject is null)
        {
            ArrangeDesktopTiles();
            return;
        }
        if (!_settings.DesktopTilesAutoArrange)
        {
            if (_desktopWindows.TryGetValue(subject, out DesktopTileWindow? moved)) SaveFreePosition(subject, moved.PixelBounds);
            ScheduleSave();
            return;
        }
        if (!DesktopWindowNative.GetCursorPos(out DesktopWindowNative.DesktopPoint point))
        {
            ArrangeDesktopTiles();
            return;
        }
        int from = _desktopSubjects.FindIndex(item => ReferenceEquals(item, subject));
        int hit = NearestDesktopSlot(point.X, point.Y);
        if (from < 0 || hit < 0 || hit == from)
        {
            ArrangeDesktopTiles();
            return;
        }
        List<SubjectBoard> ordered = _desktopSubjects.ToList();
        ordered.RemoveAt(from);
        ordered.Insert(Math.Clamp(hit, 0, ordered.Count), subject);
        _settings.DesktopTileOrder ??= new();
        _settings.DesktopTileOrder[CurrentProject.Id.ToString()] = ordered.Select(item => item.Name).ToList();
        ScheduleSave();
        SyncDesktopTiles();
    }

    /// <summary>松手时落到离光标最近的槽位。只认严格落在矩形里时，拖到缝隙上会弹回，看起来像卡住。</summary>
    private int NearestDesktopSlot(int x, int y)
    {
        int best = -1;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < _desktopSlots.Count; i++)
        {
            RectInt32 slot = _desktopSlots[i];
            long dx = x - (slot.X + slot.Width / 2);
            long dy = y - (slot.Y + slot.Height / 2);
            long distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    private void ArrangeDesktopTilesIfWorkAreaChanged()
    {
        if (!TryGetDesktopWorkArea(out RectInt32 area, out uint dpi)) return;
        if (area.X == _desktopWorkX && area.Y == _desktopWorkY && area.Width == _desktopWorkWidth && area.Height == _desktopWorkHeight && dpi == _desktopDpi)
            return;
        ArrangeDesktopTiles();
    }

    private void ArrangeDesktopTiles()
    {
        if (_desktopClosed || !_settings.DesktopTilesEnabled || (_desktopSubjects.Count == 0 && _desktopCommands is null)) return;
        if (!TryGetDesktopWorkArea(out RectInt32 area, out uint dpi)) return;
        _desktopWorkX = area.X;
        _desktopWorkY = area.Y;
        _desktopWorkWidth = area.Width;
        _desktopWorkHeight = area.Height;
        _desktopDpi = dpi;
        // dpi 为 0 时按 96 处理，避免窗口被排到坐标原点。
        _desktopScale = (dpi == 0 ? 96u : dpi) / 96d;

        DesktopWorkArea work = new(area.X / _desktopScale, area.Y / _desktopScale, area.Width / _desktopScale, area.Height / _desktopScale);
        double zoom = DesktopZoom;
        _desktopSlots.Clear();
        if (_settings.DesktopTilesAutoArrange)
        {
            List<(double Width, double Height)> sizes = _desktopSubjects.Select(subject => (subject.TileWidth * zoom, subject.TileHeight * zoom)).ToList();
            IReadOnlyList<DesktopTileSlot> slots = DesktopTileLayout.Arrange(sizes, work, DesktopLayoutGap);
            for (int i = 0; i < slots.Count && i < _desktopSubjects.Count; i++)
            {
                DesktopTileSlot slot = slots[i];
                RectInt32 pixels = ToPixels(slot);
                _desktopSlots.Add(pixels);
                PlaceSubjectWindow(_desktopSubjects[i], pixels, _desktopSubjects[i].TileWidth, _desktopSubjects[i].TileHeight);
                RememberArrangedSlot(_desktopSubjects[i], slot);
            }
        }
        else PlaceFreeTiles(work, zoom);
        PlaceDesktopCommands();
        StackDesktopCommand();
    }

    private void StackDesktopCommand()
    {
        if (_desktopCommands is null) return;
        DesktopWindowNative.StackCommandAboveTiles(
            _desktopCommands.Handles,
            _desktopWindows.Values.Select(window => window.Handle),
            DesktopEditActive);
    }

    private RectInt32 ToPixels(DesktopTileSlot slot) => new(
        (int)Math.Round(slot.X * _desktopScale),
        (int)Math.Round(slot.Y * _desktopScale),
        Math.Max(1, (int)Math.Round(slot.Width * _desktopScale)),
        Math.Max(1, (int)Math.Round(slot.Height * _desktopScale)));

    private void PlaceSubjectWindow(SubjectBoard subject, RectInt32 pixels, double widthDip, double heightDip)
    {
        if (ReferenceEquals(subject, _desktopDragSubject) || ReferenceEquals(subject, _desktopResizeSubject)) return;
        if (!_desktopWindows.TryGetValue(subject, out DesktopTileWindow? window)) return;
        if (window.Tile is { } tile && (Math.Abs(tile.Width - widthDip) > 0.5 || Math.Abs(tile.Height - heightDip) > 0.5))
            tile.FitDisplaySize(widthDip, heightDip);
        window.Place(pixels.X, pixels.Y, pixels.Width, pixels.Height);
    }

    private void PlaceFreeTiles(DesktopWorkArea work, double zoom)
    {
        List<DesktopTilePlacement> list = Placements();
        List<DesktopTilePlacement> pool = list.ToList();
        List<(double X, double Y, double Width, double Height)> occupied = [];
        foreach (SubjectBoard subject in _desktopSubjects)
        {
            double width = subject.TileWidth * zoom;
            double height = subject.TileHeight * zoom;
            DesktopTilePlacement? saved = pool.FirstOrDefault(item => string.Equals(item.Name, subject.Name, StringComparison.Ordinal));
            if (saved is not null) pool.Remove(saved);
            else
            {
                (double x, double y) = NextFreeSpot(work, width, height, occupied, DesktopLayoutGap);
                saved = new DesktopTilePlacement { Name = subject.Name, X = x, Y = y };
                list.Add(saved);
                ScheduleSave();
            }
            occupied.Add((saved.X, saved.Y, width, height));
            RectInt32 pixels = new(
                (int)Math.Round((_desktopWorkX / _desktopScale + saved.X) * _desktopScale),
                (int)Math.Round((_desktopWorkY / _desktopScale + saved.Y) * _desktopScale),
                Math.Max(1, (int)Math.Round(width * _desktopScale)),
                Math.Max(1, (int)Math.Round(height * _desktopScale)));
            PlaceSubjectWindow(subject, pixels, subject.TileWidth, subject.TileHeight);
        }
    }

    private static (double X, double Y) NextFreeSpot(DesktopWorkArea work, double width, double height, List<(double X, double Y, double Width, double Height)> occupied, double gap)
    {
        double x = Math.Max(0, work.Width - width);
        double y = 0;
        gap = double.IsFinite(gap) ? Math.Max(0, gap) : 0;
        bool Overlaps(double left, double top) => occupied.Any(item =>
            left < item.X + item.Width + gap && left + width + gap > item.X &&
            top < item.Y + item.Height + gap && top + height + gap > item.Y);
        while (Overlaps(x, y) && y + height <= work.Height + 0.001)
            y += height + gap;
        if (y + height > work.Height + 0.001 && x > 0)
        {
            x = Math.Max(0, x - width - gap);
            y = 0;
        }
        return (x, y);
    }

    private List<DesktopTilePlacement> Placements()
    {
        _settings.DesktopTilePlacements ??= new();
        string key = CurrentProject!.Id.ToString();
        if (!_settings.DesktopTilePlacements.TryGetValue(key, out List<DesktopTilePlacement>? list) || list is null)
        {
            list = [];
            _settings.DesktopTilePlacements[key] = list;
        }
        return list;
    }

    private void RememberArrangedSlot(SubjectBoard subject, DesktopTileSlot slot)
    {
        if (_desktopScale <= 0 || CurrentProject is null) return;
        SaveRelativePosition(subject, slot.X - _desktopWorkX / _desktopScale, slot.Y - _desktopWorkY / _desktopScale);
    }

    private void SnapshotFreePlacements()
    {
        foreach (SubjectBoard subject in _desktopSubjects)
        {
            if (!_desktopWindows.TryGetValue(subject, out DesktopTileWindow? window)) continue;
            if (!DesktopWindowNative.ScreenOrigin(window.Handle, out int x, out int y)) continue;
            RectInt32 size = window.PixelBounds;
            SaveFreePosition(subject, new RectInt32(x, y, Math.Max(1, size.Width), Math.Max(1, size.Height)));
        }
    }

    private void SaveRelativePosition(SubjectBoard subject, double x, double y)
    {
        if (CurrentProject is null || !double.IsFinite(x) || !double.IsFinite(y)) return;
        List<DesktopTilePlacement> list = Placements();
        DesktopTilePlacement? item = list.FirstOrDefault(placement => string.Equals(placement.Name, subject.Name, StringComparison.Ordinal));
        if (item is null)
        {
            item = new DesktopTilePlacement { Name = subject.Name };
            list.Add(item);
        }
        item.X = x;
        item.Y = y;
    }

    private void SaveFreePosition(SubjectBoard subject, RectInt32 bounds)
    {
        if (_desktopScale <= 0) return;
        List<DesktopTilePlacement> list = Placements();
        DesktopTilePlacement? item = list.FirstOrDefault(placement => string.Equals(placement.Name, subject.Name, StringComparison.Ordinal));
        if (item is null)
        {
            item = new DesktopTilePlacement { Name = subject.Name };
            list.Add(item);
        }
        item.X = (bounds.X - _desktopWorkX) / _desktopScale;
        item.Y = (bounds.Y - _desktopWorkY) / _desktopScale;
    }

    private void PlaceDesktopCommands()
    {
        if (_desktopCommands is null || _desktopWorkWidth <= 0 || _desktopWorkHeight <= 0) return;
        int margin = Math.Max(1, (int)Math.Round(16 * _desktopScale));
        double maxDip = Math.Max(1, (_desktopWorkWidth - margin * 2) / Math.Max(_desktopScale, 0.01));
        _desktopCommands.FitWidth(maxDip);
        int right = _desktopWorkX + _desktopWorkWidth - margin;
        int bottom = _desktopWorkY + _desktopWorkHeight - margin;
        _desktopCommands.PlaceGroup(right, bottom, _desktopScale);
        if (_desktopCommandHidden) ApplyDesktopCommandHiddenPose(_desktopCommandAnimTimer.IsEnabled);
    }

    /// <summary>桌面控制窗沿用软件控制窗的自动隐藏：只在查看模式、空闲达到设定时间后隐藏。</summary>
    private void UpdateDesktopCommandAutoHide()
    {
        if (_desktopClosed || _desktopCommands is null || !_settings.DesktopTilesEnabled) return;
        if (_lastToolbarActivity > _desktopCommandActivity)
            _desktopCommandActivity = _lastToolbarActivity;
        if (DesktopWindowNative.GetCursorPos(out DesktopWindowNative.DesktopPoint cursor) &&
            (cursor.X != _desktopCommandCursorX || cursor.Y != _desktopCommandCursorY))
        {
            _desktopCommandCursorX = cursor.X;
            _desktopCommandCursorY = cursor.Y;
            _desktopCommandActivity = Environment.TickCount64;
        }
        if (DesktopWindowNative.IsMouseButtonDown())
            _desktopCommandActivity = Environment.TickCount64;
        bool hide = ToolbarAutoHidePolicy.ShouldHide(
            _settings.ToolbarAutoHide,
            !DesktopEditActive,
            interacting: false,
            (Environment.TickCount64 - _desktopCommandActivity) / 1000d,
            _settings.ToolbarAutoHideSeconds);
        SetDesktopCommandHidden(hide, animate: true);
    }

    private void SetDesktopCommandHidden(bool hidden, bool animate)
    {
        if (_desktopCommands is null || _desktopCommands.HomeBounds.Width < 1) return;
        if (_desktopCommandHidden == hidden) return;
        _desktopCommandHidden = hidden;
        _desktopCommands.SetClickThrough(hidden);
        ApplyDesktopCommandHiddenPose(animate);
    }

    /// <summary>按当前显隐目标重算位移。尺寸变化时隐藏中的控制窗要跟着新的外框重新飞出。</summary>
    private void ApplyDesktopCommandHiddenPose(bool animate)
    {
        if (_desktopCommands is null) return;
        int toX = 0, toY = 0;
        double toOpacity = 1;
        if (_desktopCommandHidden)
        {
            bool fly = _settings.ToolbarHideAnimation == "Fly";
            toOpacity = fly ? 1 : 0;
            if (fly && _desktopScale > 0)
            {
                RectInt32 home = _desktopCommands.HomeBounds;
                double x = (home.X - _desktopWorkX) / _desktopScale;
                double y = (home.Y - _desktopWorkY) / _desktopScale;
                (double offsetX, double offsetY) = ToolbarAutoHidePolicy.ExitOffset(
                    x, y, home.Width / _desktopScale, home.Height / _desktopScale,
                    _desktopWorkWidth / _desktopScale, _desktopWorkHeight / _desktopScale);
                toX = (int)Math.Round(offsetX * _desktopScale);
                toY = (int)Math.Round(offsetY * _desktopScale);
            }
        }
        (int fromX, int fromY, double fromOpacity) = _desktopCommands.Presentation;
        if (!animate)
        {
            _desktopCommandAnimTimer.Stop();
            _desktopCommands.SetPresentation(toX, toY, toOpacity);
            return;
        }
        _desktopCommandFromX = fromX;
        _desktopCommandFromY = fromY;
        _desktopCommandFromOpacity = fromOpacity;
        _desktopCommandToX = toX;
        _desktopCommandToY = toY;
        _desktopCommandToOpacity = toOpacity;
        _desktopCommandAnimStart = Environment.TickCount64;
        _desktopCommandAnimTimer.Start();
    }

    private void AdvanceDesktopCommandAnimation()
    {
        if (_desktopCommands is null)
        {
            _desktopCommandAnimTimer.Stop();
            return;
        }
        double t = Math.Clamp((Environment.TickCount64 - _desktopCommandAnimStart) / 320d, 0, 1);
        double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        _desktopCommands.SetPresentation(
            (int)Math.Round(_desktopCommandFromX + (_desktopCommandToX - _desktopCommandFromX) * eased),
            (int)Math.Round(_desktopCommandFromY + (_desktopCommandToY - _desktopCommandFromY) * eased),
            _desktopCommandFromOpacity + (_desktopCommandToOpacity - _desktopCommandFromOpacity) * eased);
        if (t >= 1) _desktopCommandAnimTimer.Stop();
    }

    private bool TryGetDesktopWorkArea(out RectInt32 area, out uint dpi)
    {
        area = default;
        dpi = 96;
        if (_appWindow is null) return false;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        dpi = DesktopWindowNative.GetDpiForWindow(hwnd);
        area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        return area.Width > 0 && area.Height > 0;
    }

    private void CloseDesktopSurfaces()
    {
        foreach (SubjectBoard subject in _desktopKnownNames.Keys.ToList())
            subject.PropertyChanged -= DesktopSubject_PropertyChanged;
        _desktopKnownNames.Clear();
        foreach (DesktopTileWindow window in _desktopWindows.Values) window.CloseSurface();
        _desktopWindows.Clear();
        _desktopCommandAnimTimer.Stop();
        _desktopCommands?.CloseSurface();
        _desktopCommands = null;
        _desktopSubjects.Clear();
        _desktopSlots.Clear();
        _desktopDragSubject = null;
        _desktopResizeSubject = null;
    }

    private void BeginDesktopResize(SubjectBoard subject, DesktopResizeEdge edge)
    {
        if (!_desktopWindows.TryGetValue(subject, out DesktopTileWindow? window)) return;
        if (!DesktopWindowNative.GetCursorPos(out DesktopWindowNative.DesktopPoint cursor)) return;
        RectInt32 bounds = window.PixelBounds;
        if (bounds.Width < 1 || bounds.Height < 1) return;
        _desktopResizeSubject = subject;
        _desktopResizeEdge = edge;
        if (!DesktopWindowNative.ScreenOrigin(window.Handle, out _resizeOriginX, out _resizeOriginY))
        {
            _resizeOriginX = bounds.X;
            _resizeOriginY = bounds.Y;
        }
        _resizeOriginWidth = bounds.Width;
        _resizeOriginHeight = bounds.Height;
        _resizeCursorX = cursor.X;
        _resizeCursorY = cursor.Y;
        window.ResizeTo(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private void FollowDesktopResize()
    {
        if (_desktopResizeSubject is not { } subject || !_desktopWindows.TryGetValue(subject, out DesktopTileWindow? window)) return;
        if (!DesktopWindowNative.GetCursorPos(out DesktopWindowNative.DesktopPoint cursor)) return;
        double zoom = DesktopZoom;
        int minWidth = Math.Max(1, (int)Math.Round(SubjectTileControl.MinimumTileWidth * zoom * _desktopScale));
        int minHeight = Math.Max(1, (int)Math.Round(SubjectTileControl.MinimumTileHeight * zoom * _desktopScale));
        int maxWidth = Math.Max(minWidth, (int)Math.Round(SubjectTileControl.MaximumTileWidth * zoom * _desktopScale));
        int maxHeight = Math.Max(minHeight, (int)Math.Round(SubjectTileControl.MaximumTileHeight * zoom * _desktopScale));
        int dx = cursor.X - _resizeCursorX;
        int dy = cursor.Y - _resizeCursorY;
        int x = _resizeOriginX;
        int y = _resizeOriginY;
        int width = _resizeOriginWidth;
        int height = _resizeOriginHeight;
        if (_desktopResizeEdge is DesktopResizeEdge.Left or DesktopResizeEdge.BottomLeft)
        {
            width = Math.Clamp(_resizeOriginWidth - dx, minWidth, maxWidth);
            x = _resizeOriginX + _resizeOriginWidth - width;
        }
        else if (_desktopResizeEdge is DesktopResizeEdge.Right or DesktopResizeEdge.BottomRight)
            width = Math.Clamp(_resizeOriginWidth + dx, minWidth, maxWidth);
        if (_desktopResizeEdge is DesktopResizeEdge.Bottom or DesktopResizeEdge.BottomLeft or DesktopResizeEdge.BottomRight)
            height = Math.Clamp(_resizeOriginHeight + dy, minHeight, maxHeight);
        window.ResizeTo(x, y, width, height);
        window.Tile?.FitDisplaySize(width / _desktopScale / zoom, height / _desktopScale / zoom);
    }

    private void FinishDesktopResize()
    {
        if (_desktopResizeSubject is not { } subject || !_desktopWindows.TryGetValue(subject, out DesktopTileWindow? window))
        {
            _desktopResizeSubject = null;
            return;
        }
        window.EndResize();
        RectInt32 bounds = window.PixelBounds;
        double scale = _desktopScale > 0 ? _desktopScale : 1;
        double zoom = DesktopZoom;
        subject.TileWidth = Math.Clamp(bounds.Width / scale / zoom, SubjectTileControl.MinimumTileWidth, SubjectTileControl.MaximumTileWidth);
        subject.TileHeight = Math.Clamp(bounds.Height / scale / zoom, SubjectTileControl.MinimumTileHeight, SubjectTileControl.MaximumTileHeight);
        window.Tile?.FitDisplaySize(subject.TileWidth, subject.TileHeight);
        _desktopResizeSubject = null;
        if (!_settings.DesktopTilesAutoArrange) SaveFreePosition(subject, bounds);
        ScheduleSave();
        if (_settings.DesktopTilesAutoArrange) ArrangeDesktopTiles();
    }

    /// <summary>科目没有稳定 Id，桌面窗口按对象身份对应，避免同名科目共用一块窗口。</summary>
    private sealed class SubjectIdentity : IEqualityComparer<SubjectBoard>
    {
        public static readonly SubjectIdentity Instance = new();
        public bool Equals(SubjectBoard? x, SubjectBoard? y) => ReferenceEquals(x, y);
        public int GetHashCode(SubjectBoard obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private void AddSubjectCore()
    {
        if (CurrentProject is null) return;
        SubjectBoard subject = ViewModel.AddSubject("新科目");
        LayoutCommitted(subject);
        AddTile(subject);
        UpdateBoardBounds();
        UpdateSubjectCount();
    }
}
