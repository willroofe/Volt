# Panel Drag-to-Dock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add drag-to-dock functionality so users can drag a panel by its header to reposition it on any edge of the center zone.

**Architecture:** PanelShell wraps each registered panel in a `PanelContainer` (header + content). The header is the drag source. During drag, PanelShell shows four edge overlay borders on the center cell. Dropping on a highlighted edge calls `MovePanel`. FileExplorerPanel's built-in header is removed since PanelContainer provides one.

**Tech Stack:** WPF (.NET 10), C# 13

---

## File Structure

| File | Role |
|------|------|
| **Create:** `Volt/UI/Panels/PanelContainer.cs` | Wraps panel in header + content, handles drag initiation |
| **Modify:** `Volt/UI/Panels/IPanel.cs` | Add `event Action? TitleChanged` to interface |
| **Modify:** `Volt/UI/Panels/PanelShell.xaml` | Add four drop zone overlay Borders in center cell |
| **Modify:** `Volt/UI/Panels/PanelShell.xaml.cs` | Drag state machine, overlay logic, wrap panels in PanelContainer |
| **Modify:** `Volt/UI/FileExplorerPanel.xaml` | Remove built-in header bar |
| **Modify:** `Volt/UI/FileExplorerPanel.xaml.cs` | Remove `HeaderText` refs, make `Title` dynamic, fire `TitleChanged` |
| **Modify:** `Volt.Tests/PanelShellTests.cs` | Update tests for PanelContainer wrapping |

---

### Task 1: Add TitleChanged to IPanel and Update FileExplorerPanel

**Files:**
- Modify: `Volt/UI/Panels/IPanel.cs`
- Modify: `Volt/UI/FileExplorerPanel.xaml`
- Modify: `Volt/UI/FileExplorerPanel.xaml.cs`

- [ ] **Step 1: Add TitleChanged event to IPanel**

In `Volt/UI/Panels/IPanel.cs`, change `IPanel` to:

```csharp
using System.Windows;

namespace Volt;

public interface IPanel
{
    string PanelId { get; }
    string Title { get; }
    UIElement Content { get; }
    event Action? TitleChanged;
}

public enum PanelPlacement
{
    Left,
    Right,
    Top,
    Bottom
}
```

- [ ] **Step 2: Make FileExplorerPanel Title dynamic and remove header from XAML**

In `Volt/UI/FileExplorerPanel.xaml`, replace the entire contents with:

```xml
<UserControl x:Class="Volt.FileExplorerPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:Volt">
    <ScrollViewer x:Name="TreeScrollViewer"
                  HorizontalScrollBarVisibility="Disabled"
                  VerticalScrollBarVisibility="Auto"
                  CanContentScroll="True"
                  Focusable="False"
                  Background="{DynamicResource ThemeExplorerBg}"
                  Template="{StaticResource ThemedScrollViewer}">
        <local:ExplorerTreeControl x:Name="ExplorerTree"/>
    </ScrollViewer>
</UserControl>
```

This removes the outer `DockPanel`, the header `Border` with `HeaderText`, and the `DockPanel.Dock="Top"` structure. The panel is now just the tree scroll viewer — the header is provided by `PanelContainer`.

- [ ] **Step 3: Update FileExplorerPanel.xaml.cs for dynamic title**

Replace the `Title` property and add backing field + `TitleChanged` event. Also replace all `HeaderText.Text = ...` lines with `SetTitle(...)` calls.

Change the IPanel properties (lines 24-26) from:

```csharp
    public string PanelId => "file-explorer";
    public string Title => "Explorer";
    public new UIElement Content => this;
```

to:

```csharp
    public string PanelId => "file-explorer";
    public string Title => _title;
    public new UIElement Content => this;
    public event Action? TitleChanged;

    private string _title = "Explorer";

    private void SetTitle(string title)
    {
        _title = title;
        TitleChanged?.Invoke();
    }
```

Then replace the four `HeaderText.Text = ...` references:

- Line 45: `HeaderText.Text = Path.GetFileName(path);` → `SetTitle(Path.GetFileName(path));`
- Line 59: `HeaderText.Text = "Explorer";` → `SetTitle("Explorer");`
- Line 67: `HeaderText.Text = "Explorer";` → `SetTitle("Explorer");`
- Line 75: `HeaderText.Text = "Explorer";` → `SetTitle("Explorer");`

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded (no other files reference `HeaderText`)

