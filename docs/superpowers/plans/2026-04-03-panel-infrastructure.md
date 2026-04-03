# Panel Infrastructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hardcoded file explorer layout with a generic panel shell that supports docking panels on all four sides around a center editor zone.

**Architecture:** A `PanelShell` UserControl owns a 5x5 Grid with four dock regions (left, right, top, bottom) and a center zone. Panels implement `IPanel` and are registered with the shell. MainWindow becomes a thin coordinator that delegates layout to the shell.

**Tech Stack:** WPF (.NET 10), C# 13, XAML

---

## File Structure

| File | Role |
|------|------|
| **Create:** `Volt/UI/Panels/IPanel.cs` | `IPanel` interface, `PanelPlacement` enum |
| **Create:** `Volt/UI/Panels/PanelSlotConfig.cs` | Serializable layout state per panel |
| **Create:** `Volt/UI/Panels/PanelShell.xaml` | Shell grid layout with regions and splitters |
| **Create:** `Volt/UI/Panels/PanelShell.xaml.cs` | Shell logic: register, show, hide, move, resize |
| **Create:** `Volt.Tests/PanelShellTests.cs` | Tests for shell registration, show/hide, move |
| **Modify:** `Volt/AppSettings.cs:30-37,123` | Remove old explorer layout fields, add `PanelLayouts` |
| **Modify:** `Volt/UI/FileExplorerPanel.xaml.cs:8` | Implement `IPanel` |
| **Modify:** `Volt/UI/MainWindow.xaml:266-354` | Replace `MainContentGrid` with `PanelShell` |
| **Modify:** `Volt/UI/MainWindow.xaml.cs:374-421,56-66,83-87,684,985-987` | Replace `SetExplorerVisible` with shell calls |
| **Modify:** `Volt/UI/CommandPaletteCommands.cs:140-155` | Update explorer toggle/side commands |
| **Modify:** `Volt/UI/SettingsWindow.xaml.cs:9,22,49-50,117` | Remove `PanelSide` field |
| **Modify:** `Volt/UI/SettingsWindow.xaml:~403` | Remove PanelSide ComboBox |

---

### Task 1: Create IPanel Interface and PanelPlacement Enum

**Files:**
- Create: `Volt/UI/Panels/IPanel.cs`

- [ ] **Step 1: Create the file**

```csharp
using System.Windows;

namespace Volt;

public interface IPanel
{
    string PanelId { get; }
    string Title { get; }
    UIElement Content { get; }
}

public enum PanelPlacement
{
    Left,
    Right,
    Top,
    Bottom
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Panels/IPanel.cs
git commit -m "feat: add IPanel interface and PanelPlacement enum"
```

---

### Task 2: Create PanelSlotConfig

**Files:**
- Create: `Volt/UI/Panels/PanelSlotConfig.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace Volt;

public class PanelSlotConfig
{
    public string PanelId { get; set; } = "";
    public PanelPlacement Placement { get; set; } = PanelPlacement.Left;
    public double Size { get; set; } = 250;
    public bool Visible { get; set; }
}
```

