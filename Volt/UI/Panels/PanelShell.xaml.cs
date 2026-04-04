using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Volt;

public partial class PanelShell : UserControl
{
    private readonly Dictionary<string, PanelRegistration> _panels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PanelPlacement, TabRegion> _regions = [];
    private readonly Dictionary<PanelPlacement, double> _regionSizes = [];
    private readonly HashSet<string> _collapsedByToggle = new(StringComparer.OrdinalIgnoreCase);
    private string? _draggingPanelId;
    private PanelPlacement? _highlightedZone;
    private PanelPlacement? _highlightedTabRegion;

    public static readonly DependencyProperty CenterContentProperty =
        DependencyProperty.Register(nameof(CenterContent), typeof(UIElement), typeof(PanelShell));

    public UIElement? CenterContent
    {
        get => (UIElement?)GetValue(CenterContentProperty);
        set => SetValue(CenterContentProperty, value);
    }

    /// <summary>
    /// Fired when a panel's layout changes (resize, show/hide, move).
    /// Parameters: panelId, placement, size.
    /// </summary>
    public event Action<string, PanelPlacement, double>? PanelLayoutChanged;

    public PanelShell()
    {
        InitializeComponent();

        // Create persistent TabRegions for each placement
        foreach (PanelPlacement p in Enum.GetValues<PanelPlacement>())
        {
            var region = new TabRegion();
            region.AddPanelRequested += OnAddPanelRequested;
            region.PanelClosed += OnTabPanelClosed;
            region.PanelDragStarted += OnPanelDragStarted;
            region.ActiveTabChanged += OnActiveTabChanged;
            region.RegionCloseRequested += OnRegionCloseRequested;
            _regions[p] = region;
            _regionSizes[p] = GetMinSize(p);
            GetContentPresenter(p).Content = region;
        }

        LeftSplitter.DragCompleted += OnSplitterDragCompleted;
        RightSplitter.DragCompleted += OnSplitterDragCompleted;
        TopSplitter.DragCompleted += OnSplitterDragCompleted;
        BottomSplitter.DragCompleted += OnSplitterDragCompleted;
    }

    public void RegisterPanel(IPanel panel, PanelPlacement placement, double defaultSize)
    {
        var container = new PanelContainer(panel);
        var reg = new PanelRegistration(panel, placement, container);
        _panels[panel.PanelId] = reg;
        _regionSizes[placement] = Math.Max(_regionSizes[placement], defaultSize);
    }

    public void ShowPanel(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (reg.IsVisible) return;
        reg.IsVisible = true;

        var region = _regions[reg.Placement];
        region.AddPanel(reg.Container);

        ShowRegion(reg.Placement);
        PanelLayoutChanged?.Invoke(panelId, reg.Placement, GetRegionSize(reg.Placement));
    }

    public void HidePanel(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (!reg.IsVisible) return;
        reg.IsVisible = false;

        var region = _regions[reg.Placement];
        region.RemovePanel(panelId);

        PanelLayoutChanged?.Invoke(panelId, reg.Placement, GetRegionSize(reg.Placement));
    }

    public bool IsPanelVisible(string panelId)
    {
        return _panels.TryGetValue(panelId, out var reg) && reg.IsVisible;
    }

    public void TogglePanel(string panelId)
    {
        if (IsPanelVisible(panelId))
            HidePanel(panelId);
        else
            ShowPanel(panelId);
    }

    public void ToggleRegion(PanelPlacement placement)
    {
        if (IsRegionVisible(placement))
        {
            CollapseRegionPanels(placement);
        }
        else
        {
            // Restore only panels that were previously collapsed by ToggleRegion
            var toRestore = _panels.Values
                .Where(r => r.Placement == placement && _collapsedByToggle.Contains(r.Panel.PanelId))
                .ToList();
            foreach (var reg in toRestore)
                _collapsedByToggle.Remove(reg.Panel.PanelId);

            if (toRestore.Count > 0)
            {
                foreach (var reg in toRestore)
                    ShowPanel(reg.Panel.PanelId);
            }
            else
            {
                // Show empty region with just the "+" button
                ShowRegion(placement);
                PanelLayoutChanged?.Invoke("", placement, GetRegionSize(placement));
            }
        }
    }

