# Panel Drag-to-Dock Design

**Date:** 2026-04-03
**Status:** Approved

## Overview

Add drag-to-dock functionality to the panel system, allowing users to drag a panel by its header to reposition it on any of the four edges (left, right, top, bottom) of the center editor zone. Builds on the existing `PanelShell.MovePanel` API.

## Drag Interaction

**Initiation:** Mouse down + drag on the panel's header bar. A 5px dead zone before drag activates to prevent accidental drags on clicks.

**During drag:** Cursor changes to indicate dragging. As the mouse moves over the center zone's edges, a semi-transparent highlight strip appears on that edge (~40px deep, full edge length). Only one edge highlights at a time based on proximity.

**Drop:** Mouse up while an edge is highlighted calls `Shell.MovePanel(panelId, newPlacement)`. Mouse up with no edge highlighted cancels (panel stays put). Dragging to the panel's current placement is a no-op.

**Cancel:** Pressing Escape during drag cancels it. All overlays hide and state resets.

## PanelContainer (Drag Source)

PanelShell wraps each registered panel's `Content` in a `PanelContainer` — a lightweight element providing:

- A draggable header bar showing `IPanel.Title` (33px tall, matching current explorer header styling)
- The panel content below the header

This centralizes drag-source logic in the shell so any panel gets drag-to-dock for free by implementing `IPanel`.

**Header styling:** Uses `ThemeExplorerHeaderBg` / `ThemeExplorerHeaderFg` for background/foreground, `ThemeTabBorder` for the bottom border, `Segoe UI` 11pt SemiBold — matching the current `FileExplorerPanel` header exactly.

**Drag activation flow:**
1. `MouseLeftButtonDown` on header — record start position, begin tracking
2. `MouseMove` — if mouse has moved >5px from start position, activate drag: raise event to PanelShell with the panel ID
3. `MouseLeftButtonUp` before threshold — cancel tracking (was just a click)

Once drag is activated, PanelShell takes over (captures mouse on itself, shows overlays, handles drop).

**FileExplorerPanel header removal:** Since PanelContainer provides the header, `FileExplorerPanel.xaml` removes its built-in header bar (`DockPanel.Dock="Top"` border with `HeaderText`). The `HeaderText` field and references in the code-behind (`HeaderText.Text = ...`) are replaced — the panel's `Title` property (from `IPanel`) now controls the displayed name. For dynamic titles (e.g., showing the project name), `IPanel.Title` can be updated and the container notified.

## Drop Zone Overlays

Four `Border` elements positioned at the edges of the center cell (row 2, col 2) in the PanelShell grid. Always present in XAML, `Visibility="Collapsed"` until a drag starts.

**Layout:**
- Left overlay: 40px wide, full height, aligned left
- Right overlay: 40px wide, full height, aligned right
- Top overlay: full width, 40px tall, aligned top
- Bottom overlay: full width, 40px tall, aligned bottom

**Visual:** `ThemeTextFg` at 20% opacity fill when the mouse is within that zone. Transparent otherwise.

**Exclusion:** The overlay corresponding to the panel's current placement stays hidden during that panel's drag — no point highlighting a no-op target.

## Drag State Machine (PanelShell)

```
Idle
  |-- PanelContainer raises DragStarted(panelId)
  v
Dragging
  |-- MouseMove: hit-test edges, update overlay highlights
  |-- MouseUp on highlighted edge: MovePanel(), go to Idle
  |-- MouseUp on no edge: cancel, go to Idle
  |-- Escape key: cancel, go to Idle
```

PanelShell captures the mouse on itself during drag so mouse events are received even if the cursor leaves the window briefly.

## Dynamic Title Support

Since `FileExplorerPanel` currently sets `HeaderText.Text` dynamically (e.g., to the project name or folder name), and we're removing the built-in header, we need a way for panels to update their displayed title.

Approach: Add a `TitleChanged` event to `IPanel` (or make `PanelContainer` observe a property). Simplest: `PanelContainer` binds its header text to a `Title` property that the panel can update. Since `IPanel.Title` is a get-only property, the container reads it at registration time. For dynamic updates, add an `event Action? TitleChanged` to `IPanel` — when fired, the container re-reads `Title`.

## File Changes

### New Files

| File | Purpose |
|------|---------|
| `Volt/UI/Panels/PanelContainer.cs` | Wraps panel content with draggable header, raises drag-start event |

### Modified Files

| File | Changes |
|------|---------|
| `Volt/UI/Panels/IPanel.cs` | Add `event Action? TitleChanged` to interface |
| `Volt/UI/Panels/PanelShell.xaml` | Add four overlay Borders in the center cell |
| `Volt/UI/Panels/PanelShell.xaml.cs` | Drag state machine, overlay logic, wrap panels in PanelContainer on register |
| `Volt/UI/FileExplorerPanel.xaml` | Remove built-in header bar |
| `Volt/UI/FileExplorerPanel.xaml.cs` | Remove `HeaderText` references, implement `TitleChanged`, make `Title` dynamic |

### Unchanged

`PanelSlotConfig`, `AppSettings`, `MainWindow.xaml`, `CommandPaletteCommands`, `EditorControl`, all editor internals.