- [ ] **Step 5: Commit**

```bash
git add Volt/UI/Panels/IPanel.cs Volt/UI/FileExplorerPanel.xaml Volt/UI/FileExplorerPanel.xaml.cs
git commit -m "feat: add TitleChanged to IPanel, make explorer title dynamic, remove built-in header

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Create PanelContainer

**Files:**
- Create: `Volt/UI/Panels/PanelContainer.cs`

- [ ] **Step 1: Create PanelContainer**

This is a `DockPanel` subclass that wraps a panel's content with a draggable header bar. It raises a `DragStarted` event when the user drags the header past a 5px dead zone.

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public class PanelContainer : DockPanel
{
    private readonly IPanel _panel;
    private readonly TextBlock _titleText;
    private Point? _dragStartPoint;

    private const double DragDeadZone = 5;

    /// <summary>Raised when the user drags the header past the dead zone. Parameter is the panel ID.</summary>
    public event Action<string>? DragStarted;

    public PanelContainer(IPanel panel)
    {
        _panel = panel;

        // Header bar
        var header = new Border
        {
            Height = 33,
            Background = (Brush)Application.Current.Resources["ThemeExplorerHeaderBg"],
            BorderBrush = (Brush)Application.Current.Resources["ThemeTabBorder"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.SizeAll
        };

        _titleText = new TextBlock
        {
            Text = panel.Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };

        // Bind foreground dynamically
        _titleText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeExplorerHeaderFg");
        header.SetResourceReference(Border.BackgroundProperty, "ThemeExplorerHeaderBg");
        header.SetResourceReference(Border.BorderBrushProperty, "ThemeTabBorder");

        header.Child = _titleText;
        header.MouseLeftButtonDown += OnHeaderMouseDown;
        header.MouseMove += OnHeaderMouseMove;
        header.MouseLeftButtonUp += OnHeaderMouseUp;

        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(panel.Content);

        panel.TitleChanged += () => _titleText.Text = panel.Title;
    }

    public string PanelId => _panel.PanelId;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartPoint == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelTracking(sender);
            return;
        }

        var pos = e.GetPosition(this);
        var delta = pos - _dragStartPoint.Value;
        if (Math.Abs(delta.X) > DragDeadZone || Math.Abs(delta.Y) > DragDeadZone)
        {
            CancelTracking(sender);
            DragStarted?.Invoke(_panel.PanelId);
        }
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        CancelTracking(sender);
    }

    private void CancelTracking(object sender)
    {
        _dragStartPoint = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Panels/PanelContainer.cs
git commit -m "feat: add PanelContainer with draggable header and drag-start event

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Add Drop Zone Overlays to PanelShell XAML

**Files:**
- Modify: `Volt/UI/Panels/PanelShell.xaml`

- [ ] **Step 1: Add four overlay borders inside the center cell**

In `PanelShell.xaml`, after the `CenterPresenter` ContentPresenter (line 39) and before the Right GridSplitter comment, add four overlay borders. These share the center cell (Grid.Row="2" Grid.Column="2") and are positioned using alignment:

Replace:

```xml
        <!-- Center -->
        <ContentPresenter x:Name="CenterPresenter" Grid.Row="2" Grid.Column="2"
                          Content="{Binding CenterContent, RelativeSource={RelativeSource AncestorType=local:PanelShell}}"/>

        <!-- Right region -->
