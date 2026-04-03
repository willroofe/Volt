# Tabbed Panel Regions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Evolve the panel system from one-panel-per-region to tabbed regions with a "+" button for adding panels.

**Architecture:** A new `TabRegion` UserControl manages a tab strip, "+" button, and content switching per dock region. `PanelContainer` simplifies to a thin content wrapper. `PanelShell` manages four persistent TabRegions instead of individual PanelContainers.

**Tech Stack:** WPF (.NET 10), C#, xUnit + Xunit.StaFact

---

### Task 1: Update PanelSlotConfig with TabIndex and IsActiveTab

**Files:**
- Modify: `Volt/UI/Panels/PanelSlotConfig.cs`

- [ ] **Step 1: Add new fields to PanelSlotConfig**

```csharp
namespace Volt;

public class PanelSlotConfig
{
    public string PanelId { get; set; } = "";
    public PanelPlacement Placement { get; set; } = PanelPlacement.Left;
    public double Size { get; set; } = 250;
    public bool Visible { get; set; }
    public int TabIndex { get; set; }
    public bool IsActiveTab { get; set; }
}
```

- [ ] **Step 2: Build to verify no compilation errors**

Run: `dotnet build Volt.sln`
Expected: Build succeeded (existing code doesn't set these fields, defaults are fine)

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Panels/PanelSlotConfig.cs
git commit -m "feat: add TabIndex and IsActiveTab to PanelSlotConfig"
```

---

### Task 2: Simplify PanelContainer to Content Wrapper

**Files:**
- Modify: `Volt/UI/Panels/PanelContainer.cs`

The current `PanelContainer` is a `DockPanel` with a 34px header bar (title text, mouse drag handling) and the panel's content below. It needs to become a thin wrapper that just holds the panel reference and its content as a child. The header and drag logic will move to `TabRegion` in a later task.

- [ ] **Step 1: Replace PanelContainer with simplified version**

Replace the entire contents of `Volt/UI/Panels/PanelContainer.cs` with:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace Volt;

/// <summary>
/// Thin wrapper around an IPanel. Holds the panel reference and hosts its Content.
/// The tab strip and drag handling are managed by TabRegion.
/// </summary>
public class PanelContainer : ContentControl
{
    private readonly IPanel _panel;

    public PanelContainer(IPanel panel)
    {
        _panel = panel;
        Content = panel.Content;
    }

    public string PanelId => _panel.PanelId;
    public IPanel Panel => _panel;
}
```

- [ ] **Step 2: Build (expect errors — PanelShell references DragStarted which no longer exists)**

Run: `dotnet build Volt.sln`
Expected: Build errors in `PanelShell.xaml.cs` referencing `container.DragStarted`. This is expected and will be fixed in Task 4 when PanelShell is rewritten.

- [ ] **Step 3: Commit (with build errors — they'll be resolved in Task 4)**

```bash
git add Volt/UI/Panels/PanelContainer.cs
git commit -m "refactor: simplify PanelContainer to thin content wrapper"
```

---

### Task 3: Create TabRegion Control

**Files:**
- Create: `Volt/UI/Panels/TabRegion.xaml`
- Create: `Volt/UI/Panels/TabRegion.cs`

`TabRegion` is the core new component. It manages a tab strip with a "+" button, content switching between panels, and forwards drag events.

- [ ] **Step 1: Create TabRegion.xaml**

Create `Volt/UI/Panels/TabRegion.xaml`:

```xml
<UserControl x:Class="Volt.TabRegion"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DockPanel>
        <!-- Header bar: tab strip + "+" button -->
        <Border DockPanel.Dock="Top"
                BorderThickness="0,0,0,1"
                Background="{DynamicResource ThemeExplorerHeaderBg}"
                BorderBrush="{DynamicResource ThemeTabBorder}">
            <DockPanel Height="33">
                <Button x:Name="AddButton" DockPanel.Dock="Right"
                        Width="28" Height="28" Margin="4,0"
                        Content="&#xE710;"
                        FontFamily="Segoe MDL2 Assets" FontSize="10"
                        Foreground="{DynamicResource ThemeButtonFg}"
                        Background="Transparent" BorderThickness="0"
                        Focusable="False" Cursor="Hand"
                        ToolTip="Add Panel">
                    <Button.Template>
                        <ControlTemplate TargetType="Button">
                            <Border x:Name="Bd" Background="Transparent" CornerRadius="3">
                                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="Bd" Property="Background"
                                            Value="{DynamicResource ThemeButtonHover}"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Button.Template>
                </Button>
                <ScrollViewer x:Name="TabScrollViewer"
                              HorizontalScrollBarVisibility="Hidden"
                              VerticalScrollBarVisibility="Disabled"
                              CanContentScroll="False">
                    <StackPanel x:Name="TabStrip" Orientation="Horizontal"/>
                </ScrollViewer>
            </DockPanel>
        </Border>

        <!-- Active panel content -->
        <ContentPresenter x:Name="ContentArea"/>
    </DockPanel>
</UserControl>
```

- [ ] **Step 2: Create TabRegion.cs**

Create `Volt/UI/Panels/TabRegion.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public partial class TabRegion : UserControl
{
    private readonly List<TabEntry> _tabs = [];
    private TabEntry? _activeTab;
    private Point? _dragStartPoint;
    private string? _dragPanelId;

    private const double DragDeadZone = 5;

    /// <summary>Raised when the user requests to add a panel via the "+" button. Parameter: the TabRegion raising the event.</summary>
    public event Action<TabRegion>? AddPanelRequested;

    /// <summary>Raised when a panel tab is closed via context menu.</summary>
    public event Action<string>? PanelClosed;

    /// <summary>Raised when the user drags a tab past the dead zone.</summary>
    public event Action<string>? PanelDragStarted;

    /// <summary>Raised when the active tab changes.</summary>
    public event Action<string>? ActiveTabChanged;

    public TabRegion()
    {
        InitializeComponent();
        AddButton.Click += (_, _) => AddPanelRequested?.Invoke(this);
    }

    public string? ActivePanelId => _activeTab?.Container.PanelId;
    public int TabCount => _tabs.Count;
    public bool IsEmpty => _tabs.Count == 0;
    public IReadOnlyList<string> PanelIds => _tabs.Select(t => t.Container.PanelId).ToList();

    public void AddPanel(PanelContainer container)
    {
        var entry = CreateTabEntry(container);
        _tabs.Add(entry);
        TabStrip.Children.Add(entry.Header);
        SetActiveTab(entry);
    }

    public void RemovePanel(string panelId)
    {
        var entry = _tabs.Find(t => t.Container.PanelId == panelId);
        if (entry == null) return;

        int idx = _tabs.IndexOf(entry);
        _tabs.Remove(entry);
        TabStrip.Children.Remove(entry.Header);

        // Unsubscribe from title changes
        entry.Container.Panel.TitleChanged -= entry.OnTitleChanged;

        if (_activeTab == entry)
        {
            if (_tabs.Count > 0)
            {
                int newIdx = Math.Min(idx, _tabs.Count - 1);
                SetActiveTab(_tabs[newIdx]);
            }
            else
            {
                _activeTab = null;
                ContentArea.Content = null;
            }
        }
    }

    public void SetActiveTab(string panelId)
    {
        var entry = _tabs.Find(t => t.Container.PanelId == panelId);
        if (entry != null) SetActiveTab(entry);
    }

    public int GetTabIndex(string panelId)
    {
        return _tabs.FindIndex(t => t.Container.PanelId == panelId);
    }

    public bool IsActiveTab(string panelId)
    {
        return _activeTab?.Container.PanelId == panelId;
    }

    private void SetActiveTab(TabEntry entry)
    {
        if (_activeTab == entry) return;

        // Deactivate previous
        if (_activeTab != null)
        {
            _activeTab.Header.SetResourceReference(Border.BackgroundProperty, "ThemeExplorerHeaderBg");
        }

        _activeTab = entry;
        ContentArea.Content = entry.Container;
        entry.Header.SetResourceReference(Border.BackgroundProperty, "ThemeTabActive");
        ActiveTabChanged?.Invoke(entry.Container.PanelId);
    }

    private TabEntry CreateTabEntry(PanelContainer container)
    {
        var panel = container.Panel;

        var textBlock = new TextBlock
        {
            Text = panel.Title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, "ThemeExplorerHeaderFg");

        var header = new Border
        {
            Child = textBlock,
            Height = 33,
            MinWidth = 40,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        header.SetResourceReference(Border.BorderBrushProperty, "ThemeTabBorder");
        header.SetResourceReference(Border.BackgroundProperty, "ThemeExplorerHeaderBg");

        // Title change subscription
        Action onTitleChanged = () => textBlock.Text = panel.Title;
        panel.TitleChanged += onTitleChanged;

        // Click to activate
        header.MouseLeftButtonDown += (_, e) =>
        {
            var tabEntry = _tabs.Find(t => t.Container.PanelId == container.PanelId);
            if (tabEntry != null) SetActiveTab(tabEntry);
            // Start drag tracking
            _dragStartPoint = e.GetPosition(this);
            _dragPanelId = container.PanelId;
            header.CaptureMouse();
            e.Handled = true;
        };

        header.MouseMove += (sender, e) =>
        {
            if (_dragStartPoint == null || _dragPanelId == null) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CancelDragTracking(sender);
                return;
            }
            var pos = e.GetPosition(this);
            var delta = pos - _dragStartPoint.Value;
            if (Math.Abs(delta.X) > DragDeadZone || Math.Abs(delta.Y) > DragDeadZone)
            {
                var panelId = _dragPanelId;
                CancelDragTracking(sender);
                PanelDragStarted?.Invoke(panelId);
            }
        };

        header.MouseLeftButtonUp += (sender, _) => CancelDragTracking(sender);

        // Right-click context menu with "Close"
        header.MouseRightButtonUp += (_, e) =>
        {
            var menu = ContextMenuHelper.Create();
            menu.Items.Add(ContextMenuHelper.Item("Close", () => PanelClosed?.Invoke(container.PanelId)));
            header.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        };

        return new TabEntry(container, header, onTitleChanged);
    }

    private void CancelDragTracking(object sender)
    {
        _dragStartPoint = null;
        _dragPanelId = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private class TabEntry(PanelContainer container, Border header, Action onTitleChanged)
    {
        public PanelContainer Container { get; } = container;
        public Border Header { get; } = header;
        public Action OnTitleChanged { get; } = onTitleChanged;
    }
}
```

- [ ] **Step 3: Build (expect errors — PanelShell still references old PanelContainer.DragStarted)**

Run: `dotnet build Volt.sln`
Expected: TabRegion compiles; PanelShell errors remain from Task 2.

- [ ] **Step 4: Commit**

```bash
git add Volt/UI/Panels/TabRegion.xaml Volt/UI/Panels/TabRegion.cs
git commit -m "feat: create TabRegion control with tab strip and add-panel button"
```

---

### Task 4: Rewrite PanelShell to Use TabRegions

**Files:**
- Modify: `Volt/UI/Panels/PanelShell.xaml.cs`

This is the largest task. PanelShell changes from managing individual PanelContainers to managing four persistent TabRegions. The XAML stays the same (ContentPresenters receive TabRegions instead of PanelContainers).

- [ ] **Step 1: Replace PanelShell.xaml.cs with TabRegion-based implementation**

Replace the entire contents of `Volt/UI/Panels/PanelShell.xaml.cs` with:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Volt;

public partial class PanelShell : UserControl
{
    private readonly Dictionary<string, PanelRegistration> _panels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PanelPlacement, TabRegion> _regions = [];
    private readonly Dictionary<PanelPlacement, double> _regionSizes = [];
    private string? _draggingPanelId;
    private PanelPlacement? _highlightedZone;

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
        var reg = new PanelRegistration(panel, placement, defaultSize, container);
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

        if (region.IsEmpty)
            CollapseRegion(reg.Placement);

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
            // Collapse: hide all panels in this region but remember their placement
            var panelsInRegion = _panels.Values.Where(r => r.Placement == placement && r.IsVisible).ToList();
            foreach (var reg in panelsInRegion)
            {
                reg.IsVisible = false;
                _regions[placement].RemovePanel(reg.Panel.PanelId);
            }
            CollapseRegion(placement);
            foreach (var reg in panelsInRegion)
                PanelLayoutChanged?.Invoke(reg.Panel.PanelId, reg.Placement, GetRegionSize(placement));
        }
        else
        {
            // Restore: show panels that were assigned to this region, or show empty "+" state
            var panelsInRegion = _panels.Values.Where(r => r.Placement == placement && !r.IsVisible).ToList();
            if (panelsInRegion.Count > 0)
            {
                foreach (var reg in panelsInRegion)
                    ShowPanel(reg.Panel.PanelId);
            }
            else
            {
                // Show empty region with just the "+" button
                ShowRegion(placement);
            }
        }
    }

    public void MovePanel(string panelId, PanelPlacement newPlacement)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (reg.Placement == newPlacement) return;

        bool wasVisible = reg.IsVisible;
        if (wasVisible) HidePanel(panelId);

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
                TabIndex = region.GetTabIndex(reg.Panel.PanelId),
                IsActiveTab = region.IsActiveTab(reg.Panel.PanelId)
            });
        }
        return result;
    }

    public IReadOnlyList<IPanel> GetAvailablePanels()
    {
        return _panels.Values
            .Where(r => !r.IsVisible)
            .Select(r => r.Panel)
            .ToList();
    }

    private bool IsRegionVisible(PanelPlacement placement)
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
                LeftCol.MaxWidth = size > 0 ? GetMaxSize(placement) : double.PositiveInfinity;
                LeftCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Right:
                RightCol.MinWidth = size > 0 ? GetMinSize(placement) : 0;
                RightCol.MaxWidth = size > 0 ? GetMaxSize(placement) : double.PositiveInfinity;
                RightCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Top:
                TopRow.MinHeight = size > 0 ? GetMinSize(placement) : 0;
                TopRow.MaxHeight = size > 0 ? GetMaxSize(placement) : double.PositiveInfinity;
                TopRow.Height = new GridLength(size);
                break;
            case PanelPlacement.Bottom:
                BottomRow.MinHeight = size > 0 ? GetMinSize(placement) : 0;
                BottomRow.MaxHeight = size > 0 ? GetMaxSize(placement) : double.PositiveInfinity;
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

    // --- Event handlers ---

    private void OnAddPanelRequested(TabRegion region)
    {
        var available = GetAvailablePanels();
        if (available.Count == 0) return;

        var placement = _regions.First(kv => kv.Value == region).Key;
        var menu = ContextMenuHelper.Create();
        foreach (var panel in available)
        {
            var id = panel.PanelId;
            menu.Items.Add(ContextMenuHelper.Item(panel.Title, () =>
            {
                if (_panels.TryGetValue(id, out var reg))
                {
                    reg.Placement = placement;
                    ShowPanel(id);
                }
            }));
        }
        region.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OnTabPanelClosed(string panelId)
    {
        HidePanel(panelId);
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
            foreach (var reg in _panels.Values.Where(r => r.Placement == p && r.IsVisible))
                PanelLayoutChanged?.Invoke(reg.Panel.PanelId, p, newSize);
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
            if (!(skipCurrent && currentPlacement == PanelPlacement.Left) && pLeft < threshold && pLeft < minProp) { minProp = pLeft; zone = PanelPlacement.Left; }
            if (!(skipCurrent && currentPlacement == PanelPlacement.Right) && pRight < threshold && pRight < minProp) { minProp = pRight; zone = PanelPlacement.Right; }
            if (!(skipCurrent && currentPlacement == PanelPlacement.Top) && pTop < threshold && pTop < minProp) { minProp = pTop; zone = PanelPlacement.Top; }
            if (!(skipCurrent && currentPlacement == PanelPlacement.Bottom) && pBottom < threshold && pBottom < minProp) { zone = PanelPlacement.Bottom; }
        }

        if (zone != _highlightedZone)
        {
            if (_highlightedZone.HasValue)
                GetDropOverlay(_highlightedZone.Value).Background = System.Windows.Media.Brushes.Transparent;

            _highlightedZone = zone;
            if (zone.HasValue)
            {
                var fg = (System.Windows.Media.Brush)Application.Current.Resources["ThemeTextFg"];
                var highlight = fg.Clone();
                highlight.Opacity = 0.2;
                highlight.Freeze();
                GetDropOverlay(zone.Value).Background = highlight;
            }
        }
    }

    protected override void OnMouseUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_draggingPanelId == null) return;

        var panelId = _draggingPanelId;
        var target = _highlightedZone;

        EndDrag();

        if (target.HasValue)
            MovePanel(panelId, target.Value);
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
        Cursor = null;
        ReleaseMouseCapture();

        foreach (PanelPlacement p in Enum.GetValues<PanelPlacement>())
        {
            var overlay = GetDropOverlay(p);
            overlay.Background = System.Windows.Media.Brushes.Transparent;
            overlay.Visibility = Visibility.Collapsed;
        }
    }

    private class PanelRegistration(IPanel panel, PanelPlacement placement, double size, PanelContainer container)
    {
        public IPanel Panel { get; } = panel;
        public PanelContainer Container { get; } = container;
        public PanelPlacement Placement { get; set; } = placement;
        public double DefaultSize { get; } = size;
        public bool IsVisible { get; set; }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: Build succeeded (test project may have warnings but should compile)

- [ ] **Step 3: Run existing tests**

Run: `dotnet test Volt.Tests --verbosity normal`
Expected: Some PanelShell tests may need adjustment — note which fail.

- [ ] **Step 4: Commit**

```bash
git add Volt/UI/Panels/PanelShell.xaml.cs
git commit -m "feat: rewrite PanelShell to manage TabRegions instead of individual PanelContainers"
```

---

### Task 5: Update Tests for Tabbed Panel Behavior

**Files:**
- Modify: `Volt.Tests/PanelShellTests.cs`

The existing tests test PanelShell's public API (RegisterPanel, ShowPanel, HidePanel, TogglePanel, MovePanel, GetCurrentLayout, RestoreLayout, PanelLayoutChanged). The API names haven't changed but behavior has — panels are now added to TabRegions. Tests need updating to account for:
- `GetCurrentLayout` now includes `TabIndex` and `IsActiveTab`
- `ToggleRegion` behavior (collapse/restore region)
- Multi-tab behavior (two panels in one region)

- [ ] **Step 1: Replace PanelShellTests.cs with updated tests**

Replace the entire contents of `Volt.Tests/PanelShellTests.cs` with:

```csharp
using Xunit;
using System.Windows;

namespace Volt.Tests;

public class PanelShellTests
{
    private sealed class FakePanel(string id, string title) : IPanel
    {
        public string PanelId => id;
        public string Title => title;
        public UIElement Content { get; } = new System.Windows.Controls.Border();
#pragma warning disable CS0067
        public event Action? TitleChanged;
#pragma warning restore CS0067
    }

    [StaFact]
    public void RegisterPanel_ThenShow_MakesPanelVisible()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test Panel");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        Assert.False(shell.IsPanelVisible("test"));

        shell.ShowPanel("test");
        Assert.True(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void HidePanel_MakesPanelNotVisible()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test Panel");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        shell.HidePanel("test");
        Assert.False(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void TogglePanel_FlipsVisibility()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test Panel");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        shell.TogglePanel("test");
        Assert.True(shell.IsPanelVisible("test"));

        shell.TogglePanel("test");
        Assert.False(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void MovePanel_ChangesPlacement()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test Panel");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        shell.MovePanel("test", PanelPlacement.Right);

        var layout = shell.GetCurrentLayout();
        Assert.Single(layout);
        Assert.Equal(PanelPlacement.Right, layout[0].Placement);
        Assert.True(layout[0].Visible);
    }

    [StaFact]
    public void GetCurrentLayout_ReturnsAllRegisteredPanels()
    {
        var shell = new PanelShell();
        var panel1 = new FakePanel("left", "Left");
        var panel2 = new FakePanel("right", "Right");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 200);
        shell.RegisterPanel(panel2, PanelPlacement.Right, 300);
        shell.ShowPanel("left");

        var layout = shell.GetCurrentLayout();
        Assert.Equal(2, layout.Count);

        var leftConfig = layout.First(c => c.PanelId == "left");
        Assert.Equal(PanelPlacement.Left, leftConfig.Placement);
        Assert.True(leftConfig.Visible);
        Assert.True(leftConfig.IsActiveTab);

        var rightConfig = layout.First(c => c.PanelId == "right");
        Assert.Equal(PanelPlacement.Right, rightConfig.Placement);
        Assert.False(rightConfig.Visible);
    }

    [StaFact]
    public void RestoreLayout_AppliesConfigToRegisteredPanels()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        shell.RestoreLayout([
            new PanelSlotConfig
            {
                PanelId = "test",
                Placement = PanelPlacement.Right,
                Size = 300,
                Visible = true,
                TabIndex = 0,
                IsActiveTab = true
            }
        ]);

        var layout = shell.GetCurrentLayout();
        var config = layout.Single();
        Assert.Equal(PanelPlacement.Right, config.Placement);
        Assert.True(config.Visible);
        Assert.True(config.IsActiveTab);
    }

    [StaFact]
    public void PanelLayoutChanged_FiresOnShowHide()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);

        var events = new List<(string id, PanelPlacement placement, double size)>();
        shell.PanelLayoutChanged += (id, p, s) => events.Add((id, p, s));

        shell.ShowPanel("test");
        shell.HidePanel("test");

        Assert.Equal(2, events.Count);
        Assert.Equal("test", events[0].id);
        Assert.Equal("test", events[1].id);
    }

    [StaFact]
    public void ShowPanel_UnknownId_DoesNotThrow()
    {
        var shell = new PanelShell();
        shell.ShowPanel("nonexistent");
    }

    [StaFact]
    public void ShowPanel_AlreadyVisible_DoesNotFireEvent()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        var events = new List<string>();
        shell.PanelLayoutChanged += (id, _, _) => events.Add(id);

        shell.ShowPanel("test");
        Assert.Empty(events);
    }

    [StaFact]
    public void MultiplePanels_SameRegion_BothVisible()
    {
        var shell = new PanelShell();
        var panel1 = new FakePanel("a", "Panel A");
        var panel2 = new FakePanel("b", "Panel B");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 250);
        shell.RegisterPanel(panel2, PanelPlacement.Left, 250);

        shell.ShowPanel("a");
        shell.ShowPanel("b");

        Assert.True(shell.IsPanelVisible("a"));
        Assert.True(shell.IsPanelVisible("b"));

        var layout = shell.GetCurrentLayout();
        var configA = layout.First(c => c.PanelId == "a");
        var configB = layout.First(c => c.PanelId == "b");
        Assert.Equal(0, configA.TabIndex);
        Assert.Equal(1, configB.TabIndex);
        // Last shown panel is active
        Assert.True(configB.IsActiveTab);
    }

    [StaFact]
    public void HidePanel_InMultiTabRegion_DoesNotCollapseRegion()
    {
        var shell = new PanelShell();
        var panel1 = new FakePanel("a", "Panel A");
        var panel2 = new FakePanel("b", "Panel B");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 250);
        shell.RegisterPanel(panel2, PanelPlacement.Left, 250);

        shell.ShowPanel("a");
        shell.ShowPanel("b");

        shell.HidePanel("a");

        Assert.False(shell.IsPanelVisible("a"));
        Assert.True(shell.IsPanelVisible("b"));
    }

    [StaFact]
    public void GetAvailablePanels_ReturnsHiddenPanels()
    {
        var shell = new PanelShell();
        var panel1 = new FakePanel("a", "Panel A");
        var panel2 = new FakePanel("b", "Panel B");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 250);
        shell.RegisterPanel(panel2, PanelPlacement.Right, 250);

        shell.ShowPanel("a");

        var available = shell.GetAvailablePanels();
        Assert.Single(available);
        Assert.Equal("b", available[0].PanelId);
    }

    [StaFact]
    public void ToggleRegion_CollapsesAndRestores()
    {
        var shell = new PanelShell();
        var panel = new FakePanel("test", "Test");
        shell.RegisterPanel(panel, PanelPlacement.Left, 250);
        shell.ShowPanel("test");

        // Collapse
        shell.ToggleRegion(PanelPlacement.Left);
        Assert.False(shell.IsPanelVisible("test"));

        // Restore
        shell.ToggleRegion(PanelPlacement.Left);
        Assert.True(shell.IsPanelVisible("test"));
    }

    [StaFact]
    public void RestoreLayout_PreservesTabOrder()
    {
        var shell = new PanelShell();
        var panel1 = new FakePanel("a", "Panel A");
        var panel2 = new FakePanel("b", "Panel B");
        shell.RegisterPanel(panel1, PanelPlacement.Left, 250);
        shell.RegisterPanel(panel2, PanelPlacement.Left, 250);

        shell.RestoreLayout([
            new PanelSlotConfig { PanelId = "b", Placement = PanelPlacement.Left, Size = 250, Visible = true, TabIndex = 0, IsActiveTab = false },
            new PanelSlotConfig { PanelId = "a", Placement = PanelPlacement.Left, Size = 250, Visible = true, TabIndex = 1, IsActiveTab = true }
        ]);

        var layout = shell.GetCurrentLayout();
        var configA = layout.First(c => c.PanelId == "a");
        var configB = layout.First(c => c.PanelId == "b");
        Assert.Equal(1, configA.TabIndex);
        Assert.Equal(0, configB.TabIndex);
        Assert.True(configA.IsActiveTab);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test Volt.Tests --verbosity normal`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add Volt.Tests/PanelShellTests.cs
git commit -m "test: update PanelShell tests for tabbed region behavior"
```

---

### Task 6: Verify Full Build and Run

**Files:**
- None (verification only)

- [ ] **Step 1: Full build**

Run: `dotnet build Volt.sln`
Expected: Build succeeded with 0 errors.

- [ ] **Step 2: Run all tests**

Run: `dotnet test Volt.Tests --verbosity normal`
Expected: All tests pass.

- [ ] **Step 3: Manual smoke test**

Run: `dotnet run --project Volt/Volt.csproj`

Verify:
1. App launches without crash
2. Ctrl+B shows left panel region (with file explorer if a folder was previously open, or empty "+" state)
3. Ctrl+B again collapses the region
4. Ctrl+Alt+B shows/hides right panel region
5. Click "+" button — dropdown shows available panels
6. Select a panel from "+" — it appears as a tab
7. Right-click a panel tab — "Close" option works
8. Drag a panel tab to a different region — it moves there
9. Close the app and reopen — panel layout is restored

- [ ] **Step 4: Commit any fixes from smoke testing**

---

### Task 7: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update the Panel System section in CLAUDE.md**

Update the Panel System section to reflect tabbed regions:

Replace the `PanelShell` description with:

```
**PanelShell** (`UI/Panels/PanelShell.xaml/.cs`) — `UserControl` that owns the layout. A 5×5 Grid with four dock regions and splitters around a center `ContentPresenter` bound to the `CenterContent` dependency property. Manages four persistent `TabRegion` instances (one per placement). Key API:
- `RegisterPanel(IPanel, PanelPlacement, double defaultSize)` — registers a panel with a default region
- `ShowPanel(panelId)` / `HidePanel(panelId)` / `TogglePanel(panelId)` — add/remove panel tabs in regions
- `MovePanel(panelId, newPlacement)` — relocates a panel to a different region
- `ToggleRegion(placement)` — collapse/restore an entire region (remembers tabs)
- `GetAvailablePanels()` — returns registered panels not currently visible (for "+" menu)
- `RestoreLayout(configs)` / `GetCurrentLayout()` — serialization for settings persistence
- `PanelLayoutChanged` event — fires on resize/show/hide for settings persistence
```

Add `TabRegion` description:

```
**TabRegion** (`UI/Panels/TabRegion.xaml/.cs`) — `UserControl` managing a tab strip with "+" button per dock region. Shows tabs for each panel, supports click-to-switch, right-click-to-close, and drag-to-reposition. Empty state shows just the "+" button. Raises `AddPanelRequested`, `PanelClosed`, `PanelDragStarted`, and `ActiveTabChanged` events.
```

Update `PanelContainer` description:

```
**PanelContainer** (`UI/Panels/PanelContainer.cs`) — thin `ContentControl` wrapper around an `IPanel`. Holds the panel reference and hosts its `Content`. Tab strip and drag handling are managed by `TabRegion`.
```

Update `PanelSlotConfig` description:

```
**PanelSlotConfig** (`UI/Panels/PanelSlotConfig.cs`) — serializable layout state per panel (`PanelId`, `Placement`, `Size`, `Visible`, `TabIndex`, `IsActiveTab`). Stored in `AppSettings.Editor.PanelLayouts`.
```

- [ ] **Step 2: Build and run tests to verify nothing is broken**

Run: `dotnet build Volt.sln && dotnet test Volt.Tests`
Expected: Build succeeded, all tests pass.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md for tabbed panel regions"
```