Note: Uses `class` with `{ get; set; }` rather than `record` with `init` so that `System.Text.Json` can deserialize it without a constructor. Default values ensure a valid state when deserializing incomplete JSON.

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Panels/PanelSlotConfig.cs
git commit -m "feat: add PanelSlotConfig for panel layout persistence"
```

---

### Task 3: Create PanelShell — XAML Grid Layout

**Files:**
- Create: `Volt/UI/Panels/PanelShell.xaml`

- [ ] **Step 1: Create the XAML file**

The grid has 5 rows and 5 columns. Outer rows are for top/bottom regions, outer columns in the middle row are for left/right regions. The center cell takes all remaining space. Splitters are 1px GridSplitters between each region and the center.

```xml
<UserControl x:Class="Volt.PanelShell"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:Volt">
    <Grid x:Name="RootGrid">
        <Grid.RowDefinitions>
            <RowDefinition x:Name="TopRow" Height="0"/>
            <RowDefinition x:Name="TopSplitterRow" Height="0"/>
            <RowDefinition Height="*"/>
            <RowDefinition x:Name="BottomSplitterRow" Height="0"/>
            <RowDefinition x:Name="BottomRow" Height="0"/>
        </Grid.RowDefinitions>
        <Grid.ColumnDefinitions>
            <ColumnDefinition x:Name="LeftCol" Width="0"/>
            <ColumnDefinition x:Name="LeftSplitterCol" Width="0"/>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition x:Name="RightSplitterCol" Width="0"/>
            <ColumnDefinition x:Name="RightCol" Width="0"/>
        </Grid.ColumnDefinitions>

        <!-- Top region -->
        <ContentPresenter x:Name="TopContent" Grid.Row="0" Grid.Column="0" Grid.ColumnSpan="5"/>
        <GridSplitter x:Name="TopSplitter" Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="5"
                      Height="1" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"
                      Background="{DynamicResource ThemeBorderBrush}"
                      ResizeDirection="Rows" ResizeBehavior="PreviousAndNext"
                      Visibility="Collapsed"/>

        <!-- Left region -->
        <ContentPresenter x:Name="LeftContent" Grid.Row="2" Grid.Column="0"/>
        <GridSplitter x:Name="LeftSplitter" Grid.Row="2" Grid.Column="1"
                      Width="1" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"
                      Background="{DynamicResource ThemeBorderBrush}"
                      ResizeDirection="Columns" ResizeBehavior="PreviousAndNext"
                      Visibility="Collapsed"/>

        <!-- Center -->
        <ContentPresenter x:Name="CenterPresenter" Grid.Row="2" Grid.Column="2"
                          Content="{Binding CenterContent, RelativeSource={RelativeSource AncestorType=local:PanelShell}}"/>

        <!-- Right region -->
        <GridSplitter x:Name="RightSplitter" Grid.Row="2" Grid.Column="3"
                      Width="1" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"
                      Background="{DynamicResource ThemeBorderBrush}"
                      ResizeDirection="Columns" ResizeBehavior="PreviousAndNext"
                      Visibility="Collapsed"/>
        <ContentPresenter x:Name="RightContent" Grid.Row="2" Grid.Column="4"/>

        <!-- Bottom region -->
        <GridSplitter x:Name="BottomSplitter" Grid.Row="3" Grid.Column="0" Grid.ColumnSpan="5"
                      Height="1" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"
                      Background="{DynamicResource ThemeBorderBrush}"
                      ResizeDirection="Rows" ResizeBehavior="PreviousAndNext"
                      Visibility="Collapsed"/>
        <ContentPresenter x:Name="BottomContent" Grid.Row="4" Grid.Column="0" Grid.ColumnSpan="5"/>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Verify it compiles (needs empty code-behind — see next task)**

Create a minimal code-behind to verify:

