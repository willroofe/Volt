# Tabbed Panel Regions Design

## Goal

Evolve the panel system from one-panel-per-region to tabbed regions, where each dock region (left, right, top, bottom) can hold multiple panels as tabs, and empty regions display a "+" button for adding panels.

## Context

The current panel system (`PanelShell`) supports four dock regions around a center editor zone. Each region holds at most one panel via `PanelContainer`, which wraps an `IPanel` with a draggable header bar. Panels can be shown/hidden, resized via splitters, dragged between regions, and their layout is persisted via `PanelSlotConfig`.

This design extends the system so regions can hold multiple panels as tabs, with a dedicated `TabRegion` control managing the tab strip, active tab switching, and a "+" button for adding panels from a dropdown menu.

## Architecture

**Approach: New TabRegion wrapper around PanelContainers.** PanelContainer simplifies to a thin content wrapper (header/drag logic moves to TabRegion). A new `TabRegion` control manages the tab strip, "+" button, and content switching. PanelShell manages four persistent TabRegions instead of individual PanelContainers.

## Components

### TabRegion (`Volt/UI/Panels/TabRegion.xaml/.cs`)

A `UserControl` placed in each dock region by PanelShell. Two visual states:

**Empty state:** A 34px header bar with only a "+" button aligned right. No content area below. Shown when the region is toggled visible but has no panels.