    public void MovePanel(string panelId, PanelPlacement newPlacement, bool keepSourceRegionVisible = false)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (reg.Placement == newPlacement) return;

        bool wasVisible = reg.IsVisible;
        var oldPlacement = reg.Placement;

        if (wasVisible)
        {
            // Remove from source region without collapsing it yet
            reg.IsVisible = false;
            _regions[oldPlacement].RemovePanel(panelId);
            PanelLayoutChanged?.Invoke(panelId, oldPlacement, GetRegionSize(oldPlacement));

            // Collapse source region only if empty and not asked to keep visible
            if (_regions[oldPlacement].IsEmpty && !keepSourceRegionVisible)
                CollapseRegion(oldPlacement);
        }

        reg.Placement = newPlacement;

        if (wasVisible) ShowPanel(panelId);
    }

    public void RestoreLayout(IReadOnlyList<PanelSlotConfig> configs)
    {
        // Group by placement and sort by TabIndex to restore tab order
        var sorted = configs.OrderBy(c => c.Placement).ThenBy(c => c.TabIndex).ToList();

        foreach (var config in sorted)
        {
            if (!_panels.TryGetValue(config.PanelId, out var reg)) continue;

            // Update placement if changed
            reg.Placement = config.Placement;
            _regionSizes[config.Placement] = Math.Max(config.Size, GetMinSize(config.Placement));

            if (config.Visible)
                ShowPanel(config.PanelId);
            else
                HidePanel(config.PanelId);
        }

        // Restore active tabs
        foreach (var config in sorted.Where(c => c.IsActiveTab && c.Visible))
        {
            if (!_panels.TryGetValue(config.PanelId, out var reg)) continue;
            _regions[reg.Placement].SetActiveTab(config.PanelId);
        }
    }

    public void RestoreOpenRegions(IReadOnlyList<RegionState> openRegions)
    {
        foreach (var state in openRegions)
        {
            _regionSizes[state.Placement] = Math.Max(state.Size, GetMinSize(state.Placement));
            if (!IsRegionVisible(state.Placement))
                ShowRegion(state.Placement);
        }
    }

    public List<PanelSlotConfig> GetCurrentLayout()
    {
        var result = new List<PanelSlotConfig>();
        foreach (var reg in _panels.Values)
        {
            var region = _regions[reg.Placement];
            result.Add(new PanelSlotConfig
            {
                PanelId = reg.Panel.PanelId,
                Placement = reg.Placement,
                Size = GetRegionSize(reg.Placement),
                Visible = reg.IsVisible,
                TabIndex = Math.Max(0, region.GetTabIndex(reg.Panel.PanelId)),
                IsActiveTab = region.IsActiveTab(reg.Panel.PanelId)
            });
        }
        return result;
    }

    public List<RegionState> GetOpenRegions()
    {
        return Enum.GetValues<PanelPlacement>()
            .Where(IsRegionVisible)
            .Select(p => new RegionState { Placement = p, Size = _regionSizes[p] })
            .ToList();
    }

    public IReadOnlyList<IPanel> GetAvailablePanels()
    {
        return _panels.Values
            .Where(r => !r.IsVisible)
            .Select(r => r.Panel)
            .ToList();
    }

    public bool IsRegionVisible(PanelPlacement placement)
    {
        return placement switch
        {
            PanelPlacement.Left => LeftCol.Width.Value > 0,
            PanelPlacement.Right => RightCol.Width.Value > 0,
            PanelPlacement.Top => TopRow.Height.Value > 0,
            PanelPlacement.Bottom => BottomRow.Height.Value > 0,
            _ => false
        };
    }

    private void ShowRegion(PanelPlacement placement)
    {
        if (IsRegionVisible(placement)) return;
        ApplyRegionSize(placement, _regionSizes[placement]);
        ShowSplitter(placement);
    }

    private void CollapseRegion(PanelPlacement placement)
    {
        ApplyRegionSize(placement, 0);
        HideSplitter(placement);
    }

    private double GetRegionSize(PanelPlacement placement)
    {
        return _regionSizes[placement];
    }

    private ContentPresenter GetContentPresenter(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => LeftContent,
        PanelPlacement.Right => RightContent,
        PanelPlacement.Top => TopContent,
        PanelPlacement.Bottom => BottomContent,
        _ => throw new ArgumentOutOfRangeException(nameof(placement))
    };

    private GridSplitter GetSplitter(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => LeftSplitter,
        PanelPlacement.Right => RightSplitter,
        PanelPlacement.Top => TopSplitter,
        PanelPlacement.Bottom => BottomSplitter,
        _ => throw new ArgumentOutOfRangeException(nameof(placement))
    };

    private void ApplyRegionSize(PanelPlacement placement, double size)
    {
        switch (placement)
        {
            case PanelPlacement.Left:
                LeftCol.MinWidth = size > 0 ? GetMinSize(placement) : 0;
                LeftCol.MaxWidth = size > 0 ? GetMaxSize(placement) : 0;
                LeftCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Right:
                RightCol.MinWidth = size > 0 ? GetMinSize(placement) : 0;
                RightCol.MaxWidth = size > 0 ? GetMaxSize(placement) : 0;
                RightCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Top:
                TopRow.MinHeight = size > 0 ? GetMinSize(placement) : 0;
                TopRow.MaxHeight = size > 0 ? GetMaxSize(placement) : 0;
                TopRow.Height = new GridLength(size);
                break;
            case PanelPlacement.Bottom:
                BottomRow.MinHeight = size > 0 ? GetMinSize(placement) : 0;
                BottomRow.MaxHeight = size > 0 ? GetMaxSize(placement) : 0;
                BottomRow.Height = new GridLength(size);
                break;
        }
    }

    private void ShowSplitter(PanelPlacement placement)
    {
        GetSplitter(placement).Visibility = Visibility.Visible;
        SetSplitterRowCol(placement, 1);
    }

    private void HideSplitter(PanelPlacement placement)
    {
        GetSplitter(placement).Visibility = Visibility.Collapsed;
        SetSplitterRowCol(placement, 0);
    }

    private void SetSplitterRowCol(PanelPlacement placement, double size)
    {
        switch (placement)
        {
            case PanelPlacement.Left:
                LeftSplitterCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Right:
                RightSplitterCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Top:
                TopSplitterRow.Height = new GridLength(size);
                break;
            case PanelPlacement.Bottom:
                BottomSplitterRow.Height = new GridLength(size);
                break;
        }
    }

    private static double GetMinSize(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left or PanelPlacement.Right => 150,
        PanelPlacement.Top or PanelPlacement.Bottom => 100,
        _ => 150
    };

    private static double GetMaxSize(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left or PanelPlacement.Right => 600,
        PanelPlacement.Top or PanelPlacement.Bottom => 400,
        _ => 600
    };

    private PanelPlacement GetPlacement(TabRegion region)
    {
        return _regions.First(kv => kv.Value == region).Key;
    }

    private void CollapseRegionPanels(PanelPlacement placement)
    {
        var panelsInRegion = _panels.Values.Where(r => r.Placement == placement && r.IsVisible).ToList();
        foreach (var reg in panelsInRegion)
        {
            _collapsedByToggle.Add(reg.Panel.PanelId);
            reg.IsVisible = false;
            _regions[placement].RemovePanel(reg.Panel.PanelId);
            PanelLayoutChanged?.Invoke(reg.Panel.PanelId, reg.Placement, GetRegionSize(placement));
        }
        CollapseRegion(placement);
        if (panelsInRegion.Count == 0)
            PanelLayoutChanged?.Invoke("", placement, GetRegionSize(placement));
    }

    // --- Event handlers ---

    private void OnAddPanelRequested(TabRegion region)
    {
        var available = GetAvailablePanels();
        if (available.Count == 0) return;

        var placement = GetPlacement(region);
        var menu = ContextMenuHelper.Create();
        foreach (var panel in available)
        {
            var id = panel.PanelId;
            var item = panel.IconGlyph is { } glyph
                ? ContextMenuHelper.Item(panel.Title, glyph, () =>
                {
                    if (_panels.TryGetValue(id, out var reg))
                    {
                        reg.Placement = placement;
                        ShowPanel(id);
                    }
                })
                : ContextMenuHelper.Item(panel.Title, () =>
                {
                    if (_panels.TryGetValue(id, out var reg))
                    {
                        reg.Placement = placement;
                        ShowPanel(id);
                    }
                });
            menu.Items.Add(item);
        }
        region.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OnTabPanelClosed(string panelId)
    {
        HidePanel(panelId);
    }

    private void OnRegionCloseRequested(TabRegion region)
    {
        CollapseRegionPanels(GetPlacement(region));
    }

    private void OnActiveTabChanged(string panelId)
    {
        if (_panels.TryGetValue(panelId, out var reg))
            PanelLayoutChanged?.Invoke(panelId, reg.Placement, GetRegionSize(reg.Placement));
    }

    private void OnSplitterDragCompleted(object? sender, DragCompletedEventArgs e)
    {
        foreach (PanelPlacement p in Enum.GetValues<PanelPlacement>())
        {
            if (GetSplitter(p) != sender) continue;
            if (!IsRegionVisible(p)) continue;

            double newSize = p switch
            {
                PanelPlacement.Left => LeftCol.ActualWidth,
                PanelPlacement.Right => RightCol.ActualWidth,
                PanelPlacement.Top => TopRow.ActualHeight,
                PanelPlacement.Bottom => BottomRow.ActualHeight,
                _ => _regionSizes[p]
            };
            _regionSizes[p] = newSize;

            // Fire for each visible panel in this region so settings persist
            var visiblePanels = _panels.Values.Where(r => r.Placement == p && r.IsVisible).ToList();
            if (visiblePanels.Count > 0)
            {
                foreach (var reg in visiblePanels)
                    PanelLayoutChanged?.Invoke(reg.Panel.PanelId, p, newSize);
            }
            else
            {
                // Empty region resized — still need to persist
                PanelLayoutChanged?.Invoke("", p, newSize);
            }
            break;
        }
    }

    // --- Drag-to-dock ---

    private Border GetDropOverlay(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => DropLeft,
        PanelPlacement.Right => DropRight,
        PanelPlacement.Top => DropTop,
        PanelPlacement.Bottom => DropBottom,
        _ => throw new ArgumentOutOfRangeException(nameof(placement))
    };

    private void OnPanelDragStarted(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (!reg.IsVisible) return;

        _draggingPanelId = panelId;
        _highlightedZone = null;

        // Show overlays (except the panel's current edge, unless the region has other tabs)
        foreach (PanelPlacement p in Enum.GetValues<PanelPlacement>())
        {
            var overlay = GetDropOverlay(p);
            bool isSourceRegion = p == reg.Placement;
            bool sourceHasOtherTabs = isSourceRegion && _regions[p].TabCount > 1;

            if (isSourceRegion && !sourceHasOtherTabs)
            {
                overlay.Visibility = Visibility.Collapsed;
            }
            else
            {
                overlay.Background = System.Windows.Media.Brushes.Transparent;
                overlay.Visibility = Visibility.Visible;
            }
        }

        var centerSize = CenterPresenter.RenderSize;
        DropLeft.Width = centerSize.Width / 4;
        DropRight.Width = centerSize.Width / 4;
        DropTop.Height = centerSize.Height / 4;
        DropBottom.Height = centerSize.Height / 4;

        CaptureMouse();
        Cursor = System.Windows.Input.Cursors.SizeAll;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingPanelId == null) return;

        var pos = e.GetPosition(CenterPresenter);
        var size = CenterPresenter.RenderSize;

        PanelPlacement? zone = null;

        var currentPlacement = _panels.TryGetValue(_draggingPanelId, out var reg)
            ? reg.Placement : (PanelPlacement?)null;
        bool sourceHasOtherTabs = currentPlacement.HasValue && _regions[currentPlacement.Value].TabCount > 1;

        if (size.Width > 0 && size.Height > 0 &&
            pos.X >= 0 && pos.X <= size.Width && pos.Y >= 0 && pos.Y <= size.Height)
        {
            double pLeft = pos.X / size.Width;
            double pRight = 1 - pLeft;
            double pTop = pos.Y / size.Height;
            double pBottom = 1 - pTop;

            const double threshold = 0.25;

            double minProp = double.MaxValue;
            bool skipCurrent = currentPlacement.HasValue && !sourceHasOtherTabs;
            if (!IsRegionVisible(PanelPlacement.Left) && !(skipCurrent && currentPlacement == PanelPlacement.Left) && pLeft < threshold && pLeft < minProp) { minProp = pLeft; zone = PanelPlacement.Left; }
            if (!IsRegionVisible(PanelPlacement.Right) && !(skipCurrent && currentPlacement == PanelPlacement.Right) && pRight < threshold && pRight < minProp) { minProp = pRight; zone = PanelPlacement.Right; }
            if (!IsRegionVisible(PanelPlacement.Top) && !(skipCurrent && currentPlacement == PanelPlacement.Top) && pTop < threshold && pTop < minProp) { minProp = pTop; zone = PanelPlacement.Top; }
            if (!IsRegionVisible(PanelPlacement.Bottom) && !(skipCurrent && currentPlacement == PanelPlacement.Bottom) && pBottom < threshold && pBottom < minProp) { zone = PanelPlacement.Bottom; }
        }

        if (zone != _highlightedZone)
        {
            if (_highlightedZone.HasValue)
                GetDropOverlay(_highlightedZone.Value).Background = System.Windows.Media.Brushes.Transparent;

            _highlightedZone = zone;
            if (zone.HasValue)
            {
                var fg = (System.Windows.Media.Brush)Application.Current.Resources[ThemeResourceKeys.TextFg];
                var highlight = fg.Clone();
                highlight.Opacity = 0.2;
                highlight.Freeze();
                GetDropOverlay(zone.Value).Background = highlight;
            }
        }

        // Also check if mouse is over a visible TabRegion (for dropping onto tab strip)
        PanelPlacement? tabRegionZone = null;
        if (zone == null)
        {
            foreach (var kv in _regions)
            {
                if (!IsRegionVisible(kv.Key)) continue;
                bool isSource = currentPlacement == kv.Key;
                if (isSource && !sourceHasOtherTabs) continue;

                var regionPos = e.GetPosition(kv.Value);
                var regionSize = kv.Value.RenderSize;
                if (regionPos.X >= 0 && regionPos.X <= regionSize.Width &&
                    regionPos.Y >= 0 && regionPos.Y <= regionSize.Height)
                {
                    tabRegionZone = kv.Key;
                    break;
                }
            }
        }

        if (tabRegionZone != _highlightedTabRegion)
        {
            if (_highlightedTabRegion.HasValue)
                _regions[_highlightedTabRegion.Value].SetDropHighlight(false);
            _highlightedTabRegion = tabRegionZone;
            if (tabRegionZone.HasValue)
                _regions[tabRegionZone.Value].SetDropHighlight(true);
        }
    }

    protected override void OnMouseUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_draggingPanelId == null) return;

        var panelId = _draggingPanelId;
        var target = _highlightedZone ?? _highlightedTabRegion;

        EndDrag();

        if (target.HasValue)
            MovePanel(panelId, target.Value, keepSourceRegionVisible: true);
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_draggingPanelId != null && e.Key == System.Windows.Input.Key.Escape)
        {
            EndDrag();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_draggingPanelId != null)
            EndDrag();
    }

    private void EndDrag()
    {
        _draggingPanelId = null;
        _highlightedZone = null;
        if (_highlightedTabRegion.HasValue)
        {
            _regions[_highlightedTabRegion.Value].SetDropHighlight(false);
            _highlightedTabRegion = null;
        }
        Cursor = null;
        ReleaseMouseCapture();

        foreach (PanelPlacement p in Enum.GetValues<PanelPlacement>())
        {
            var overlay = GetDropOverlay(p);
            overlay.Background = System.Windows.Media.Brushes.Transparent;
            overlay.Visibility = Visibility.Collapsed;
        }
    }

    private class PanelRegistration(IPanel panel, PanelPlacement placement, PanelContainer container)
    {
        public IPanel Panel { get; } = panel;
        public PanelContainer Container { get; } = container;
        public PanelPlacement Placement { get; set; } = placement;
        public bool IsVisible { get; set; }
    }
}
