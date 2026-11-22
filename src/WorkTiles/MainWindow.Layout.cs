using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WorkTiles.Controls;
using WorkTiles.Models;
using WorkTiles.Services;
using WorkTiles.Shared;

namespace WorkTiles;

public sealed partial class MainWindow
{
    // ------------------------------------------------------------------ 屏幕上报（集控）

    private readonly DispatcherTimer _screenReportDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>窗口、布局、缩放或当前项目变化后上报屏幕与分区，网页据此按真实尺寸画出整屏；合并 0.5 秒内的连续变化。</summary>
    private void ScheduleScreenReport()
    {
        if (!_settings.CloudSync.Enabled) return;
        _screenReportDebounce.Stop();
        _screenReportDebounce.Start();
    }

    private async Task ReportScreenAsync()
    {
        _screenReportDebounce.Stop();
        if (CurrentScreen() is { } report) await CloudSync.ReportScreenAsync(_settings.CloudSync, report);
    }

    /// <summary>
    /// 当前屏幕与分区：整屏取显示区，时钟区与作业区按实际位置换算到显示区坐标（作业区左上角就是磁贴坐标原点）。
    /// 设置页、集控页打开时显示区不可见，不上报。
    /// </summary>
    private DeviceScreenReport? CurrentScreen()
    {
        if (DisplayRoot.Visibility != Visibility.Visible || DisplayRoot.XamlRoot is not { } root || DisplayRoot.ActualWidth < 1 || DisplayRoot.ActualHeight < 1) return null;
        ScreenRegion? Region(FrameworkElement element)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth < 1 || element.ActualHeight < 1) return null;
            Windows.Foundation.Point origin = element.TransformToVisual(DisplayRoot).TransformPoint(new Windows.Foundation.Point(0, 0));
            return new ScreenRegion(Math.Round(origin.X, 1), Math.Round(origin.Y, 1), Math.Round(element.ActualWidth, 1), Math.Round(element.ActualHeight, 1));
        }
        bool free = _settings.LayoutMode == "Free";
        return new DeviceScreenReport(Math.Round(DisplayRoot.ActualWidth, 1), Math.Round(DisplayRoot.ActualHeight, 1), root.RasterizationScale,
            _settings.LayoutMode, Math.Round(_settings.SplitRatio, 4), RootShell.ActualWidth < 900,
            free ? null : Region(ClockPanel), Region(BoardScroller), _library.ActiveProjectId);
    }

    private const double DockedClockFallbackAspectRatio = 1.8;
    private const double DockedComponentsFallbackAspectRatio = 6.5;
    private readonly Dictionary<string, Viewbox> _freeWidgets = [];
    private Thumb? _splitter;
    private bool _layingOut;
    private bool _splitDragging;
    private double _splitDragRatio;
    private bool _splitDragFramePending;
    private Dictionary<string, RegionPlacement>? _widgetEditSnapshot;
    private HashSet<string>? _dockedCustomizationEditSnapshot;
    private bool _boardLayoutPending;

    /// <summary>分屏方向只由顶栏按钮决定，窗口尺寸不再参与判断；无法识别的旧值按左右分屏处理。</summary>
    private bool SplitIsVertical => string.Equals(_settings.SplitOrientation, "Vertical", StringComparison.Ordinal);
    /// <summary>默认时钟在前（左/上），对调后作业在前。</summary>
    private bool ClockIsFirstSplitRegion => !_settings.SplitSwapped;

    /// <summary>
    /// 网格大小变化时把已有磁贴重新吸附到新网格：位置和尺寸始终落在网格线上，不会停在旧网格。
    /// 设置页打开时看板是折叠的，这里只改数据；回到看板时再统一应用，拖动滑块因此不会卡顿。
    /// </summary>
    private void SnapTilesToGrid()
    {
        if (!IsGridSnappingEnabled) return;
        double grid = GridSize;
        double width = Math.Floor(BoardCanvas.Width / grid) * grid;
        double height = Math.Floor(BoardCanvas.Height / grid) * grid;
        double minWidth = Math.Ceiling(280 / grid) * grid;
        // 画板还放不下一个最小磁贴时保持原样，等画板尺寸可用后再吸附。
        if (width < minWidth || height < SubjectTileControl.MinimumTileHeight) return;
        foreach (SubjectBoard subject in ViewModel.Subjects)
        {
            subject.TileWidth = Math.Clamp(SnapToGrid(subject.TileWidth), minWidth, width);
            subject.TileHeight = Math.Clamp(SnapToGrid(subject.TileHeight), SubjectTileControl.MinimumTileHeight, height);
            subject.X = Math.Clamp(SnapToGrid(subject.X), 0, width - subject.TileWidth);
            subject.Y = Math.Clamp(SnapToGrid(subject.Y), 0, height - subject.TileHeight);
        }
        _boardLayoutPending = true;
    }

    /// <summary>设置页里改过网格大小后，回到看板时一次性应用坐标、尺寸与网格重画。</summary>
    private void ApplyPendingBoardLayout()
    {
        if (!_boardLayoutPending) return;
        _boardLayoutPending = false;
        _renderedGridWidth = 0;
        foreach (SubjectBoard subject in ViewModel.Subjects)
        {
            if (FindTile(subject) is not { } tile) continue;
            tile.ApplyModelLayout();
            Canvas.SetLeft(tile, subject.X);
            Canvas.SetTop(tile, subject.Y);
        }
        UpdateBoardBounds();
        SyncDesktopTiles();
    }

    private void ApplyDisplayLayout()
    {
        if (DisplayGrid is null || _layingOut) return;
        _layingOut = true;
        try
        {
            bool free = _settings.LayoutMode == "Free", split = _settings.LayoutMode == "Split";
            bool compact = RootShell.ActualWidth < 900;
            bool vertical = split && SplitIsVertical;
            bool clockFirst = ClockIsFirstSplitRegion;
            DisplayGrid.ColumnDefinitions[0].MinWidth = 0;
            DisplayGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            DisplayGrid.ColumnDefinitions[1].Width = new GridLength(0);
            DisplayGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            DisplayGrid.RowDefinitions[1].Height = new GridLength(0);
            Grid.SetColumn(ClockPanel, 0); Grid.SetRow(ClockPanel, 0);
            Grid.SetColumn(BoardWorkspace, 0); Grid.SetRow(BoardWorkspace, 0);
            ClockPanel.Visibility = free || _settings.LayoutMode == "Board" ? Visibility.Collapsed : Visibility.Visible;
            BoardWorkspace.Visibility = _settings.LayoutMode == "Clock" || CurrentProject is null ? Visibility.Collapsed : Visibility.Visible;
            ClockPanel.BorderThickness = new Thickness(0);
            MainTimeText.FontSize = compact ? 72 : 112;
            // 分屏方向与前后顺序由顶部菜单栏控制，不再随窗口宽窄自动切换。
            if (SplitLayoutCommands is not null)
                SplitLayoutCommands.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            if (split)
            {
                double ratio = Math.Clamp(_settings.SplitRatio, .05, .95);
                FrameworkElement first = clockFirst ? ClockPanel : BoardWorkspace;
                FrameworkElement second = clockFirst ? BoardWorkspace : ClockPanel;
                if (vertical)
                {
                    DisplayGrid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
                    DisplayGrid.RowDefinitions[1].Height = new GridLength(1 - ratio, GridUnitType.Star);
                    Grid.SetRow(first, 0); Grid.SetRow(second, 1);
                }
                else
                {
                    DisplayGrid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
                    DisplayGrid.ColumnDefinitions[1].Width = new GridLength(1 - ratio, GridUnitType.Star);
                    Grid.SetColumn(first, 0); Grid.SetColumn(second, 1);
                }
            }
            ApplyFreeWidgets(free);
            if (!free && _settings.LayoutMode != "Board") ApplyDockedClockLayout();
            UpdateLayoutHandles();
            // 仅时钟模式才显示和书写整屏笔迹，切换布局时同步显示状态。
            RefreshClockInk();
        }
        finally { _layingOut = false; }
        ScheduleScreenReport();
    }

    /// <summary>单按钮在左右分屏和上下分屏之间切换；窗口再窄也不会自动改回上下分屏。</summary>
    private void ToggleSplitOrientationButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.SplitOrientation = SplitIsVertical ? "Horizontal" : "Vertical";
        SettingChanged();
    }

    private void SwapSplitAreasButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.SplitSwapped = !_settings.SplitSwapped;
        SettingChanged();
    }

    /// <summary>同步顶栏方向按钮的图标和两个按钮的方向相关提示，不使用选中色。</summary>
    private void RefreshSplitLayoutCommands()
    {
        if (ToggleSplitOrientationButton is null || SwapSplitAreasButton is null || SplitOrientationIcon is null) return;
        bool vertical = SplitIsVertical;
        SplitOrientationIcon.Symbol = vertical ? "SplitVertical" : "SplitHorizontal";
        string horizontalOrder = _settings.SplitSwapped ? "作业在左、时钟在右" : "时钟在左、作业在右";
        string verticalOrder = _settings.SplitSwapped ? "作业在上、时钟在下" : "时钟在上、作业在下";
        string next = vertical ? "左右分屏" : "上下分屏";
        AutomationProperties.SetName(ToggleSplitOrientationButton, $"切换为{next}");
        ToolTipService.SetToolTip(ToggleSplitOrientationButton, $"当前{(vertical ? "上下分屏" : "左右分屏")}，点击切换为{next}");
        ToolTipService.SetToolTip(SwapSplitAreasButton, _settings.SplitSwapped
            ? "恢复默认顺序：时钟在前、作业在后"
            : $"对调时钟与作业区域（当前{(vertical ? verticalOrder : horizontalOrder)}）");
    }

    private void ApplyFreeWidgets(bool free)
    {
        // 将现有组件视图迁入统一容器，原服务和数据绑定继续复用。
        if (free && _freeWidgets.Count == 0)
        {
            ClockComponents.Children.Remove(WeatherWidget); ClockComponents.Children.Remove(NoiseWidget);
            ClockLayoutCanvas.Children.Remove(ClockContentView);
            foreach (var (key, element) in new (string, UIElement)[] { ("Clock", ClockContentView), ("Weather", WeatherWidget), ("Noise", NoiseWidget) })
            {
                Viewbox view = new() { Child = element, Stretch = Stretch.Uniform };
                _freeWidgets[key] = view; FreeWidgetsCanvas.Children.Add(view);
            }
        }
        else if (!free && _freeWidgets.Count != 0)
        {
            foreach (var view in _freeWidgets.Values) view.Child = null;
            FreeWidgetsCanvas.Children.Clear(); _freeWidgets.Clear();
            ClockLayoutCanvas.Children.Add(ClockContentView);
            ClockComponents.Children.Add(WeatherWidget); ClockComponents.Children.Add(NoiseWidget);
        }
        foreach (var (key, view) in _freeWidgets)
        {
            if (!_settings.Widgets.TryGetValue(key, out var placement))
            {
                double x = Math.Clamp(ViewModel.Subjects.Select(s => s.X + s.TileWidth + 24).DefaultIfEmpty(20).Max(), 0, Math.Max(0, DisplayRoot.ActualWidth - 420));
                _settings.Widgets[key] = placement = key switch
                {
                    "Clock" => new() { X = x, Y = 20, Width = 400, Height = 210 },
                    "Weather" => new() { X = x, Y = 250, Width = 190, Height = 64 },
                    "Noise" => new() { X = x + 210, Y = 250, Width = 190, Height = 64 },
                    _ => new() { X = x, Y = 340, Width = 240, Height = 100 }
                };
            }
            view.Width = Math.Max(80, placement.Width); view.Height = Math.Max(48, placement.Height);
            Canvas.SetLeft(view, Math.Max(0, placement.X)); Canvas.SetTop(view, Math.Max(0, placement.Y));
        }
    }

    private void ClockPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 班务条在分屏模式下按时钟区宽度排版。
        if (_isLoaded) RenderBoardBanner();
        if (!_isLoaded || _settings.LayoutMode is not ("Split" or "Clock")) return;
        ApplyDockedClockLayout();
        UpdateLayoutHandles();
    }

    private void ClockContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isLoaded || _layingOut || _settings.LayoutMode is not ("Split" or "Clock")) return;
        ApplyDockedClockLayout();
        UpdateLayoutHandles();
    }

    private double DockedComponentsAspectRatio
    {
        get
        {
            // 按天气和噪音卡片的真实尺寸定框，固定 6.5 会在框里留下一侧空白，内容就看起来不居中。
            double contentWidth = ClockComponentsView.Child?.DesiredSize.Width ?? 0;
            double contentHeight = ClockComponentsView.Child?.DesiredSize.Height ?? 0;
            return contentWidth > 1 && contentHeight > 1
                ? Math.Clamp(contentWidth / contentHeight, 2, 20)
                : DockedComponentsFallbackAspectRatio;
        }
    }
    private double DockedClockAspectRatio
    {
        get
        {
            double contentWidth = ClockContentView.Child?.DesiredSize.Width ?? 0;
            double contentHeight = ClockContentView.Child?.DesiredSize.Height ?? 0;
            return contentWidth > 1 && contentHeight > 1
                ? Math.Clamp(contentWidth / contentHeight, 1, 4)
                : DockedClockFallbackAspectRatio;
        }
    }

    private void ApplyDockedClockLayout()
    {
        double width = ClockPanel.ActualWidth, height = ClockPanel.ActualHeight;
        if (!_isLoaded || width < 1 || height < 1) return;

        bool splitMode = _settings.LayoutMode == "Split";
        string prefix = splitMode ? "Split" : "ClockMode";
        string clockKey = prefix + "Clock", componentsKey = prefix + "Components";
        double clockAspectRatio = DockedClockAspectRatio;
        if (!_settings.Widgets.TryGetValue(clockKey, out RegionPlacement? clock) || !_settings.CustomizedDockedWidgets.Contains(clockKey))
        {
            double clockWidth = Math.Min(splitMode ? 720 : 760, width * (splitMode ? .84 : .68));
            double clockHeight = clockWidth / clockAspectRatio;
            double componentsHeight = Math.Min(splitMode ? 520 : 560, width * .72) / DockedComponentsAspectRatio;
            double groupHeight = clockHeight + componentsHeight + 18;
            double groupTop = Math.Max(0, (height - groupHeight) / 2 - (splitMode ? height * .08 : 0));
            _settings.Widgets[clockKey] = clock = new RegionPlacement { Y = groupTop, Width = clockWidth, Height = clockHeight };
        }
        else if (Math.Abs(clock.Width / Math.Max(1, clock.Height) - clockAspectRatio) > .01)
        {
            // 旧版本用固定宽高比保存了多余的右侧空白；保留视觉高度，只收紧宽度并重新回到中轴线。
            clock.Width = clock.Height * clockAspectRatio;
        }
        if (!_settings.Widgets.TryGetValue(componentsKey, out RegionPlacement? components) || !_settings.CustomizedDockedWidgets.Contains(componentsKey))
        {
            double componentsWidth = Math.Min(splitMode ? 520 : 560, width * .72);
            double componentsHeight = componentsWidth / DockedComponentsAspectRatio;
            double componentsY = Math.Min(Math.Max(0, height - componentsHeight), clock.Y + clock.Height + 18);
            _settings.Widgets[componentsKey] = components = new RegionPlacement { Y = componentsY, Width = componentsWidth, Height = componentsHeight };
        }
        WidgetLayout.ResizeCentered(clock, 0, 0, clockAspectRatio, 220, width, height);
        WidgetLayout.ResizeCentered(components, 0, 0, DockedComponentsAspectRatio, 240, width, height);
        ApplyCenteredPlacement(ClockContentView, clock);
        ApplyCenteredPlacement(ClockComponentsView, components);
    }

    private static void ApplyCenteredPlacement(FrameworkElement element, RegionPlacement placement)
    {
        element.Width = placement.Width;
        element.Height = placement.Height;
        Canvas.SetLeft(element, placement.X);
        Canvas.SetTop(element, placement.Y);
    }

    private void UpdateLayoutHandles()
    {
        if (FreeLayoutHandles is null || _splitDragging) return;
        FreeLayoutHandles.Children.Clear();
        bool split = _settings.LayoutMode == "Split";
        // 单区模式同样保留分隔滑条：仅时钟贴右侧、仅作业贴左侧，用户随时能拖回分屏。
        bool singleZone = _settings.LayoutMode is "Clock" or "Board";
        bool canEditWidgets = _isEditing && GlobalPenButton.IsChecked != true;
        FreeLayoutHandles.IsHitTestVisible = split || singleZone || (_settings.LayoutMode is "Free" or "Clock" && canEditWidgets);
        if (split || singleZone) AddSplitHandle(split);
        if (_settings.LayoutMode is "Split" or "Clock" && canEditWidgets)
        {
            string prefix = _settings.LayoutMode == "Split" ? "Split" : "ClockMode";
            AddCenteredWidgetInteraction(prefix + "Clock", ClockContentView, "时钟", DockedClockAspectRatio, 220);
            AddCenteredWidgetInteraction(prefix + "Components", ClockComponentsView, "组件栏", DockedComponentsAspectRatio, 240);
        }
        if (_settings.LayoutMode != "Free" || !canEditWidgets) return;
        foreach (var (key, view) in _freeWidgets)
        {
            RegionPlacement placement = _settings.Widgets[key];
            AddWidgetInteractionLayer(view, placement, key == "Clock" ? "时钟" : "组件",
                (dx, dy) => WidgetLayout.Move(placement, dx, dy, DisplayRoot.ActualWidth, DisplayRoot.ActualHeight),
                (dx, dy) => WidgetLayout.Resize(placement, dx, dy, DisplayRoot.ActualWidth, DisplayRoot.ActualHeight),
                ScheduleSave);
        }
    }

    /// <summary>
    /// 分隔滑条：按当前手动分屏方向停在两区交界处。单区时贴在被占满的一侧边缘，
    /// 往恢复分屏的方向拖动即可回到分屏，拖到另一端则再次切换单区。
    /// </summary>
    private void AddSplitHandle(bool split)
    {
        bool vertical = SplitIsVertical;
        bool clockFirst = ClockIsFirstSplitRegion;
        bool clockOnly = _settings.LayoutMode == "Clock";
        double width = Math.Max(1, DisplayRoot.ActualWidth), height = Math.Max(1, DisplayRoot.ActualHeight);
        // 比例始终表示第一块区域的占比；单区模式下据此把分隔条放到第一块消失或占满的边缘。
        double ratio = split ? Math.Clamp(_settings.SplitRatio, .01, .99) : (clockOnly == clockFirst ? 1 : 0);
        _splitter = new Thumb
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(95, 128, 128, 128)),
            Width = vertical ? width : 10,
            Height = vertical ? 10 : height
        };
        string axis = vertical ? "上下" : "左右";
        string restoreDirection = vertical
            ? (ratio > .5 ? "向上" : "向下")
            : (ratio > .5 ? "向左" : "向右");
        ToolTipService.SetToolTip(_splitter, split
            ? $"拖动调整{axis}分屏，拖至边缘切换单区"
            : $"{restoreDirection}拖动回到分屏");
        if (vertical)
        {
            Canvas.SetLeft(_splitter, 0);
            Canvas.SetTop(_splitter, height * ratio - 5);
        }
        else
        {
            Canvas.SetLeft(_splitter, width * ratio - 5);
            Canvas.SetTop(_splitter, 0);
        }
        _splitter.DragStarted += (_, _) => BeginSplitDrag();
        _splitter.DragDelta += (_, args) => DragSplitHandle(vertical, args.HorizontalChange, args.VerticalChange);
        _splitter.DragCompleted += (_, _) => CompleteSplitDrag();
        Canvas.SetZIndex(_splitter, 100);
        FreeLayoutHandles.Children.Add(_splitter);
    }

    /// <summary>按下分隔条时记录第一块区域的起始比例；单区模式按当前方向和对调状态决定贴边位置。</summary>
    private void BeginSplitDrag()
    {
        _splitDragging = true;
        bool clockFirst = ClockIsFirstSplitRegion;
        _splitDragRatio = _settings.LayoutMode switch
        {
            "Clock" => clockFirst ? 1 : 0,
            "Board" => clockFirst ? 0 : 1,
            _ => _settings.SplitRatio
        };
        // 先按整块显示区铺好网格，拖动中作业区变大也不用逐帧重建成百上千条网格线。
        RenderSplitDragGrid();
    }

    /// <summary>拖动分隔条：按位移换算比例，分隔条即时跟手，两区尺寸合并到下一帧统一重排。</summary>
    private void DragSplitHandle(bool vertical, double horizontalChange, double verticalChange)
    {
        double width = Math.Max(1, DisplayRoot.ActualWidth), height = Math.Max(1, DisplayRoot.ActualHeight);
        _splitDragRatio = Math.Clamp(_splitDragRatio + (vertical ? verticalChange : horizontalChange) / (vertical ? height : width), .01, .99);
        if (_splitter is not null)
        {
            if (vertical) Canvas.SetTop(_splitter, height * _splitDragRatio - 5);
            else Canvas.SetLeft(_splitter, width * _splitDragRatio - 5);
        }
        // 指针事件的频率常高于刷新率，每次都重排两区会堆积布局；一帧只应用一次最新比例。
        if (_splitDragFramePending) return;
        _splitDragFramePending = true;
        CompositionTarget.Rendering += ApplySplitDragFrame;
    }

    private void ApplySplitDragFrame(object? sender, object e)
    {
        CompositionTarget.Rendering -= ApplySplitDragFrame;
        if (!_splitDragFramePending) return;
        _splitDragFramePending = false;
        _settings.SplitRatio = _splitDragRatio;
        // 拖回分屏范围时立即切回分屏，松手前就能看到分屏比例，松手时才写入最终模式。
        if (_settings.LayoutMode != "Split")
        {
            if (WidgetLayout.CompleteSplit(_splitDragRatio, ClockIsFirstSplitRegion) != "Split") return;
            _settings.LayoutMode = "Split";
            ApplyDisplayLayout();
            return;
        }
        // 已经是分屏时只改两区比例，不再重走整套布局（字号、组件迁移、笔迹层等都不变）。
        double ratio = Math.Clamp(_splitDragRatio, .05, .95);
        if (SplitIsVertical)
        {
            DisplayGrid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
            DisplayGrid.RowDefinitions[1].Height = new GridLength(1 - ratio, GridUnitType.Star);
        }
        else
        {
            DisplayGrid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
            DisplayGrid.ColumnDefinitions[1].Width = new GridLength(1 - ratio, GridUnitType.Star);
        }
    }

    /// <summary>松开分隔条：落在两端就切换单区并复位比例，否则留在分屏并保留拖出来的比例。</summary>
    private void CompleteSplitDrag()
    {
        if (_splitDragFramePending) ApplySplitDragFrame(null, EventArgs.Empty);
        _splitDragging = false;
        // 拖动时网格按整块显示区铺开，松手后按作业区的最终尺寸重新裁剪一次。
        _renderedGridWidth = 0;
        UpdateBoardBounds();
        string completed = WidgetLayout.CompleteSplit(_splitDragRatio, ClockIsFirstSplitRegion);
        if (completed is "Board" or "Clock")
        {
            _settings.LayoutMode = completed;
            _settings.SplitRatio = .4;
        }
        SyncLayoutChoiceSelection();
        SettingChanged();
        ScheduleScreenReport();
    }

    /// <summary>拖动分隔条切换模式后，把设置里的布局下拉框同步到同一模式。</summary>
    private void SyncLayoutChoiceSelection()
    {
        if (_layoutChoice is null) return;
        int index = _settings.LayoutMode switch { "Board" => 1, "Clock" => 2, "Free" => 3, _ => 0 };
        if (index < _layoutChoice.Items.Count) _layoutChoice.SelectedIndex = index;
    }

    private void AddCenteredWidgetInteraction(string key, Viewbox view, string label, double aspectRatio, double minimumWidth)
    {
        if (!_settings.Widgets.TryGetValue(key, out RegionPlacement? placement)) return;
        double width = ClockPanel.ActualWidth, height = ClockPanel.ActualHeight;
        AddWidgetInteractionLayer(view, placement, label,
            (_, dy) => WidgetLayout.MoveVerticallyCentered(placement, dy, width, height),
            (dx, dy) => WidgetLayout.ResizeCentered(placement, dx, dy, aspectRatio, minimumWidth, width, height),
            () => { _settings.CustomizedDockedWidgets.Add(key); ScheduleSave(); });
    }

    private void AddWidgetInteractionLayer(Viewbox view, RegionPlacement placement, string label,
        Action<double, double> movePlacement, Action<double, double> resizePlacement, Action complete)
    {
        Grid interactionLayer = new() { Width = placement.Width, Height = placement.Height, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
        // 选中框和缩放手柄同步出现：指针进入组件后描一圈表示当前选中的组件，离开组件再一起收起。
        Border selectionBorder = new() { Style = (Style)Application.Current.Resources["WidgetSelectionBorderStyle"] };
        Thumb move = new() { Style = (Style)Application.Current.Resources["InvisibleWidgetMoveThumbStyle"] };
        Thumb resize = new()
        {
            Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Style = (Style)Application.Current.Resources["WidgetResizeThumbStyle"], Opacity = 0
        };
        // 选中框画在移动层之上、缩放手柄之下，避免被移动层的覆盖范围挡住，同时让手柄始终保持在最上层。
        interactionLayer.Children.Add(move);
        interactionLayer.Children.Add(selectionBorder);
        interactionLayer.Children.Add(resize);
        bool dragging = false;
        bool pointerOver = false;
        void SetSelected(bool selected)
        {
            selectionBorder.Opacity = selected ? 1 : 0;
            resize.Opacity = selected ? 1 : 0;
        }
        interactionLayer.PointerEntered += (_, _) => { pointerOver = true; SetSelected(true); };
        interactionLayer.PointerExited += (_, _) => { pointerOver = false; if (!dragging) SetSelected(false); };
        void PositionInteraction()
        {
            interactionLayer.Width = placement.Width; interactionLayer.Height = placement.Height;
            Canvas.SetLeft(interactionLayer, placement.X); Canvas.SetTop(interactionLayer, placement.Y);
            ApplyCenteredPlacement(view, placement);
        }
        ToolTipService.SetToolTip(move, $"拖动{label}上下移动（锁定中轴线）");
        ToolTipService.SetToolTip(resize, $"缩放{label}");
        move.DragDelta += (_, args) =>
        {
            movePlacement(args.HorizontalChange, args.VerticalChange);
            PositionInteraction();
        };
        move.DragStarted += (_, _) => { dragging = true; SetSelected(true); };
        resize.DragDelta += (_, args) =>
        {
            resizePlacement(args.HorizontalChange, args.VerticalChange);
            PositionInteraction();
        };
        resize.DragStarted += (_, _) => { dragging = true; SetSelected(true); };
        void CompleteDrag()
        {
            dragging = false;
            // 松手后指针通常仍停在组件上，此时保持选中框可见，等指针离开再收起。
            SetSelected(pointerOver);
            complete();
        }
        move.DragCompleted += (_, _) => CompleteDrag(); resize.DragCompleted += (_, _) => CompleteDrag();
        Canvas.SetZIndex(interactionLayer, 50);
        PositionInteraction(); FreeLayoutHandles.Children.Add(interactionLayer);
    }
}
