# File Explorer Panel Design

## Overview

Add a file explorer panel to Volt that lets users browse folder contents in a tree view. The panel is togglable, resizable, and configurable to appear on the left or right side of the editor.

## Layout

Replace the current single `EditorHost` area in MainWindow's DockPanel with a three-column Grid:

| Column 0 | Column 1 | Column 2 |
|-----------|----------|----------|
| FileExplorerPanel | GridSplitter (3px) | Editor area (tabs + editor) |

When panel side is "Right", the order reverses (editor col 0, splitter col 1, explorer col 2).

- Panel starts hidden (column width 0, splitter collapsed)
- Panel width is resizable via GridSplitter drag, persisted in settings
- Default width: 250px

## FileExplorerPanel (UserControl)

**New files:** `UI/FileExplorerPanel.xaml` + `UI/FileExplorerPanel.cs`

### Structure

- Header bar (~28px): displays folder name or "Explorer" when empty
- WPF TreeView with `VirtualizingStackPanel.IsVirtualizing="True"`

### Data Model — FileTreeItem

Properties:
- `Name` (string) — display name
- `FullPath` (string) — absolute path
- `IsDirectory` (bool)
- `IsExpanded` (bool) — bound to TreeViewItem
- `Children` (ObservableCollection<FileTreeItem>)

Unexpanded directories contain a single dummy placeholder child so the expand arrow renders. On first expand, the placeholder is replaced with real children loaded from disk.

### Lazy Loading

On folder expand:
1. Enumerate immediate children via `Directory.GetDirectories` / `Directory.GetFiles` on a background thread
2. Marshal results to UI thread
3. Sort: directories first, then files, alphabetical within each group
4. Ignore hidden entries (names starting with `.`) and hardcoded noise directories: `node_modules`, `bin`, `obj`, `.git`, `__pycache__`, `.vs`

### Interaction

- Single-click: selects item in tree (default TreeView behavior, no file action)
- Double-click file: raises `FileOpenRequested(string path)` event
- Double-click folder: toggles expand/collapse
- No context menu (deferred to future work)

### Public Interface

- `void OpenFolder(string path)` — sets the root and populates top-level children
- `void CloseFolder()` — clears the tree
- `event Action<string>? FileOpenRequested` — MainWindow subscribes to open/switch-to file

## Settings

### New Classes in AppSettings.cs

```csharp
public class ExplorerSettings
{
    public string PanelSide { get; set; } = "Left";
    public double PanelWidth { get; set; } = 250;
    public bool PanelVisible { get; set; } = false;
    public string? OpenFolderPath { get; set; }
}
```

Added to `EditorSettings`:
```csharp
public ExplorerSettings Explorer { get; set; } = new();
```

Static options:
```csharp
public static readonly string[] PanelSideOptions = ["Left", "Right"];
```

### Persisted Across Sessions

- `PanelVisible` — whether the panel is showing
- `OpenFolderPath` — the folder currently open in the explorer
- `PanelWidth` — saved on GridSplitter drag complete
- `PanelSide` — left or right

### Settings Window

New "Explorer" nav button under the EDITOR section in the left column. Content section with:
- Panel side ComboBox (Left / Right)

## Keyboard Shortcuts & Commands

- **Ctrl+B** — toggle file explorer visibility
- Command palette entries:
  - "Toggle File Explorer" — same as Ctrl+B
  - "Explorer: Open Folder..." — opens FolderBrowserDialog, sets root
  - "Explorer: Close Folder" — clears tree and hides panel
  - "Explorer: Panel Side: Left" / "Explorer: Panel Side: Right" — with preview/commit/revert

## Theme Integration

### New Color Keys

| Key | Purpose | Dark default | Light default |
|-----|---------|-------------|--------------|
| `ThemeExplorerBg` | Panel background | same as editor bg | same as editor bg |
| `ThemeExplorerHeaderBg` | Header bar bg | slightly lighter than editor bg | slightly darker |
| `ThemeExplorerHeaderFg` | Header text | muted foreground | muted foreground |
| `ThemeExplorerItemHoverBg` | Hovered tree item | subtle highlight | subtle highlight |
| `ThemeExplorerItemSelectedBg` | Selected tree item | accent-based highlight | accent-based highlight |

### Files Modified

1. `Theme/ColorTheme.cs` — add `explorer` section to `ChromeColors`
2. `Theme/ThemeManager.cs` — map new keys in `UpdateAppResources()`
3. `App.xaml` — default resource values for new keys
4. Theme JSON files: `default-dark.json`, `default-light.json`, `gruvbox-dark.json`

### Visual Treatment

- TreeView `ItemContainerStyle` uses `{DynamicResource}` for all colors
- File/folder indicators are text-based: `>` / `v` for folders, no file icon (icons can be added later)
- Styled TreeView item template with folder/file name text

## Files Changed

### New Files
- `Volt/UI/FileExplorerPanel.xaml`
- `Volt/UI/FileExplorerPanel.cs`

### Modified Files
- `Volt/AppSettings.cs` — ExplorerSettings class, added to EditorSettings
- `Volt/UI/MainWindow.xaml` — three-column Grid layout replacing EditorHost area
- `Volt/UI/MainWindow.xaml.cs` — panel toggle, open/close folder, GridSplitter persistence, session restore of panel state
- `Volt/UI/SettingsWindow.xaml` — Explorer section
- `Volt/UI/SettingsWindow.xaml.cs` — Explorer settings binding (via SettingsSnapshot)
- `Volt/UI/CommandPaletteCommands.cs` — new explorer commands
- `Volt/Theme/ColorTheme.cs` — explorer color keys
- `Volt/Theme/ThemeManager.cs` — explorer resource mapping
- `Volt/App.xaml` — default explorer resource values
- `Volt/Resources/Themes/default-dark.json` — explorer colors
- `Volt/Resources/Themes/default-light.json` — explorer colors
- `Volt/Resources/Themes/gruvbox-dark.json` — explorer colors

## Future Work (Out of Scope)

- Right-click context menu (New File, Rename, Delete, etc.)
- Multi-root workspaces
- File icons (per extension)
- Custom-rendered panel (approach 1) if TreeView isn't sufficient
- Configurable ignore patterns
- File search within explorer