```

with:

```xml
        <!-- Center -->
        <ContentPresenter x:Name="CenterPresenter" Grid.Row="2" Grid.Column="2"
                          Content="{Binding CenterContent, RelativeSource={RelativeSource AncestorType=local:PanelShell}}"/>

        <!-- Drop zone overlays (center cell) -->
        <Border x:Name="DropLeft" Grid.Row="2" Grid.Column="2"
                Width="40" HorizontalAlignment="Left" VerticalAlignment="Stretch"
                Background="Transparent" IsHitTestVisible="False"
                Visibility="Collapsed"/>
        <Border x:Name="DropRight" Grid.Row="2" Grid.Column="2"
                Width="40" HorizontalAlignment="Right" VerticalAlignment="Stretch"
                Background="Transparent" IsHitTestVisible="False"
                Visibility="Collapsed"/>
        <Border x:Name="DropTop" Grid.Row="2" Grid.Column="2"
                Height="40" HorizontalAlignment="Stretch" VerticalAlignment="Top"
                Background="Transparent" IsHitTestVisible="False"
                Visibility="Collapsed"/>
        <Border x:Name="DropBottom" Grid.Row="2" Grid.Column="2"
                Height="40" HorizontalAlignment="Stretch" VerticalAlignment="Bottom"
                Background="Transparent" IsHitTestVisible="False"
                Visibility="Collapsed"/>

        <!-- Right region -->
```

Note: `IsHitTestVisible="False"` — hit testing is done in code via mouse position math, not by the overlays themselves. They're purely visual feedback.

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Panels/PanelShell.xaml
git commit -m "feat: add drop zone overlay borders to PanelShell center cell

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Implement Drag State Machine in PanelShell

**Files:**
- Modify: `Volt/UI/Panels/PanelShell.xaml.cs`

This is the core task. PanelShell needs to:
1. Wrap panels in PanelContainer on registration
2. Handle `DragStarted` from PanelContainer
3. Track mouse during drag, highlight edge overlays
4. Handle drop (mouse up) and cancel (Escape)

- [ ] **Step 1: Add drag state fields and drop zone helper**

Add these fields after the `_panels` dictionary (line 9):

```csharp
    private string? _draggingPanelId;
    private PanelPlacement? _highlightedZone;
```

Add a helper to get the drop overlay border:

```csharp
    private Border GetDropOverlay(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => DropLeft,
        PanelPlacement.Right => DropRight,
        PanelPlacement.Top => DropTop,
        PanelPlacement.Bottom => DropBottom,
        _ => throw new ArgumentOutOfRangeException(nameof(placement))
    };
```

- [ ] **Step 2: Modify RegisterPanel to wrap in PanelContainer**

Replace the `RegisterPanel` method:

```csharp
    public void RegisterPanel(IPanel panel, PanelPlacement placement, double defaultSize)
    {
        var container = new PanelContainer(panel);
        container.DragStarted += OnPanelDragStarted;
        var reg = new PanelRegistration(panel, placement, defaultSize, container);
        _panels[panel.PanelId] = reg;
        PlacePanel(reg);
    }
```

Update `PlacePanel` to use the container:

```csharp
    private void PlacePanel(PanelRegistration reg)
    {
        var presenter = GetContentPresenter(reg.Placement);
        presenter.Content = reg.Container;
    }
```

Update `PanelRegistration` to include the container:

```csharp
    private class PanelRegistration(IPanel panel, PanelPlacement placement, double size, PanelContainer container)
    {
        public IPanel Panel { get; } = panel;
        public PanelContainer Container { get; } = container;
        public PanelPlacement Placement { get; set; } = placement;
        public double Size { get; set; } = size;
        public bool IsVisible { get; set; }
    }
```

- [ ] **Step 3: Implement drag start handler**

```csharp
    private void OnPanelDragStarted(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (!reg.IsVisible) return;

        _draggingPanelId = panelId;
        _highlightedZone = null;

        // Show overlays (except the panel's current edge)
        foreach (PanelPlacement p in Enum.GetValues<PanelPlacement>())
        {
            var overlay = GetDropOverlay(p);
            if (p == reg.Placement)
            {
                overlay.Visibility = Visibility.Collapsed;
            }
            else
            {
                overlay.Background = System.Windows.Media.Brushes.Transparent;
                overlay.Visibility = Visibility.Visible;
            }
        }

        CaptureMouse();
        Cursor = System.Windows.Input.Cursors.SizeAll;
    }
