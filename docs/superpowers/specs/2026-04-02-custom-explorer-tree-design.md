# Custom Explorer Tree Control — Design Spec

## Goal

Replace the WPF `TreeView` in `FileExplorerPanel` with a custom `FrameworkElement` (`ExplorerTreeControl`) that renders the file/project tree via `DrawingContext`, giving full control over layout, rendering, hit-testing, and scrolling — consistent with `EditorControl`'s approach.

## Scope

**In scope:**
- Custom `ExplorerTreeControl` with `DrawingContext` rendering and `IScrollInfo`
- Fixed 24px row height, vertical-only scrolling
- Truncated names with tooltip on hover
- Mouse interaction: click to select, double-click to open files, click arrow to expand/collapse
- Hover and selection highlights with rounded corners
- Right-click selects item under cursor, delegates context menu to `FileExplorerPanel`
- Theme integration via `ThemeManager` (no new theme keys)
- Full support for all `FileTreeItemKind` types (File, Directory, VirtualFolder, ProjectRoot)

**Not in scope (future):**
- Keyboard navigation (arrow keys, Enter to open)
- Expand/collapse animation
- Custom-rendered context menus
- Drag-and-drop

## Architecture

```
FileExplorerPanel (UserControl — XAML shell, unchanged role)
├── Header bar (XAML — stays as-is)
└── ExplorerTreeControl (new custom FrameworkElement)
    ├── Implements IScrollInfo
    ├── Renders via DrawingContext
    ├── Reads brushes from ThemeManager
    └── Operates on a flat list of visible rows
```

### Files

- **Create:** `TextEdit/UI/ExplorerTreeControl.cs` — the custom control (~400-500 lines)
- **Modify:** `TextEdit/UI/FileExplorerPanel.xaml` — replace TreeView with ExplorerTreeControl, remove TreeViewItem styles and HierarchicalDataTemplate
- **Modify:** `TextEdit/UI/FileExplorerPanel.xaml.cs` — simplify to use new control's API instead of TreeView workarounds
- **Keep:** `TextEdit/UI/FileTreeItem.cs` — data model stays as-is

### Class: ExplorerTreeControl

A `FrameworkElement` implementing `IScrollInfo`. Owns rendering, hit-testing, mouse handling, and scroll logic.

**Public API:**

```csharp
// Data
void SetRootItems(ObservableCollection<FileTreeItem> items)
FileTreeItem? SelectedItem { get; }

// Events
event Action<string>? FileOpenRequested;       // double-click on file
event Action<FileTreeItem>? SelectionChanged;
event Action<FileTreeItem>? ItemRightClicked;  // right-click, item already selected

// Methods
void RefreshFlatList();  // call after external expand state changes
```

**Internal state:**

- `List<FlatRow> _flatRows` — pre-order traversal of expanded nodes
- `int _hoverRowIndex` — row under mouse, -1 if none
- `int _selectedRowIndex` — clicked row, -1 if none
- `double _verticalOffset` — scroll position in pixels

**FlatRow struct:**

```csharp
readonly record struct FlatRow(FileTreeItem Item, int Depth);
```

### Flat list management

The control maintains a flat list that is a pre-order traversal of all expanded nodes in the `FileTreeItem` tree. This list is rebuilt when:

- `SetRootItems()` is called (new data)
- A node is expanded or collapsed (user clicks arrow)
- `RefreshFlatList()` is called (external tree data change, e.g. project rebuild)

Rebuilding walks the tree recursively: for each item, add a `FlatRow`, then if `IsExpanded` and has children, recurse into children with `depth + 1`.

## Row Layout

Each row is a fixed **24px** height. Layout left to right:

| Zone | Width | Content |
|------|-------|---------|
| Indent | `depth * 20px` | Empty space |
| Arrow | 20px | Chevron glyph for expandable nodes, blank for files |
| Icon | 16px | Folder or file icon (Segoe MDL2 Assets) |
| Gap | 4px | Spacing |
| Name | Remaining width | Text, truncated with ellipsis via `FormattedText.MaxTextWidth` |

**Row styling by kind:**

| Kind | Font Weight | Font Style | Foreground | Icon |
|------|------------|------------|------------|------|
| ProjectRoot | SemiBold | Normal | `ThemeExplorerHeaderFg` | None (chevron only) |
| VirtualFolder | Normal | Italic | `ThemeExplorerHeaderFg` | None (chevron only) |
| Directory | Normal | Normal | `ThemeTextFg` | Folder glyph |
| File | Normal | Normal | `ThemeTextFg` | File glyph |

**Hover highlight:** Full-width rounded-corner rectangle using `ThemeExplorerItemHover` brush.