```csharp
namespace Volt;

public partial class PanelShell : System.Windows.Controls.UserControl
{
    public PanelShell()
    {
        InitializeComponent();
    }
}
```

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Panels/PanelShell.xaml Volt/UI/Panels/PanelShell.xaml.cs
git commit -m "feat: add PanelShell XAML grid layout with four dock regions"
```

---

### Task 4: Implement PanelShell Code-Behind

**Files:**
- Modify: `Volt/UI/Panels/PanelShell.xaml.cs`

- [ ] **Step 1: Write the full code-behind**

Replace the minimal code-behind with the full implementation:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Volt;

public partial class PanelShell : UserControl
{
    private readonly Dictionary<string, PanelRegistration> _panels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fired when a panel's layout changes (resize, show/hide, move).
    /// Parameters: panelId, placement, size.
    /// </summary>
    public static readonly DependencyProperty CenterContentProperty =
        DependencyProperty.Register(nameof(CenterContent), typeof(UIElement), typeof(PanelShell));

    public new UIElement? CenterContent
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
        LeftSplitter.DragCompleted += OnSplitterDragCompleted;
        RightSplitter.DragCompleted += OnSplitterDragCompleted;
        TopSplitter.DragCompleted += OnSplitterDragCompleted;
        BottomSplitter.DragCompleted += OnSplitterDragCompleted;
    }

    public void RegisterPanel(IPanel panel, PanelPlacement placement, double defaultSize)
    {
        var reg = new PanelRegistration(panel, placement, defaultSize);
        _panels[panel.PanelId] = reg;
        PlacePanel(reg);
    }

    public void ShowPanel(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (reg.IsVisible) return;
        reg.IsVisible = true;
        ApplyRegionSize(reg.Placement, reg.Size);
        ShowSplitter(reg.Placement);
        PanelLayoutChanged?.Invoke(panelId, reg.Placement, reg.Size);
    }

    public void HidePanel(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (!reg.IsVisible) return;
        reg.IsVisible = false;
        ApplyRegionSize(reg.Placement, 0);
        HideSplitter(reg.Placement);
        PanelLayoutChanged?.Invoke(panelId, reg.Placement, reg.Size);
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

    public void MovePanel(string panelId, PanelPlacement newPlacement)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (reg.Placement == newPlacement) return;

        bool wasVisible = reg.IsVisible;
        if (wasVisible) HidePanel(panelId);

        ClearRegionContent(reg.Placement);
        reg.Placement = newPlacement;
        PlacePanel(reg);

        if (wasVisible) ShowPanel(panelId);
    }

    public void RestoreLayout(IReadOnlyList<PanelSlotConfig> configs)
    {
        foreach (var config in configs)
        {
            if (!_panels.TryGetValue(config.PanelId, out var reg)) continue;

            if (reg.Placement != config.Placement)
            {
                ClearRegionContent(reg.Placement);
                reg.Placement = config.Placement;
                PlacePanel(reg);
            }

            reg.Size = Math.Max(config.Size, GetMinSize(config.Placement));

            if (config.Visible)
                ShowPanel(config.PanelId);
            else
                HidePanel(config.PanelId);
        }
    }

    public List<PanelSlotConfig> GetCurrentLayout()
    {
        var result = new List<PanelSlotConfig>();
        foreach (var reg in _panels.Values)
        {
            result.Add(new PanelSlotConfig
            {
                PanelId = reg.Panel.PanelId,
                Placement = reg.Placement,
                Size = reg.Size,
                Visible = reg.IsVisible
            });
        }
        return result;
    }

    private void PlacePanel(PanelRegistration reg)
    {
        var presenter = GetContentPresenter(reg.Placement);
        presenter.Content = reg.Panel.Content;
    }

    private void ClearRegionContent(PanelPlacement placement)
    {
        var presenter = GetContentPresenter(placement);
        presenter.Content = null;
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

    private void OnSplitterDragCompleted(object? sender, DragCompletedEventArgs e)
    {
        // Find which panel is in the region that was resized
        foreach (var reg in _panels.Values)
        {
            if (!reg.IsVisible) continue;
            if (GetSplitter(reg.Placement) != sender) continue;

            double newSize = reg.Placement switch
            {
                PanelPlacement.Left => LeftCol.ActualWidth,
                PanelPlacement.Right => RightCol.ActualWidth,
                PanelPlacement.Top => TopRow.ActualHeight,
                PanelPlacement.Bottom => BottomRow.ActualHeight,
                _ => reg.Size
            };
            reg.Size = newSize;
            PanelLayoutChanged?.Invoke(reg.Panel.PanelId, reg.Placement, newSize);
            break;
        }
    }

    private class PanelRegistration(IPanel panel, PanelPlacement placement, double size)
    {
        public IPanel Panel { get; } = panel;
        public PanelPlacement Placement { get; set; } = placement;
        public double Size { get; set; } = size;
        public bool IsVisible { get; set; }
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Panels/PanelShell.xaml.cs
git commit -m "feat: implement PanelShell register/show/hide/move/resize logic"
```

---

### Task 5: Write PanelShell Tests

**Files:**
- Create: `Volt.Tests/PanelShellTests.cs`

- [ ] **Step 1: Write tests**

