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
    private DesktopInsertionLineWindow? _desktopInsertLine;
    private int _desktopInsertIndex = -1;
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
    private bool _desktopTilesHidden;
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

    private bool DesktopTilesVisible => _settings.DesktopTilesEnabled && !_desktopTilesHidden;

    /// <summary>右下角控制窗点了编辑，并且编辑锁已经通过。</summary>
    private bool DesktopEditActive =>
        _desktopEditing && DesktopTilesVisible && CurrentProject is not null && !NeedsEditAuth() && !NeedsCloudEditLock();

    private void ToggleDesktopEdit()
    {
        if (_desktopEditing) EndDesktopEdit();
        else BeginDesktopEdit();
    }

    private void BeginDesktopEdit(Action? then = null)
    {
        if (_desktopClosed || _initialView == "verification" || _desktopTilesHidden) return;
        void Enable()
        {
            if (_desktopClosed || _initialView == "verification" || _desktopTilesHidden ||
                !_settings.DesktopTilesEnabled || CurrentProject is null)
            {
                ReleaseUnusedEditLock();
                return;
            }
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
                else ReleaseUnusedEditLock();
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
                if (_desktopClosed || _initialView == "verification" || _desktopTilesHidden ||
                    !_settings.DesktopTilesEnabled)
                {
                    if (result.Acquired) _ = CloudSync.ReleaseEditLockAsync(_settings.CloudSync, project.Id);
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

    private void ToggleDesktopTilesVisibility() => SetDesktopTilesHidden(!_desktopTilesHidden);

    private void SetDesktopTilesHidden(bool hidden)
    {
        if (_desktopClosed || _desktopCommands is null || !_settings.DesktopTilesEnabled || _desktopTilesHidden == hidden) return;
        _desktopTilesHidden = hidden;
        if (hidden)
        {
            if (_desktopEditing) EndDesktopEdit();
            CloseDesktopTileWindows();
        }
        _desktopCommands.SetTilesHidden(hidden);
        _desktopCommandActivity = Environment.TickCount64;
        if (!hidden) SyncDesktopTiles();
        PlaceDesktopCommands();
        StackDesktopCommand();
        UpdateDesktopCommandAutoHide();
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
        if (_desktopTilesHidden)
        {
            if (_desktopEditing) EndDesktopEdit();
            CloseDesktopTileWindows();
            _desktopCommands ??= CreateDesktopCommands();
            _desktopCommands.ApplyTheme(RootShell.RequestedTheme);
            _desktopCommands.SetEditing(false);
            _desktopCommands.SetTilesHidden(true);
            ApplyDesktopCommandChrome();
            ArrangeDesktopTiles();
            return;
        }

        List<SubjectBoard> ordered = OrderDesktopSubjects();
        HashSet<SubjectBoard> keep = new(ordered, SubjectIdentity.Instance);
        foreach (SubjectBoard stale in _desktopWindows.Keys.Where(subject => !keep.Contains(subject)).ToList())
        {
            stale.PropertyChanged -= DesktopSubject_PropertyChanged;
            _desktopKnownNames.Remove(stale);
            _desktopWindows[stale].Tile?.Detach();
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
            window.ApplyBackgroundSettings(_settings.EffectiveDesktopTileLook().Background);
            ApplyDesktopCorner(window);
            window.SetEditing(editing);
            window.TitleText = subject.Name;
        }
        _desktopCommands ??= CreateDesktopCommands();
        _desktopCommands.ApplyTheme(theme);
        _desktopCommands.SetEditing(editing);
        _desktopCommands.SetTilesHidden(false);
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
        if (_desktopTilesHidden || !_desktopWindows.TryGetValue(subject, out DesktopTileWindow? current) || !ReferenceEquals(current, window))
        {
            window.CloseSurface();
            return;
        }
        if (window.Tile is not null) return;
        AutofillPopup popup = new();
        SubjectTileControl? tile = null;
        tile = new SubjectTileControl(
            subject,
            _ => ConfirmDesktopDelete(window, subject),
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
        tile.LookSource = () => _settings.EffectiveDesktopTileLook();
        tile.ApplyAppearance(_settings);
        tile.DesktopDragStarted += () =>
        {
            _desktopDragSubject = subject;
            _desktopInsertIndex = -1;
            if (_desktopWindows.TryGetValue(subject, out DesktopTileWindow? dragging)) dragging.BeginExternalDrag();
            UpdateDesktopInsertionLine();
        };
        tile.DesktopDragDelta += (_, _) =>
        {
            if (_desktopWindows.TryGetValue(subject, out DesktopTileWindow? dragging)) dragging.FollowCursor();
            UpdateDesktopInsertionLine();
        };
        tile.DesktopDragCompleted += () =>
        {
            UpdateDesktopInsertionLine();
            if (_desktopWindows.TryGetValue(subject, out DesktopTileWindow? dragging)) dragging.EndExternalDrag();
            SubjectBoard? dragged = _desktopDragSubject;
            int insertAt = _desktopInsertIndex;
            _desktopDragSubject = null;
            _desktopInsertIndex = -1;
            HideDesktopInsertionLine();
            if (dragged is not null) FinishDesktopDrag(dragged, insertAt);
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
        window.ApplyBackgroundSettings(_settings.EffectiveDesktopTileLook().Background);
        ApplyDesktopCorner(window);
        window.SetEditing(DesktopEditActive);
        ArrangeDesktopTiles();
    }

    /// <summary>在这块磁贴窗口上确认。不要把确认框开到主窗口，否则会把已收起的主界面拉回来。</summary>
    private void ConfirmDesktopDelete(DesktopTileWindow window, SubjectBoard subject)
    {
        window.Tile?.HideAutofill();
        window.ConfirmRemove(DeleteSubjectMessage(subject), () => RemoveSubject(subject));
    }

    private DesktopCommandWindow CreateDesktopCommands()
    {
        DesktopCommandWindow window = new();
        window.ApplyTheme(RootShell.RequestedTheme);
        window.EditInvoked += ToggleDesktopEdit;
        window.AddInvoked += AddDesktopSubject;
        window.AboveTiles = true;
        window.AutoArrangeInvoked += SetDesktopAutoArrange;
        window.TilesVisibilityInvoked += ToggleDesktopTilesVisibility;
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
        string key = CurrentProject!.Id.ToString();
        var current = ViewModel.Subjects.Select(subject => (subject.Id, subject.Name)).ToList();
        _settings.DesktopTileOrderIds ??= new();
        _settings.DesktopTileOrderIds.TryGetValue(key, out List<Guid>? savedIds);
        _settings.DesktopTileOrder ??= new();
        _settings.DesktopTileOrder.TryGetValue(key, out List<string>? savedNames);
        List<Guid> mergedIds = DesktopTileLayout.MergeOrderIds(savedIds, savedNames, current);
        if (savedIds is null || !savedIds.SequenceEqual(mergedIds))
        {
            _settings.DesktopTileOrderIds[key] = mergedIds;
            ScheduleSave();
        }
        List<SubjectBoard> remaining = ViewModel.Subjects.ToList();
        List<SubjectBoard> ordered = new(remaining.Count);
        foreach (Guid id in mergedIds)
        {
            int index = remaining.FindIndex(subject => subject.Id == id);
            if (index < 0) continue;
            ordered.Add(remaining[index]);
            remaining.RemoveAt(index);
        }
        ordered.AddRange(remaining);
        // 同步旧的名字顺序，兼容仍按名读取的旧客户端。
        List<string> names = ordered.Select(subject => subject.Name).ToList();
        if (savedNames is null || !savedNames.SequenceEqual(names))
        {
            _settings.DesktopTileOrder[key] = names;
            ScheduleSave();
        }
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
        DesktopCommandLook look = _settings.EffectiveDesktopCommandLook();
        double scale = Math.Clamp(look.Scale, .6, 2);
        double border = Math.Clamp(look.BorderThickness, 0, 20);
        double padding = 7 * scale;
        double button = 40 * scale;
        double outer = button + padding * 2 + border * 2;
        double pixelRadius = ToolbarChrome.CornerRadius(look.Radius, outer, outer);
        double radius = MatchToolbarButtonRadius(pixelRadius, padding, button, button).TopLeft;
        // 不用主界面控制窗的画笔：毛玻璃画笔会被原地更新，桌面自定义不能改到主界面。
        _desktopCommands.ApplyToolbarChrome(
            SurfaceBackground.Create(look.BackgroundColor, look.BackgroundOpacity, look.BackgroundColorCleared, look.Glass, look.Blur),
            CreateToolbarBorder(look.BorderColor),
            border,
            pixelRadius,
            radius,
            button,
            16 * scale,
            look.Glass,
            look.BackgroundColor,
            look.BackgroundOpacity,
            look.BackgroundColorCleared,
            look.Blur);
    }

    private void ApplyDesktopTileBackdrops()
    {
        BackgroundSettings background = _settings.EffectiveDesktopTileLook().Background;
        foreach (DesktopTileWindow window in _desktopWindows.Values)
        {
            window.ApplyBackgroundSettings(background);
            ApplyDesktopCorner(window);
        }
    }

    private void ApplyDesktopCorner(DesktopTileWindow window)
    {
        double zoom = DesktopZoom;
        double corner = _settings.DesktopTileLookSynced ? _settings.TileCornerRadius : _settings.DesktopTileLook.CornerRadius;
        int radius = (int)Math.Round(Math.Clamp(corner, 0, 40) * zoom * Math.Max(1, _desktopScale));
        window.SetRegionRadius(radius);
    }

    private void SetDesktopZoom(double zoom)
    {
        _settings.DesktopTilesZoom = Math.Clamp(zoom, 0.5, 2);
        _desktopCommands?.SetZoom(_settings.DesktopTilesZoom);
        ScheduleSave();
        ArrangeDesktopTiles();
    }

    private void FinishDesktopDrag(SubjectBoard subject, int insertAt)
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
        int from = _desktopSubjects.FindIndex(item => ReferenceEquals(item, subject));
        if (from < 0 || insertAt < 0 || insertAt == from)
        {
            ArrangeDesktopTiles();
            return;
        }
        List<SubjectBoard> ordered = _desktopSubjects.ToList();
        ordered.RemoveAt(from);
        ordered.Insert(Math.Clamp(insertAt, 0, ordered.Count), subject);
        _desktopSubjects.Clear();
        _desktopSubjects.AddRange(ordered);
        _settings.DesktopTileOrderIds ??= new();
        _settings.DesktopTileOrderIds[CurrentProject.Id.ToString()] = ordered.Select(item => item.Id).ToList();
        _settings.DesktopTileOrder ??= new();
        _settings.DesktopTileOrder[CurrentProject.Id.ToString()] = ordered.Select(item => item.Name).ToList();
        ScheduleSave();
        ArrangeDesktopTiles();
        SyncDesktopTiles();
    }

    /// <summary>自动排列时按正在拖的窗口盖住哪一块槽来更新插入线和插入下标。关掉自动排列就只跟着光标走。</summary>
    private void UpdateDesktopInsertionLine()
    {
        if (!_settings.DesktopTilesAutoArrange || _desktopDragSubject is null || _desktopSubjects.Count < 2 || _desktopScale <= 0)
        {
            _desktopInsertIndex = -1;
            HideDesktopInsertionLine();
            return;
        }
        int from = _desktopSubjects.FindIndex(item => ReferenceEquals(item, _desktopDragSubject));
        double scale = _desktopScale;
        double zoom = DesktopZoom;
        List<DesktopTileSlot> slots = new(_desktopSubjects.Count);
        foreach (SubjectBoard item in _desktopSubjects)
            slots.Add(new DesktopTileSlot(0, 0, Math.Max(1, item.TileWidth * zoom), Math.Max(1, item.TileHeight * zoom)));
        DesktopWorkArea work = new(_desktopWorkX / scale, _desktopWorkY / scale, _desktopWorkWidth / scale, _desktopWorkHeight / scale);
        DesktopInsertionGuide? guide = null;
        bool hasCursor = DesktopWindowNative.GetCursorPos(out DesktopWindowNative.DesktopPoint point);
        if (from >= 0 && _desktopWindows.TryGetValue(_desktopDragSubject, out DesktopTileWindow? dragged))
        {
            RectInt32 bounds = dragged.PixelBounds;
            if (bounds.Width > 0 && bounds.Height > 0)
                guide = DesktopTileLayout.InsertionGuideForTile(
                    slots, from, bounds.X / scale, bounds.Y / scale, bounds.Width / scale, bounds.Height / scale, work, DesktopLayoutGap,
                    hasCursor ? point.X / scale : double.NaN, hasCursor ? point.Y / scale : double.NaN);
        }
        if (guide is null && hasCursor)
            guide = DesktopTileLayout.InsertionGuide(slots, from, point.X / scale, point.Y / scale, work, DesktopLayoutGap);
        if (guide is not { } line)
        {
            _desktopInsertIndex = -1;
            HideDesktopInsertionLine();
            return;
        }
        _desktopInsertIndex = line.Index;
        ShowDesktopInsertionLine(line, scale);
    }

    private void ShowDesktopInsertionLine(DesktopInsertionGuide guide, double scale)
    {
        int x1 = (int)Math.Round(guide.X1 * scale);
        int y1 = (int)Math.Round(guide.Y1 * scale);
        int x2 = (int)Math.Round(guide.X2 * scale);
        int y2 = (int)Math.Round(guide.Y2 * scale);
        bool vertical = Math.Abs(x2 - x1) <= Math.Abs(y2 - y1);
        int cross = Math.Max(4, (int)Math.Round(8 * scale));
        int left;
        int top;
        int width;
        int height;
        if (vertical)
        {
            int length = Math.Max(1, Math.Abs(y2 - y1));
            left = (x1 + x2) / 2 - cross / 2;
            top = Math.Min(y1, y2);
            width = cross;
            height = length;
        }
        else
        {
            int length = Math.Max(1, Math.Abs(x2 - x1));
            left = Math.Min(x1, x2);
            top = (y1 + y2) / 2 - cross / 2;
            width = length;
            height = cross;
        }
        bool wasShowing = _desktopInsertLine?.IsShowing == true;
        _desktopInsertLine ??= new DesktopInsertionLineWindow();
        _desktopInsertLine.ShowGuide(left, top, width, height, vertical);
        if (!wasShowing) StackDesktopCommand();
    }

    private void HideDesktopInsertionLine() => _desktopInsertLine?.HideGuide();

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
        _desktopSlots.Clear();
        if (_desktopTilesHidden)
        {
            PlaceDesktopCommands();
            StackDesktopCommand();
            return;
        }

        DesktopWorkArea work = new(area.X / _desktopScale, area.Y / _desktopScale, area.Width / _desktopScale, area.Height / _desktopScale);
        double zoom = DesktopZoom;
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
        List<nint> tiles = [];
        foreach (DesktopTileWindow window in _desktopWindows.Values)
            if (window.Handle != 0) tiles.Add(window.Handle);
        if (_desktopInsertLine is { IsShowing: true, Handle: var line } && line != 0)
            tiles.Add(line);
        // 主窗口还在时先挂回桌面再沉底，避免编辑态的无主窗口被这次重排抬到主界面前面。
        // 已经最小化或为显示桌面收起时不挂回去，否则壁纸会盖住正在桌面上编辑的磁贴。
        bool cover = AppCoversDesktop();
        if (cover) DesktopWidgetHost.HoldAnchoredOwners();
        DesktopWindowNative.StackCommandAboveTiles(_desktopCommands.Handles, tiles, DesktopEditActive);
        if (!cover && DesktopEditActive)
            DesktopWidgetHost.RaiseVisibleHostsAboveDesktop();
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
        ApplyDesktopCorner(window);
    }

    private void PlaceFreeTiles(DesktopWorkArea work, double zoom)
    {
        List<DesktopTilePlacement> list = Placements();
        List<DesktopTilePlacement> pool = list.ToList();
        List<(double X, double Y, double Width, double Height)> occupied = [];
        bool OnAnyDisplay(RectInt32 rect)
        {
            Windows.Graphics.PointInt32[] corners =
            [
                new(rect.X, rect.Y),
                new(rect.X + rect.Width - 1, rect.Y),
                new(rect.X, rect.Y + rect.Height - 1),
                new(rect.X + rect.Width - 1, rect.Y + rect.Height - 1)
            ];
            return corners.Any(point => Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
                point, Microsoft.UI.Windowing.DisplayAreaFallback.None) is not null);
        }
        foreach (SubjectBoard subject in _desktopSubjects)
        {
            double width = subject.TileWidth * zoom;
            double height = subject.TileHeight * zoom;
            DesktopTilePlacement? saved = FindPlacement(pool, subject);
            if (saved is not null) { pool.Remove(saved); saved.Id = subject.Id; }
            else
            {
                (double x, double y) = NextFreeSpot(work, width, height, occupied, DesktopLayoutGap);
                saved = new DesktopTilePlacement { Id = subject.Id, Name = subject.Name, X = x, Y = y };
                list.Add(saved);
                ScheduleSave();
            }
            RectInt32 pixels = new(
                (int)Math.Round((_desktopWorkX / _desktopScale + saved.X) * _desktopScale),
                (int)Math.Round((_desktopWorkY / _desktopScale + saved.Y) * _desktopScale),
                Math.Max(1, (int)Math.Round(width * _desktopScale)),
                Math.Max(1, (int)Math.Round(height * _desktopScale)));
            // 分辨率变小或副屏拔掉后，旧坐标可能整块落在所有显示器之外；移到当前工作区的空位。
            if (!OnAnyDisplay(pixels))
            {
                (double x, double y) = NextFreeSpot(work, width, height, occupied, DesktopLayoutGap);
                saved.X = x;
                saved.Y = y;
                pixels = new RectInt32(
                    (int)Math.Round((_desktopWorkX / _desktopScale + x) * _desktopScale),
                    (int)Math.Round((_desktopWorkY / _desktopScale + y) * _desktopScale),
                    Math.Max(1, (int)Math.Round(width * _desktopScale)),
                    Math.Max(1, (int)Math.Round(height * _desktopScale)));
                ScheduleSave();
            }
            occupied.Add((saved.X, saved.Y, width, height));
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
        while (y + height > work.Height + 0.001 && x > 0)
        {
            x = Math.Max(0, x - width - gap);
            y = 0;
            while (Overlaps(x, y) && y + height <= work.Height + 0.001)
                y += height + gap;
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

    /// <summary>按稳定 Id 找位置记录；旧数据 Id 为空时按名兜底，命中后由调用方回填 Id。</summary>
    private static DesktopTilePlacement? FindPlacement(List<DesktopTilePlacement> list, SubjectBoard subject) =>
        list.FirstOrDefault(placement => placement.Id == subject.Id)
        ?? list.FirstOrDefault(placement => placement.Id == Guid.Empty && string.Equals(placement.Name, subject.Name, StringComparison.Ordinal));

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
        DesktopTilePlacement? item = FindPlacement(list, subject);
        if (item is null)
        {
            item = new DesktopTilePlacement { Id = subject.Id, Name = subject.Name };
            list.Add(item);
        }
        item.Id = subject.Id;
        item.X = x;
        item.Y = y;
    }

    private void SaveFreePosition(SubjectBoard subject, RectInt32 bounds)
    {
        if (_desktopScale <= 0) return;
        List<DesktopTilePlacement> list = Placements();
        DesktopTilePlacement? item = FindPlacement(list, subject);
        if (item is null)
        {
            item = new DesktopTilePlacement { Id = subject.Id, Name = subject.Name };
            list.Add(item);
        }
        item.Id = subject.Id;
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

    private void CloseDesktopTileWindows()
    {
        foreach (SubjectBoard subject in _desktopKnownNames.Keys.ToList())
            subject.PropertyChanged -= DesktopSubject_PropertyChanged;
        _desktopKnownNames.Clear();
        foreach (DesktopTileWindow window in _desktopWindows.Values) { window.Tile?.Detach(); window.CloseSurface(); }
        _desktopWindows.Clear();
        _desktopInsertLine?.CloseSurface();
        _desktopInsertLine = null;
        _desktopInsertIndex = -1;
        _desktopSubjects.Clear();
        _desktopSlots.Clear();
        _desktopDragSubject = null;
        _desktopResizeSubject = null;
    }

    private void CloseDesktopSurfaces()
    {
        CloseDesktopTileWindows();
        _desktopCommandAnimTimer.Stop();
        _desktopTilesHidden = false;
        _desktopCommands?.SetTilesHidden(false);
        _desktopCommands?.CloseSurface();
        _desktopCommands = null;
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
        ApplyDesktopCorner(window);
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