**Selection highlight:** Full-width rounded-corner rectangle using `ThemeExplorerItemSelected` brush, with a small horizontal margin and corner radius to match the app's style.

## Scrolling (IScrollInfo)

Follows `EditorControl`'s pattern:

- **Extent height:** `_flatRows.Count * RowHeight`
- **Extent width:** viewport width (no horizontal scrolling)
- **Viewport:** control's actual rendered size
- **Vertical offset:** pixel-based, clamped to `[0, max(0, extentHeight - viewportHeight)]`
- **MouseWheel:** 3 rows per tick (`3 * RowHeight` pixels)
- **ScrollOwner:** wired via `IScrollInfo.ScrollOwner`, notified on offset/extent/viewport changes

**Visible row range:**

```
firstVisible = (int)(verticalOffset / RowHeight)
lastVisible = min(firstVisible + (int)Math.Ceiling(viewportHeight / RowHeight), _flatRows.Count - 1)
```

No render buffer — the explorer renders only the visible slice on each paint. The row count is small enough that this is fast.

## Mouse Interaction

**Hit-testing** (all based on fixed row height math):

```
rowIndex = (int)((mouseY + _verticalOffset) / RowHeight)
```

**Click zones** (relative to row start, after indent):
- **Arrow zone (0–20px after indent):** Toggle expand/collapse for nodes with children
- **Rest of row:** Select the item

**Behaviors:**

| Event | Action |
|-------|--------|
| `MouseLeftButtonDown` | Select row. If click is in arrow zone of an expandable node, toggle expand/collapse. |
| `MouseDoubleClick` | If selected item is a `File` with non-empty `FullPath`, fire `FileOpenRequested`. |
| `MouseRightButtonDown` | Select row under cursor, fire `ItemRightClicked`. |
| `MouseMove` | Update `_hoverRowIndex`. If changed, `InvalidateVisual()`. Update tooltip if name is truncated. |
| `MouseLeave` | Clear `_hoverRowIndex`, `InvalidateVisual()`. |

**Expand/collapse:** When toggling, set `FileTreeItem.IsExpanded`, then rebuild the flat list and `InvalidateVisual()`. The lazy-loading in `FileTreeItem` (placeholder children loaded on first expand) continues to work as-is.

**Tooltip:** On `MouseMove`, check if the name text width exceeds the available space. If so, set `ToolTip` to the item's `Name` (or `FullPath` for files). Clear otherwise.

## Theme Integration

Reads directly from `ThemeManager` instance — no local brush caching:

| Usage | Brush/Resource |
|-------|---------------|
| Control background | `ThemeExplorerBg` |
| Hover highlight | `ThemeExplorerItemHover` |
| Selection highlight | `ThemeExplorerItemSelected` |
| Project root / virtual folder text | `ThemeExplorerHeaderFg` |
| Normal text (files, directories) | `ThemeTextFg` |
| Arrow chevrons | `ThemeTextFgMuted` |

**No new theme keys required.**

Subscribe to `ThemeManager.ThemeChanged` and call `InvalidateVisual()`.

**Text rendering:** Uses `FormattedText` (not `GlyphRun`). The explorer draws at most ~40 short strings per frame — `FormattedText` is simpler and supports `MaxTextWidth` with `Trimming = CharacterEllipsis` natively. `GlyphRun` is reserved for EditorControl where per-character rendering performance matters.

## FileExplorerPanel Changes

**XAML:** The entire TreeView block (style, resources, HierarchicalDataTemplate) is replaced with:

```xml
<local:ExplorerTreeControl x:Name="ExplorerTree"/>
```

Wrapped in a `ScrollViewer` (using the existing `ThemedScrollViewer` template) with `CanContentScroll="True"` for `IScrollInfo` integration. The `ScrollViewer` provides the scrollbar visuals; the control provides the scroll logic via `IScrollInfo`.

**Code-behind simplifications:**

- `OnTreeDoubleClick` removed — control fires `FileOpenRequested` directly
- `OnTreeRightClick` simplified — no more `FindVisualParent<TreeViewItem>()`. Subscribe to `ItemRightClicked` event, read `SelectedItem` property, build context menu as before.
- `OpenFolder()` / `OpenProject()` / `CloseFolder()` / `CloseProject()` call `ExplorerTree.SetRootItems()` instead of setting `FolderTree.ItemsSource`
- `RebuildProjectTree` and `CollectExpandedPaths` stay — they operate on the `FileTreeItem` data model, not the control
- After rebuilding the tree, call `ExplorerTree.RefreshFlatList()` to sync the flat row list

The context menu building logic in `OnTreeRightClick` is unchanged — it already works with `FileTreeItem` objects.