These tests verify the panel registration, show/hide, move, and layout state APIs. Since `PanelShell` is a WPF UserControl, tests use `[STAThread]` fact attribute and instantiate the control directly (it doesn't need to be in a window to test the API).

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
        Assert.Equal(200, leftConfig.Size);
        Assert.True(leftConfig.Visible);

        var rightConfig = layout.First(c => c.PanelId == "right");
        Assert.Equal(PanelPlacement.Right, rightConfig.Placement);
        Assert.Equal(300, rightConfig.Size);
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
                Visible = true
            }
        ]);

        var layout = shell.GetCurrentLayout();
        var config = layout.Single();
        Assert.Equal(PanelPlacement.Right, config.Placement);
        Assert.Equal(300, config.Size);
        Assert.True(config.Visible);
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
        shell.ShowPanel("nonexistent"); // should not throw
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

        shell.ShowPanel("test"); // already visible
        Assert.Empty(events);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test Volt.Tests --filter PanelShell --verbosity normal`
Expected: All 8 tests pass

- [ ] **Step 3: Commit**

```bash
git add Volt.Tests/PanelShellTests.cs
git commit -m "test: add PanelShell unit tests for register/show/hide/move/layout"
```

---

### Task 6: Implement IPanel on FileExplorerPanel

**Files:**
- Modify: `Volt/UI/FileExplorerPanel.xaml.cs:8`

- [ ] **Step 1: Add IPanel implementation to class declaration and properties**

Change the class declaration at line 8 from:

```csharp
public partial class FileExplorerPanel : UserControl
```

to:

```csharp
public partial class FileExplorerPanel : UserControl, IPanel
```

Add the three `IPanel` properties after the `_pendingExpandPaths` field (after line 22):

```csharp
    public string PanelId => "file-explorer";
    public string Title => "Explorer";
    public UIElement Content => this;
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/FileExplorerPanel.xaml.cs
git commit -m "feat: implement IPanel on FileExplorerPanel"
```

---

### Task 7: Update AppSettings — Remove Old Fields, Add PanelLayouts

**Files:**
- Modify: `Volt/AppSettings.cs`

- [ ] **Step 1: Remove old layout fields from ExplorerSettings**

In `ExplorerSettings` (lines 30-37), remove `PanelSide`, `PanelWidth`, and `PanelVisible`. Keep `OpenFolderPath` and `ExpandedPaths` — those are content state.

Change from:

```csharp
public class ExplorerSettings
{
    public string PanelSide { get; set; } = "Left";
    public double PanelWidth { get; set; } = 250;
    public bool PanelVisible { get; set; } = false;
    public string? OpenFolderPath { get; set; }
    public List<string> ExpandedPaths { get; set; } = [];
}
```

to:

```csharp
public class ExplorerSettings
{
    public string? OpenFolderPath { get; set; }
    public List<string> ExpandedPaths { get; set; } = [];
}
```

- [ ] **Step 2: Add PanelLayouts to EditorSettings**

In `EditorSettings` (line 39-47), add `PanelLayouts` after the `Explorer` property:

```csharp
public class EditorSettings
{
    public int TabSize { get; set; } = 4;
    public bool WordWrap { get; set; }
    public FontSettings Font { get; set; } = new();
    public CaretSettings Caret { get; set; } = new();
    public FindSettings Find { get; set; } = new();
    public ExplorerSettings Explorer { get; set; } = new();
    public List<PanelSlotConfig> PanelLayouts { get; set; } = [];
}
```

- [ ] **Step 3: Remove PanelSideOptions from AppSettings**

Remove line 123:

```csharp
    public static readonly string[] PanelSideOptions = ["Left", "Right"];
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build will fail due to references in other files — that's expected. Just verify the AppSettings changes themselves are syntactically correct. The compile errors will be resolved in subsequent tasks.

- [ ] **Step 5: Commit**

```bash
git add Volt/AppSettings.cs
git commit -m "feat: replace explorer layout fields with generic PanelLayouts in settings"
```

---

### Task 8: Remove PanelSide from SettingsWindow

**Files:**
- Modify: `Volt/UI/SettingsWindow.xaml.cs`
- Modify: `Volt/UI/SettingsWindow.xaml`

- [ ] **Step 1: Remove PanelSide from SettingsSnapshot record**

In `SettingsWindow.xaml.cs` line 5-9, change from:

```csharp
public record SettingsSnapshot(
    int TabSize, bool BlockCaret, int CaretBlinkMs,
    string FontFamily, double FontSize, string FontWeight,
    double LineHeight, string ColorTheme, string FindBarPosition,
    string PanelSide);
```

to:

```csharp
public record SettingsSnapshot(
    int TabSize, bool BlockCaret, int CaretBlinkMs,
    string FontFamily, double FontSize, string FontWeight,
    double LineHeight, string ColorTheme, string FindBarPosition);
```

- [ ] **Step 2: Remove PanelSide property and references from SettingsWindow class**

Remove:
- Line 22: `public string PanelSide { get; private set; }`
- Lines 49-50: `PanelSide = snapshot.PanelSide;` and `PanelSideBox.SelectedIndex = ...`
- Line 117: `PanelSide = PanelSideBox.SelectedIndex == 1 ? "Right" : "Left";`

- [ ] **Step 3: Remove PanelSide ComboBox from SettingsWindow.xaml**

Find the `PanelSideBox` ComboBox (around line 403 in `SettingsWindow.xaml`) and remove it along with its label. This is inside the Explorer settings section.

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: May still have errors from MainWindow references — will be fixed in the next task.

- [ ] **Step 5: Commit**

```bash
git add Volt/UI/SettingsWindow.xaml Volt/UI/SettingsWindow.xaml.cs
git commit -m "refactor: remove PanelSide from SettingsWindow"
```

---

### Task 9: Integrate PanelShell into MainWindow XAML

**Files:**
- Modify: `Volt/UI/MainWindow.xaml:266-354`

- [ ] **Step 1: Replace MainContentGrid with PanelShell**

Replace the entire `MainContentGrid` block (lines 266-355) with:

```xml
            <local:PanelShell x:Name="Shell" Grid.Row="0"
                              WindowChrome.IsHitTestVisibleInChrome="True">
                <local:PanelShell.Content>
                    <Grid x:Name="EditorColumnGrid">
                    <DockPanel x:Name="EditorArea">
                        <!-- Tab bar -->
                        <Border DockPanel.Dock="Top" Background="{DynamicResource ThemeTabBarBg}"
                                BorderBrush="{DynamicResource ThemeTabBorder}" BorderThickness="0,0,0,1">
                            <DockPanel Height="33">
                                <Button x:Name="NewTabButton" DockPanel.Dock="Right"
                                        Width="28" Height="28" Margin="4,0"
                                        Content="&#xE710;"
                                        FontFamily="Segoe MDL2 Assets" FontSize="10"
                                        Foreground="{DynamicResource ThemeButtonFg}"
                                        Background="Transparent" BorderThickness="0"
                                        Focusable="False" Cursor="Hand"
                                        Click="OnNewTab"
                                        ToolTip="New Tab (Ctrl+N)">
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
                                <Grid>
                                    <ScrollViewer x:Name="TabScrollViewer"
                                                  HorizontalScrollBarVisibility="Hidden"
                                                  VerticalScrollBarVisibility="Disabled"
                                                  CanContentScroll="False"
                                                  PreviewMouseWheel="OnTabScrollViewerMouseWheel">
                                        <Grid>
                                            <StackPanel x:Name="TabStrip" Orientation="Horizontal"/>
                                            <Border x:Name="TabDropIndicator" Width="2"
                                                    HorizontalAlignment="Left" VerticalAlignment="Stretch"
                                                    Background="{DynamicResource ThemeTextFg}"
                                                    Visibility="Collapsed" IsHitTestVisible="False"/>
                                        </Grid>
                                    </ScrollViewer>
                                    <!-- Overflow fade indicators with arrow glyphs -->
                                    <Border x:Name="TabOverflowLeft" Width="32"
                                            HorizontalAlignment="Left" VerticalAlignment="Stretch"
                                            IsHitTestVisible="False" Visibility="Collapsed">
                                        <TextBlock Text="&#xE76B;" FontFamily="Segoe MDL2 Assets" FontSize="12"
                                                   Foreground="{DynamicResource ThemeTextFg}"
                                                   HorizontalAlignment="Left" VerticalAlignment="Center"
                                                   Margin="6,0,0,0"/>
                                    </Border>
                                    <Border x:Name="TabOverflowRight" Width="32"
                                            HorizontalAlignment="Right" VerticalAlignment="Stretch"
                                            IsHitTestVisible="False" Visibility="Collapsed">
                                        <TextBlock Text="&#xE76C;" FontFamily="Segoe MDL2 Assets" FontSize="12"
                                                   Foreground="{DynamicResource ThemeTextFg}"
                                                   HorizontalAlignment="Right" VerticalAlignment="Center"
                                                   Margin="0,0,6,0"/>
                                    </Border>
                                </Grid>
                            </DockPanel>
                        </Border>
                        <Border x:Name="EditorHost"/>
                    </DockPanel>

                    <!-- Find bar overlay (scoped to editor panel) -->
                    <local:FindBar x:Name="FindBarControl" />
                    </Grid>
                </local:PanelShell.Content>
            </local:PanelShell>
```

Wait — `PanelShell` uses `SetCenterContent()` method, not a `Content` property in XAML. The center content should be set in code-behind. So the XAML should be:

```xml
            <local:PanelShell x:Name="Shell"
                              WindowChrome.IsHitTestVisibleInChrome="True">
                <local:PanelShell.Resources>
                    <!-- EditorArea is set as center content in code-behind -->
                </local:PanelShell.Resources>
            </local:PanelShell>
```

And the EditorArea grid needs to be defined somewhere the code-behind can access it. The simplest approach: keep the EditorArea inside the PanelShell as a named child, but set it as center content in code-behind.

Actually, the cleanest approach: define the EditorArea grid inside MainWindow's resources or as a field created in code. But since it contains many named elements (`TabStrip`, `TabScrollViewer`, etc.), it must stay in XAML.

**Revised approach:** Add a `CenterContent` dependency property to PanelShell so it can be set in XAML, and bind the `CenterContent` ContentPresenter to it.

Update `PanelShell.xaml` — change the center ContentPresenter to:

```xml
        <ContentPresenter x:Name="CenterContent" Grid.Row="2" Grid.Column="2"
                          Content="{Binding CenterContent, RelativeSource={RelativeSource AncestorType=local:PanelShell}}"/>
```

And in `PanelShell.xaml.cs`, replace the `SetCenterContent` method with a dependency property:

```csharp
    public static readonly DependencyProperty CenterContentProperty =
        DependencyProperty.Register(nameof(CenterContent), typeof(UIElement), typeof(PanelShell));

    public UIElement? CenterContent
    {
        get => (UIElement?)GetValue(CenterContentProperty);
        set => SetValue(CenterContentProperty, value);
    }
```

And rename the XAML ContentPresenter to `CenterPresenter` to avoid name collision:

```xml
        <ContentPresenter x:Name="CenterPresenter" Grid.Row="2" Grid.Column="2"
                          Content="{Binding CenterContent, RelativeSource={RelativeSource AncestorType=local:PanelShell}}"/>
```

Then in MainWindow.xaml, the full replacement for lines 266-355:

```xml
            <local:PanelShell x:Name="Shell"
                              WindowChrome.IsHitTestVisibleInChrome="True">
                <local:PanelShell.CenterContent>
                    <Grid x:Name="EditorColumnGrid">
                    <DockPanel x:Name="EditorArea">
                        <!-- Tab bar -->
                        <Border DockPanel.Dock="Top" Background="{DynamicResource ThemeTabBarBg}"
                                BorderBrush="{DynamicResource ThemeTabBorder}" BorderThickness="0,0,0,1">
                            <DockPanel Height="33">
                                <Button x:Name="NewTabButton" DockPanel.Dock="Right"
                                        Width="28" Height="28" Margin="4,0"
                                        Content="&#xE710;"
                                        FontFamily="Segoe MDL2 Assets" FontSize="10"
                                        Foreground="{DynamicResource ThemeButtonFg}"
                                        Background="Transparent" BorderThickness="0"
                                        Focusable="False" Cursor="Hand"
                                        Click="OnNewTab"
                                        ToolTip="New Tab (Ctrl+N)">
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
                                <Grid>
                                    <ScrollViewer x:Name="TabScrollViewer"
                                                  HorizontalScrollBarVisibility="Hidden"
                                                  VerticalScrollBarVisibility="Disabled"
                                                  CanContentScroll="False"
                                                  PreviewMouseWheel="OnTabScrollViewerMouseWheel">
                                        <Grid>
                                            <StackPanel x:Name="TabStrip" Orientation="Horizontal"/>
                                            <Border x:Name="TabDropIndicator" Width="2"
                                                    HorizontalAlignment="Left" VerticalAlignment="Stretch"
                                                    Background="{DynamicResource ThemeTextFg}"
                                                    Visibility="Collapsed" IsHitTestVisible="False"/>
                                        </Grid>
                                    </ScrollViewer>
                                    <Border x:Name="TabOverflowLeft" Width="32"
                                            HorizontalAlignment="Left" VerticalAlignment="Stretch"
                                            IsHitTestVisible="False" Visibility="Collapsed">
                                        <TextBlock Text="&#xE76B;" FontFamily="Segoe MDL2 Assets" FontSize="12"
                                                   Foreground="{DynamicResource ThemeTextFg}"
                                                   HorizontalAlignment="Left" VerticalAlignment="Center"
                                                   Margin="6,0,0,0"/>
                                    </Border>
                                    <Border x:Name="TabOverflowRight" Width="32"
                                            HorizontalAlignment="Right" VerticalAlignment="Stretch"
                                            IsHitTestVisible="False" Visibility="Collapsed">
                                        <TextBlock Text="&#xE76C;" FontFamily="Segoe MDL2 Assets" FontSize="12"
                                                   Foreground="{DynamicResource ThemeTextFg}"
                                                   HorizontalAlignment="Right" VerticalAlignment="Center"
                                                   Margin="0,0,6,0"/>
                                    </Border>
                                </Grid>
                            </DockPanel>
                        </Border>
                        <Border x:Name="EditorHost"/>
                    </DockPanel>
                    <local:FindBar x:Name="FindBarControl" />
                    </Grid>
                </local:PanelShell.CenterContent>
            </local:PanelShell>
```

This removes: `MainContentGrid`, `ExplorerColumn`, `SplitterColumn`, `EditorColumn`, `ExplorerSplitter`, `HeaderBorderBridge`, and the `ExplorerPanel` element (it will be registered via code-behind).

Note: `ExplorerPanel` must be moved out of XAML and into code-behind creation, OR defined in XAML outside the shell and registered in code. The simplest: keep `ExplorerPanel` defined in MainWindow XAML resources or as a separate element and register it. Actually, since `FileExplorerPanel` is used by name throughout MainWindow.xaml.cs, the simplest approach is to define it in the MainWindow XAML (but outside the shell) and register it:

Add before the `Shell` element in the `DockPanel`:

```xml
<!-- Explorer panel (hosted by PanelShell, defined here for x:Name access) -->
```