```

- [ ] **Step 4: Override mouse event handlers for drag**

Add these overrides to `PanelShell`:

```csharp
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingPanelId == null) return;

        var pos = e.GetPosition(CenterPresenter);
        var size = CenterPresenter.RenderSize;

        PanelPlacement? zone = null;
        const double edgeDepth = 40;

        if (size.Width > 0 && size.Height > 0 &&
            pos.X >= 0 && pos.X <= size.Width && pos.Y >= 0 && pos.Y <= size.Height)
        {
            // Check which edge the mouse is closest to, within edgeDepth
            if (pos.X < edgeDepth) zone = PanelPlacement.Left;
            else if (pos.X > size.Width - edgeDepth) zone = PanelPlacement.Right;
            else if (pos.Y < edgeDepth) zone = PanelPlacement.Top;
            else if (pos.Y > size.Height - edgeDepth) zone = PanelPlacement.Bottom;
        }

        // Exclude current placement
        if (_panels.TryGetValue(_draggingPanelId, out var reg) && zone == reg.Placement)
            zone = null;

        if (zone != _highlightedZone)
        {
            // Clear old highlight
            if (_highlightedZone.HasValue)
                GetDropOverlay(_highlightedZone.Value).Background = System.Windows.Media.Brushes.Transparent;

            // Set new highlight
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

        // Hide all overlays
        foreach (PanelPlacement p in Enum.GetValues<PanelPlacement>())
        {
            var overlay = GetDropOverlay(p);
            overlay.Background = System.Windows.Media.Brushes.Transparent;
            overlay.Visibility = Visibility.Collapsed;
        }
    }
```

- [ ] **Step 5: Verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeded

- [ ] **Step 6: Run existing tests**

Run: `dotnet test Volt.Tests --filter PanelShell --verbosity normal`
Expected: Tests may need updating due to `PanelRegistration` constructor change — the `FakePanel` needs to implement `TitleChanged`. Fix in next task if needed.

- [ ] **Step 7: Commit**

```bash
git add Volt/UI/Panels/PanelShell.xaml.cs
git commit -m "feat: implement drag state machine with edge detection and drop zone highlights

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Fix Tests for PanelContainer Wrapping

**Files:**
- Modify: `Volt.Tests/PanelShellTests.cs`

- [ ] **Step 1: Add TitleChanged event to FakePanel**

The `FakePanel` class needs to implement the new `TitleChanged` event. Update it:

```csharp
    private sealed class FakePanel(string id, string title) : IPanel
    {
        public string PanelId => id;
        public string Title => title;
        public UIElement Content { get; } = new System.Windows.Controls.Border();
        public event Action? TitleChanged;
    }
```

The compiler may warn that `TitleChanged` is never used — that's fine for a test fake. If the warning is noisy, suppress it:

```csharp
    private sealed class FakePanel(string id, string title) : IPanel
    {
        public string PanelId => id;
        public string Title => title;
        public UIElement Content { get; } = new System.Windows.Controls.Border();
#pragma warning disable CS0067
        public event Action? TitleChanged;
#pragma warning restore CS0067
    }
```

- [ ] **Step 2: Run tests**

Run: `dotnet test Volt.Tests --filter PanelShell --verbosity normal`
Expected: All 9 tests pass

- [ ] **Step 3: Commit**

```bash
git add Volt.Tests/PanelShellTests.cs
git commit -m "test: update FakePanel to implement TitleChanged event

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Full Build and Test Verification

**Files:** None (verification only)

- [ ] **Step 1: Clean build**

Run: `dotnet build Volt.sln --no-incremental`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test Volt.Tests --verbosity normal`
Expected: All tests pass (76 total)

- [ ] **Step 3: Manual smoke test**

Run: `dotnet run --project Volt/Volt.csproj`

Verify:
1. App launches, explorer panel shows with header bar (now provided by PanelContainer)
2. Header shows "Explorer" text with correct styling (same as before)
3. Opening a folder changes the header text to the folder name
4. Click and drag on the header — after 5px movement, cursor changes to SizeAll
5. Dragging over the right edge of the editor area shows a semi-transparent highlight strip
6. Dropping on a highlighted edge moves the panel to that side
7. Dragging to current side does not show a highlight (no-op)
8. Pressing Escape during drag cancels it
9. Releasing mouse over center (no edge) cancels the drag
10. Ctrl+B still toggles the panel
11. Panel resize via splitter still works
12. Panel position persists across app restart

- [ ] **Step 4: Commit any fixes if needed**

```bash
git add -A
git commit -m "feat: complete panel drag-to-dock functionality

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```
