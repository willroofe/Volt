# Panel Infrastructure Design

**Date:** 2026-04-03
**Status:** Approved

## Overview

Replace the hardcoded file explorer layout in MainWindow with a generic, dynamic panel system that supports docking panels on all four sides (left, right, top, bottom) around a center editor zone. This lays the groundwork for future features like editor splitting, terminal panels, search results, etc.

## Approach

**Shell Layout with Named Regions** — a `PanelShell` control owns a fixed layout structure with four dock regions around a center zone. Each region is a generic container that can hold any panel, with splitters and show/hide built in. MainWindow becomes thin — it tells the shell where to place panels.

```
+----------------------------------+
|           Top Region             |
|------+--------------------+------|
|      |                    |      |
| Left |   Center Zone     | Right|
|      |                    |      |
|------+--------------------+------|
|          Bottom Region           |
+----------------------------------+
```

## Panel Contracts

### IPanel Interface

```csharp
public interface IPanel
{
    string PanelId { get; }        // unique identifier, e.g. "file-explorer"
    string Title { get; }          // display name for headers, e.g. "Explorer"
    UIElement Content { get; }     // the control to render (typically `this`)
}
```

Panels don't know where they're docked — the shell decides that. `FileExplorerPanel` implements `IPanel` directly.

### PanelPlacement Enum

```csharp
public enum PanelPlacement { Left, Right, Top, Bottom }
```

### PanelSlotConfig

Record for persisting per-panel layout state:

```csharp
public record PanelSlotConfig
{
    public string PanelId { get; init; }
    public PanelPlacement Placement { get; init; }
    public double Size { get; init; }       // width for left/right, height for top/bottom
    public bool Visible { get; init; }
}
```

## PanelShell Control

A `UserControl` that replaces the current `MainContentGrid` in MainWindow.

### Layout

A single `Grid` with 5 rows x 5 columns:

- Row 0: Top region (spans full width)
- Row 1: Top splitter (1px GridSplitter)
- Row 2: Middle row containing [Left region | Left splitter | Center zone | Right splitter | Right region]
- Row 3: Bottom splitter (1px GridSplitter)
- Row 4: Bottom region (spans full width)

Each region is a `ContentPresenter`. Splitters are 1px `GridSplitter`s styled like the current `ExplorerSplitter`. Regions sized to 0 when empty or hidden; center zone always takes `*` space.

### Public API

```csharp
// Place a panel in a region
void RegisterPanel(IPanel panel, PanelPlacement placement, double defaultSize);

// Toggle visibility
void ShowPanel(string panelId);
void HidePanel(string panelId);

// Relocate a panel
void MovePanel(string panelId, PanelPlacement newPlacement);

// Center content — dependency property
UIElement CenterContent { get; set; }

// Fired on resize/move/toggle for persistence
event Action<string, PanelPlacement, double>? PanelLayoutChanged;
```

### Behavior

- `RegisterPanel` places the panel's `Content` into the appropriate region's `ContentPresenter` and sizes the corresponding row/column to `defaultSize`
- `ShowPanel`/`HidePanel` toggle the region's row/column size between 0 and the stored size, and toggle splitter visibility
- `MovePanel` removes from current region, places in new region
- Splitter `DragCompleted` fires `PanelLayoutChanged` with the new size
- Each region gets a border line between its header area and the center (generalizing the current `HeaderBorderBridge`)

## Settings Integration

### Changes to AppSettings

- **Remove** from `Explorer` settings: `PanelSide`, `PanelWidth`, `PanelVisible` (no migration — internal only)
- **Add** to `Editor` settings: `PanelLayouts` — a `List<PanelSlotConfig>` storing placement/size/visibility per panel ID
- Explorer-specific content state (`OpenFolderPath`, `ExpandedPaths`) stays in `AppSettings.Editor.Explorer`

### Persistence Flow

`PanelShell.PanelLayoutChanged` fires on splitter drag or show/hide -> MainWindow updates `_settings.Editor.PanelLayouts` -> saves. On startup, MainWindow reads `PanelLayouts` and restores panel states.

If `PanelLayouts` has no entry for a registered panel, the panel starts hidden at its default size.

## File Changes

### New Files

| File | Purpose |
|------|---------|
| `UI/Panels/IPanel.cs` | `IPanel` interface + `PanelPlacement` enum |
| `UI/Panels/PanelSlotConfig.cs` | Layout state record |
| `UI/Panels/PanelShell.xaml` + `.cs` | Shell control with grid layout, splitters, register/show/hide/move API |

### Modified Files

| File | Changes |
|------|---------|
| `UI/FileExplorerPanel.xaml.cs` | Implement `IPanel` (add `PanelId`, `Title`, `Content` properties) |
| `UI/MainWindow.xaml` | Replace 3-column `MainContentGrid` with `<local:PanelShell>`, move EditorArea inside as `CenterContent` |
| `UI/MainWindow.xaml.cs` | Replace `SetExplorerVisible` and manual column manipulation with `Shell.RegisterPanel`/`ShowPanel`/`HidePanel`. Remove FlowDirection hack. |
| `AppSettings.cs` | Remove `PanelSide`/`PanelWidth`/`PanelVisible`, add `PanelLayouts` list |
| `UI/CommandPaletteCommands.cs` | Update explorer toggle command to use shell API |

### Unchanged

`EditorControl`, `ExplorerTreeControl`, `FileTreeItem`, `ThemeManager`, and all editor internals. The center zone content (EditorArea DockPanel) stays as-is, hosted inside the shell.

## Future Extensions

This design directly supports:

- **Editor splitting:** Replace the center zone's single EditorArea with a recursive split container
- **New panel types:** Implement `IPanel`, call `Shell.RegisterPanel` — terminal, search results, git panel, etc.
- **Tabbed panels per region:** Extend the region container from `ContentPresenter` to a tabbed container holding multiple `IPanel`s
- **Drag-to-dock:** Add drag handles that call `MovePanel` based on drop target