Actually, the cleanest approach: just create it in code-behind. Add to MainWindow constructor:

```csharp
var explorerPanel = new FileExplorerPanel();
// ... wire events ...
Shell.RegisterPanel(explorerPanel, PanelPlacement.Left, 250);
```

But this means all the `ExplorerPanel.xxx` references in XAML.cs need to use a field instead of a XAML-named element. Since `ExplorerPanel` is already referenced ~20 times in the code-behind, we should keep it as a field:

```csharp
private readonly FileExplorerPanel ExplorerPanel = new();
```

Remove `ExplorerPanel` from XAML entirely.

- [ ] **Step 2: Remove old named elements from XAML**

Ensure these named elements are removed from MainWindow.xaml since they no longer exist:
- `MainContentGrid`
- `ExplorerColumn`
- `SplitterColumn`
- `ExplorerSplitter`
- `HeaderBorderBridge`
- `ExplorerPanel` (moved to code-behind field)

The `EditorColumn` ColumnDefinition name was used in `SetExplorerVisible` — no longer needed.

- [ ] **Step 3: Verify XAML is well-formed (may not compile until code-behind is updated)**

- [ ] **Step 4: Commit**

```bash
git add Volt/UI/MainWindow.xaml
git commit -m "refactor: replace MainContentGrid with PanelShell in MainWindow XAML"
```

---

### Task 10: Update MainWindow Code-Behind

**Files:**
- Modify: `Volt/UI/MainWindow.xaml.cs`
- Modify: `Volt/UI/Panels/PanelShell.xaml.cs` (add CenterContent DP if not already done)

This is the largest task — it replaces all the manual grid column manipulation with shell API calls.

- [ ] **Step 1: Add ExplorerPanel field and remove XAML reference**

Add a field to MainWindow class (near the top, around line 18):

```csharp
    private readonly FileExplorerPanel ExplorerPanel = new();
```

- [ ] **Step 2: Replace constructor explorer setup**

Replace lines 56-66 (the explorer restore block) with:

```csharp
        // Register explorer panel with shell
        Shell.RegisterPanel(ExplorerPanel, PanelPlacement.Left, 250);
        RestorePanelLayout();

        if (_settings.Editor.Explorer.OpenFolderPath is string folderPath && Directory.Exists(folderPath))
        {
            ExplorerPanel.OpenFolder(folderPath);
            if (_settings.Editor.Explorer.ExpandedPaths.Count > 0)
                ExplorerPanel.RestoreExpandedPaths(_settings.Editor.Explorer.ExpandedPaths);
        }
```

- [ ] **Step 3: Replace splitter drag handler with PanelLayoutChanged handler**

Remove lines 83-87 (the ExplorerSplitter.DragCompleted handler). Add:

```csharp
        Shell.PanelLayoutChanged += OnPanelLayoutChanged;
```

And add the handler method:

```csharp
    private void OnPanelLayoutChanged(string panelId, PanelPlacement placement, double size)
    {
        _settings.Editor.PanelLayouts = Shell.GetCurrentLayout();
        _settings.Save();
    }
```

- [ ] **Step 4: Replace ToggleExplorer method**

Replace the existing `ToggleExplorer` method (lines 374-380) with:

```csharp
    private void ToggleExplorer()
    {
        Shell.TogglePanel("file-explorer");
    }
```

- [ ] **Step 5: Remove SetExplorerVisible method entirely**

Delete the `SetExplorerVisible` method (lines 382-422). All callers will be updated to use shell methods.

- [ ] **Step 6: Update all SetExplorerVisible(true) calls**

Replace every `SetExplorerVisible(true)` call with `Shell.ShowPanel("file-explorer")`. These are at approximately:
- Line 64 (constructor — already handled in Step 3)
- Line 438 (OpenFolderInExplorer)
- Line 987 (ApplySettingsFromDialog)
- Line 1066 (command palette RefreshLayout lambda)
- Line 1101 (after opening project)
- Line 1151 (after opening project with session)

Replace every `SetExplorerVisible(false)` call with `Shell.HidePanel("file-explorer")`:
- Line 446 (CloseFolderInExplorer)

- [ ] **Step 7: Update OnSettings / ApplySettingsFromDialog**

In `OnSettings` (line 961-971), remove `PanelSide` from the snapshot construction:

Change:
```csharp
        var snapshot = new SettingsSnapshot(
            Editor.TabSize, _settings.Editor.Caret.BlockCaret, _settings.Editor.Caret.BlinkMs,
            Editor.FontFamilyName, Editor.EditorFontSize, Editor.EditorFontWeight,
            Editor.LineHeightMultiplier, _settings.Application.ColorTheme, _settings.Editor.Find.BarPosition,
            _settings.Editor.Explorer.PanelSide);
```