**Populated state:** A 34px header bar containing:
- A horizontal tab strip (left-aligned, scrollable if many tabs)
- A "+" button docked to the right (same style as the editor's new-tab button)

Below the header, the active panel's `Content` UIElement fills the remaining space.

**Tab items** display the panel's `Title` text. They subscribe to `IPanel.TitleChanged` for dynamic title updates.

**Tab interactions:**
- Click a tab to switch the active panel
- Right-click a tab to show a context menu with "Close" (styled via `ContextMenuHelper`)
- Mouse-down + drag on a tab initiates panel drag (forwarded as `DragStarted` event, same dead-zone pattern as current PanelContainer)

**"+" button** opens a dropdown menu (styled like existing context menus via `ContextMenuHelper`) listing all registered panels not currently visible in any region. Selecting one adds it as a new tab in this region.

**Theming:** Header uses the same dynamic resource keys as the current PanelContainer header (`ThemeExplorerHeaderBg`, `ThemeExplorerHeaderFg`, `ThemeTabBorder`). Tab items use theme-appropriate hover/selected highlights.

**Events raised:**
- `PanelAdded(string panelId)` — a panel was added to this region (via "+" or programmatically)
- `PanelClosed(string panelId)` — a panel tab was closed via context menu
- `PanelDragStarted(string panelId)` — user started dragging a tab
- `ActiveTabChanged(string panelId)` — the active tab was switched

**Public API:**
- `AddPanel(PanelContainer container)` — add a panel as a new tab
- `RemovePanel(string panelId)` — remove a panel tab
- `SetActiveTab(string panelId)` — switch active tab
- `string? ActivePanelId { get; }` — currently active panel ID, or null if empty
- `IReadOnlyList<string> PanelIds { get; }` — ordered list of panel IDs in tab order
- `int TabCount { get; }` — number of tabs
- `bool IsEmpty => TabCount == 0`

### PanelContainer (`Volt/UI/Panels/PanelContainer.cs`)

Simplifies from its current form. Changes:

- **Remove** the header bar (Border with title text). Title display moves to TabRegion tab items.
- **Remove** the drag-start mouse handling (OnHeaderMouseDown/Move/Up, CancelTracking). Drag initiation moves to TabRegion tab items.
- **Remove** the `DragStarted` event.
- **Keep** the `IPanel` reference, `PanelId` property, and the panel's `Content` as the child element.
- Becomes a thin wrapper: holds the `IPanel` and exposes its content as a `UIElement`.

### PanelShell (`Volt/UI/Panels/PanelShell.xaml.cs`)

Changes from managing individual PanelContainers to managing four persistent TabRegions:

**Construction:** Creates four `TabRegion` instances (one per `PanelPlacement`) and places them in the ContentPresenters. Subscribes to each TabRegion's events.

**Panel registry:** `RegisterPanel` stores the panel registration (IPanel, default placement, default size, PanelContainer) in a dictionary but does not immediately place it in a TabRegion. Panels are added to their TabRegion when `ShowPanel` is called or via the "+" picker.

**ShowPanel(panelId):** Adds the panel as a tab in its assigned region's TabRegion. Makes the region visible (sizes grid row/column to the stored size, shows splitter).

**HidePanel(panelId):** Removes the panel's tab from its TabRegion. If the TabRegion becomes empty, collapses the region (sizes to 0, hides splitter).

**ToggleRegion(placement):** If the region is visible, collapses it — all panel tabs are hidden (their `Visible` flag set to false) but they retain their placement assignment, so restoring the region re-adds them. If collapsed and has panels assigned, restores them as visible tabs. If collapsed and no panels assigned, shows the empty "+" state.

**Drag-to-dock:** When dropping a panel onto a region that already has tabs, adds it as a new tab in that region (rather than replacing). The panel is removed from its source region first.

**MovePanel(panelId, newPlacement):** Removes the panel from its current TabRegion and adds it to the target TabRegion.

**"+" menu population:** TabRegion asks PanelShell (via event or callback) for the list of available panels. PanelShell returns all registered panels that are not currently visible in any region.

**GetCurrentLayout / RestoreLayout:** Updated to handle multiple panels per region, tab order, and active tab state.

### PanelSlotConfig (`Volt/UI/Panels/PanelSlotConfig.cs`)

Two new fields:

```csharp
public int TabIndex { get; set; }      // order within the region's tab strip
public bool IsActiveTab { get; set; }   // which tab is selected in the region
```

On save, each panel writes its current tab position and active state. On restore, panels are added to their TabRegion sorted by `TabIndex`, and the one with `IsActiveTab = true` is selected.

### MainWindow

- `RegisterPanel` calls unchanged (API stays the same)
- `ToggleRegion` already wired to Ctrl+B / Ctrl+Alt+B from earlier work
- `ToggleExplorer` (command palette) continues to call `Shell.TogglePanel("file-explorer")`
- `RestorePanelLayout` / `OnPanelLayoutChanged` updated to handle new `TabIndex` and `IsActiveTab` fields

### IPanel

No changes. Interface stays the same.

## Behavior Details

### Region Lifecycle

1. **App starts** — all regions collapsed (grid size 0)
2. **RestoreLayout** — panels added to their regions per saved config; regions with visible panels become visible
3. **User clicks "+"** — dropdown shows available panels; selecting one adds it as a tab, region becomes visible if it wasn't
4. **User closes last tab** — region collapses automatically
5. **User presses Ctrl+B** — if left region visible, collapses it (tabs remembered); if collapsed, restores (or shows empty "+" state if no tabs)
6. **User drags panel tab to another region** — panel removed from source region (source collapses if now empty), added as tab in target region

### Tab Close via Context Menu

Right-clicking a tab shows a context menu with "Close". Closing a tab:
1. Removes the panel from the TabRegion
2. Panel's `Visible` is set to false in the registry
3. If the closed tab was active, the next tab (or previous if last) becomes active
4. If no tabs remain, the region collapses
5. `PanelLayoutChanged` fires for persistence

### "+" Button Dropdown

The dropdown lists all registered panels not currently visible. Each item shows the panel's `Title`. Selecting an item:
1. Creates/retrieves the PanelContainer for that panel
2. Adds it as a tab in this region
3. Sets it as the active tab
4. Updates the panel's placement to this region
5. `PanelLayoutChanged` fires for persistence

## File Changes Summary

| File | Action | Description |
|------|--------|-------------|
| `Volt/UI/Panels/TabRegion.xaml` | Create | Tab strip + "+" button + content area layout |
| `Volt/UI/Panels/TabRegion.cs` | Create | Tab management, active tab switching, drag forwarding, context menu |
| `Volt/UI/Panels/PanelContainer.cs` | Modify | Remove header bar and drag handling; simplify to content wrapper |
| `Volt/UI/Panels/PanelShell.xaml.cs` | Modify | Manage TabRegions instead of PanelContainers; update show/hide/drag/restore |
| `Volt/UI/Panels/PanelSlotConfig.cs` | Modify | Add `TabIndex` and `IsActiveTab` fields |
| `Volt/UI/MainWindow.xaml.cs` | Modify | Update restore/save for new fields |
| `Volt.Tests/PanelShellTests.cs` | Modify | Update tests for multi-tab behavior |