to:

```csharp
        var snapshot = new SettingsSnapshot(
            Editor.TabSize, _settings.Editor.Caret.BlockCaret, _settings.Editor.Caret.BlinkMs,
            Editor.FontFamilyName, Editor.EditorFontSize, Editor.EditorFontWeight,
            Editor.LineHeightMultiplier, _settings.Application.ColorTheme, _settings.Editor.Find.BarPosition);
```

In `ApplySettingsFromDialog` (line 974-991), remove:
```csharp
        _settings.Editor.Explorer.PanelSide = dlg.PanelSide;
        if (_settings.Editor.Explorer.PanelVisible)
            SetExplorerVisible(true);
```

- [ ] **Step 8: Add RestorePanelLayout helper**

```csharp
    private void RestorePanelLayout()
    {
        if (_settings.Editor.PanelLayouts.Count > 0)
            Shell.RestoreLayout(_settings.Editor.PanelLayouts);
    }
```

- [ ] **Step 9: Update OnWindowClosing to save panel layout**

In `OnWindowClosing` (around line 684), replace:
```csharp
        _settings.Editor.Explorer.ExpandedPaths = ExplorerPanel.GetExpandedPaths();
```

with:

```csharp
        _settings.Editor.Explorer.ExpandedPaths = ExplorerPanel.GetExpandedPaths();
        _settings.Editor.PanelLayouts = Shell.GetCurrentLayout();
```

- [ ] **Step 10: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 11: Commit**

```bash
git add Volt/UI/MainWindow.xaml Volt/UI/MainWindow.xaml.cs Volt/UI/Panels/PanelShell.xaml Volt/UI/Panels/PanelShell.xaml.cs
git commit -m "refactor: integrate PanelShell into MainWindow, remove manual grid layout"
```

---

### Task 11: Update CommandPaletteCommands

**Files:**
- Modify: `Volt/UI/CommandPaletteCommands.cs`

- [ ] **Step 1: Remove ExplorerActions.RefreshLayout and PanelSide command**

In `CommandPaletteCommands.cs`, the `ExplorerActions` record (line 6-10) loses `RefreshLayout`:

Change:
```csharp
internal record ExplorerActions(
    Action ToggleExplorer,
    Action OpenFolder,
    Action CloseFolder,
    Action RefreshLayout);
```

to:

```csharp
internal record ExplorerActions(
    Action ToggleExplorer,
    Action OpenFolder,
    Action CloseFolder);
```

- [ ] **Step 2: Remove the "Explorer: Panel Side" command**

Remove lines 146-155 (the entire `"Explorer: Panel Side"` command entry from the list).

- [ ] **Step 3: Update MainWindow's command palette wiring**

In MainWindow.xaml.cs `OpenCommandPalette` method (line 1060-1075), update the ExplorerActions construction:

Change:
```csharp
            new ExplorerActions(ToggleExplorer, OpenFolderInExplorer, CloseFolderInExplorer,
                () => { if (_settings.Editor.Explorer.PanelVisible) SetExplorerVisible(true); }),
```

to:

```csharp
            new ExplorerActions(ToggleExplorer, OpenFolderInExplorer, CloseFolderInExplorer),
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add Volt/UI/CommandPaletteCommands.cs Volt/UI/MainWindow.xaml.cs
git commit -m "refactor: remove PanelSide command, simplify ExplorerActions"
```

---

### Task 12: Full Build and Test Verification

**Files:** None (verification only)

- [ ] **Step 1: Clean build**

Run: `dotnet build Volt.sln --no-incremental`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test Volt.Tests --verbosity normal`
Expected: All tests pass (existing 67 + new 8 PanelShell tests = 75)

- [ ] **Step 3: Manual smoke test checklist**

Run the app: `dotnet run --project Volt/Volt.csproj`

Verify:
1. App launches without errors
2. Ctrl+B toggles the file explorer panel on the left
3. Explorer panel is resizable via the splitter
4. Opening a folder works and shows files
5. Closing the app and reopening restores the panel state (visible/hidden, width)
6. Settings window opens without errors (no PanelSide option visible)
7. Command palette works (no "Explorer: Panel Side" option, toggle/open/close folder work)
8. Right-click context menus in explorer still work
9. Projects still work (new, open, save, close)

- [ ] **Step 4: Commit any remaining fixes if needed**

- [ ] **Step 5: Final commit with all files**

```bash
git add -A
git commit -m "feat: complete panel infrastructure — generic dockable panel system"
```
