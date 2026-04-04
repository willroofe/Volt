# Code Complexity & Simplification Review

**Project**: Volt Text Editor (WPF / .NET 10)
**Date**: 2025-04-04
**Total source lines reviewed**: ~10,800 (main project) + ~1,200 (tests/benchmarks)
**Estimated total line reduction**: ~400–550 lines

---

## Executive Summary

The codebase is well-organized with clear separation of concerns. The main complexity hotspots are:

1. **EditorControl.cs (2,084 lines)** — the single largest file, with duplicated scroll-anchor logic, a 26-case `OnKeyDown` switch, and deeply nested rendering methods
2. **MainWindow.xaml.cs (1,618 lines)** — contains three near-identical tab-restoration methods (~240 lines combined) that should be unified
3. **XAML button template duplication** — five files repeat nearly identical `ControlTemplate` definitions (~50 lines recoverable)
4. **ThemeManager verbose resource mapping** — 28 identical assignment lines that could be a data-driven loop (~40 lines recoverable)
5. **Scattered `Debug.WriteLine` calls** — production code in 6+ files contains debug logging that should be removed or replaced

No fundamental architectural problems were found. Issues are primarily duplication and verbosity.

---

## 1. Dead Weight

### ~~Debug.WriteLine / Console.WriteLine in production code~~ DONE

All ~27 `Debug.WriteLine` calls removed across 10 files: `SyntaxManager.cs`, `SyntaxDefinition.cs`, `FontManager.cs`, `MainWindow.xaml.cs`, `FileExplorerPanel.xaml.cs`, `FileTreeItem.cs`, `AppSettings.cs`, `EditorControl.cs`, `ColorTheme.cs`, `ThemeManager.cs`. Catch blocks preserved with empty bodies or simplified.

### ~~Redundant null checks~~ DONE

Removed `ThemeManager != null` guards in `EditorControl.cs` Loaded/Unloaded handlers — `ThemeManager` is non-nullable, set in constructor.

**False positives removed from review:**
- ~~`UI/MainWindow.xaml.cs` 59–60~~ — no `_editor` field exists at these lines
- ~~`UI/MainWindow.xaml.cs` 116–117~~ — single guard before event unhook, not redundant

### ~~Redundant field initialization~~ NOT APPLICABLE

~~`Theme/ThemeManager.cs` 20–30~~ — false positive. The default brush values serve as safe fallbacks before `Apply()` runs during startup. Removing them would leave null properties.

---

## 2. Duplication

### HIGH-IMPACT: Tab restoration logic (MainWindow.xaml.cs) — ~100 lines recoverable

Three methods contain near-identical tab-restoration code:

| Method | Lines | Line count |
|--------|-------|------------|
| `RestoreSession()` | 598–649 | ~50 |
| `RestoreFolderTabs()` | 709–758 | ~50 |
| `RestoreWorkspaceSession()` | 1552–1595 | ~43 |

**Fix**: Extract a single `RestoreTabsFromList(IEnumerable<TabInfo> tabs, TabInfo? activeTab)` method.

### HIGH-IMPACT: XAML button templates — ~50 lines recoverable

Five files define near-identical button `ControlTemplate` blocks:

| File | Lines | Template name |
|------|-------|---------------|
| `UI/FindBar.xaml` | 28–102 | Four `MatchCaseButton`-style buttons |
| `UI/Dialogs/SettingsWindow.xaml` | 42–83 | `TitleBarButton` + `CloseButton` |
| `UI/Dialogs/ThemedInputBox.xaml` | 15–42 | `DialogButton` |
| `UI/Dialogs/ThemedMessageBox.xaml` | 15–42 | `DialogButton` (identical copy) |
| `UI/Panels/TabRegion.xaml` | 19–53 | Close/add button templates |

**Fix**: Consolidate shared button styles into `App.xaml` or a shared resource dictionary.

### MODERATE-IMPACT: ThemeManager resource assignments — ~40 lines recoverable

**ThemeManager.cs lines 121–154**: 28 identical `res[key] = ColorTheme.ParseBrush(hex)` assignments.

**Fix**: Use a data-driven loop:
```csharp
var mapping = new[] {
    (ThemeResourceKeys.ChromeBrush, c.TitleBar),
    (ThemeResourceKeys.BorderBrush, c.Border),
    // ...
};
foreach (var (key, hex) in mapping)
    res[key] = ColorTheme.ParseBrush(hex);
```

Similarly, **ThemeManager.cs lines 95–109** has 12 sequential editor color assignments that could use the same pattern.

### MODERATE-IMPACT: FindBar toggle handlers — ~60 lines recoverable

**FindBar.xaml.cs lines 206–234**: Four toggle button handlers (`OnMatchCaseClick`, `OnRegexClick`, `OnWholeWordClick`, `OnFindInSelectionClick`) are structurally identical: toggle boolean, update button appearance, call `UpdateSearch`.

**Fix**: Extract `ToggleOption(ref bool field, Button btn)`.

### MODERATE-IMPACT: FileExplorerPanel new file/folder — ~30 lines recoverable

**FileExplorerPanel.xaml.cs lines 276–324**: `DoNewFile()` and `DoNewFolder()` are 95% identical.

**Fix**: Extract `CreateFileSystemItem(string parentDir, bool isDirectory)`.

### MODERATE-IMPACT: TextBuffer dirty-flag assignments

**TextBuffer.cs** — six separate locations assign `_maxLineLengthDirty = true; _charCountDirty = true;` (lines 115, 121, 127, 201, 223, 247, 258).

**Fix**: Extract `InvalidateCachedLengths()` method.

### MODERATE-IMPACT: Scroll-anchor restoration (EditorControl.cs)

**EditorControl.cs lines 54–95**: The wrap/scroll anchor save-restore pattern appears three times with slight variations in the `WordWrap` setter and `UpdateExtent()`.

**Fix**: Extract `SaveScrollAnchor()` / `RestoreScrollAnchor()` helpers.

### MODERATE-IMPACT: PanelShell placement switches

**PanelShell.xaml.cs lines 258–330**: Four separate `switch` statements on `PanelPlacement` for `GetContentPresenter`, `GetSplitter`, `GetDropOverlay`, and `SetSplitterRowCol`.

**Fix**: Use `Dictionary<PanelPlacement, (ContentPresenter, GridSplitter, ...)>` lookup.

### LOW-IMPACT: AppSettings migration

**AppSettings.cs lines 217–245**: 18 repetitive `TryGetProperty` blocks with identical structure.

**Fix**: Helper method `T ReadProperty<T>(JsonElement root, string key, T defaultValue)`.

### LOW-IMPACT: Session directory clearing

**AppSettings.cs lines 120–147**: `ClearSessionDir()` and `ClearFolderSessionDir()` are identical except for the path.

**Fix**: Extract `SafeDeleteFiles(string dir)`.

### LOW-IMPACT: SyntaxDefinition error handling

**SyntaxDefinition.cs lines 92–158**: Four identical try-catch blocks with `Debug.WriteLine`.

**Fix**: Extract `TryCompileRegex(string pattern, out Regex? result)`.

### LOW-IMPACT: Duplicate brush caching

**ThemeManager.cs line 33** (`_scopeBrushes`) and **ColorTheme.cs line 71** (`_brushCache`) maintain parallel caches for scope brushes.

**Fix**: Remove `_scopeBrushes` and delegate to `ColorTheme` directly.

---

## 3. Over-Engineering

### LOW-IMPACT: PanelShell nested classes

**PanelShell.xaml.cs lines 639–645**: `PanelRegistration` is a 7-line class that could be a `record`:
```csharp
record PanelRegistration(IPanel Panel, PanelPlacement Placement, PanelContainer Container, bool IsVisible);
```

### LOW-IMPACT: TabRegion TabEntry

**TabRegion.cs lines 240–252**: `TabEntry` stores event handler lambdas for later unsubscription. Inherent WPF complexity, but could be simplified to a record.

### LOW-IMPACT: ExplorerTreeControl FlatRow

**ExplorerTreeControl.cs line 717**: `FlatRow` struct could be a simple tuple `(FileTreeItem Item, int Depth)`.

---

## 4. Structural Complexity

### HIGH-IMPACT: EditorControl.OnKeyDown — 26-case switch

**EditorControl.cs lines 1317–1391**: Massive switch with internal if-else chains. Cases like `Key.Z when ctrl` (lines 1375–1381) follow repetitive patterns.

**Fix**: Extract case bodies into named methods (`HandleCtrlZ()`, `HandleCtrlY()`, etc.) or use a `Dictionary<(Key, bool ctrl, bool shift), Action>` dispatch table.

### HIGH-IMPACT: EditorControl.EnsureLineStates

**EditorControl.cs lines 781–801**: 60-line method with 4 nesting levels handling line count shifting, dirty tracking, and continuation.

**Fix**: Split into `GrowLineStates()`, `ShrinkLineStates()`, `RevalidateLineStates()`.

### MODERATE-IMPACT: ExplorerTreeControl.OnRender

**ExplorerTreeControl.cs lines 292–378**: 86-line render method with manual FormattedText caching, conditional rendering for hover/selected/drop-target states, and multi-step icon/arrow/text positioning.

**Fix**: Extract `RenderRow()`, `RenderIcon()`, `RenderText()` helpers.

### MODERATE-IMPACT: MainWindow.RestoreSession

**MainWindow.xaml.cs lines 551–669**: 119-line method with 5+ nesting levels including lambdas inside loops.

**Fix**: In addition to extracting the shared tab-restoration helper (see Duplication above), split the remaining session-specific logic into smaller methods.

### MODERATE-IMPACT: PanelShell drag-to-dock

**PanelShell.xaml.cs lines 509–586**: 77-line `OnMouseMove` calculating drop zones with repeated position threshold checks and brush manipulation.

**Fix**: Extract `CalculateDropZone()` and `UpdateDropOverlay()`.

### MODERATE-IMPACT: RenderWrappedSelection

**EditorControl.cs lines 969–997**: 28 lines with 6 nesting levels determining if selection extends to edge.

**Fix**: Extract the `extendToEdge` logic (lines 986–990) into a helper method.

### LOW-IMPACT: FileExplorerPanel Undo/Redo switches

**FileExplorerPanel.xaml.cs lines 411–481**: `Undo()` and `Redo()` contain near-identical 5-case switch statements.

**Fix**: Extract shared `ApplyFileOperation(FileOperation op, bool isUndo)`.

### LOW-IMPACT: AppSettings.Load nesting

**AppSettings.cs lines 181–211**: Nested try-catch blocks for file load and backup creation.

**Fix**: Extract `TryLoadAndMigrate(string json)`.

---

## 5. Verbose Patterns

### MODERATE-IMPACT: Manual loops replaceable with LINQ

| File | Lines | Description |
|------|-------|-------------|
| `Editor/EditorControl.cs` | 514–527 | Manual while loop for quote detection — use `LastIndexOf` |
| `Editor/EditorControl.cs` | 879–884 | Manual binary search — `Array.BinarySearch` exists |
| `Editor/FindManager.cs` | 51–61 | Hand-rolled binary search — use `Array.BinarySearch` |
| `Editor/SyntaxManager.cs` | 318–323 | Quote-finding loop — use `FirstOrDefault` |
| `UI/Explorer/FileExplorerPanel.xaml.cs` | 185–194 | Manual foreach for workspace folders — use `AddRange` + LINQ |

### MODERATE-IMPACT: Verbose null-check patterns

| File | Lines | Description |
|------|-------|-------------|
| `UI/MainWindow.xaml.cs` | 204–208 | Multiple null checks in `RemoveTab` — consolidate |
| `UI/FindBar.xaml.cs` | 379–393 | Manual visual tree walk — use null-conditional chain |

### LOW-IMPACT: Complex boolean expressions

| File | Lines | Description |
|------|-------|-------------|
| `Editor/EditorControl.cs` | 1406–1408 | Triple-nested bracket pair detection condition |
| `Editor/EditorControl.cs` | 1460–1461 | Two-line pair deletion OR condition |
| `UI/MainWindow.xaml.cs` | 775–778 | Window visibility check with 4 comparisons |

### LOW-IMPACT: App.xaml scrollbar template duplication

**App.xaml lines 155–216**: Vertical and horizontal scrollbar templates are 60+ lines of nearly identical XAML differing only in orientation.

---

## 6. Test & Benchmark Issues

### ~~MODERATE-IMPACT: Test setup duplication~~ DONE

| File | Pattern | Fix Applied |
|------|---------|-------------|
| `PanelShellTests.cs` | `new FakePanel(...)` creation 14 times | Extracted `CreatePanel()` helper |
| `SelectionManagerTests.cs` | Selection manager init + anchor set 6 times | Extracted `CreateSelection()` helper |
| `SyntaxManagerTests.cs` | `CreateInitialized()` per test 6 times | Already had helper — no change needed |
| `FindManagerTests.cs` | Identical `Search()` call pattern 5 times | Extracted `Search()` helper with defaults |

### ~~LOW-IMPACT: Verbose assertion patterns~~ DONE

**BracketMatcherTests.cs**: Extracted `AssertMatch(result, line, col, matchLine, matchCol)` helper, replacing 6 repeated five-line assertion blocks.

### ~~LOW-IMPACT: Benchmark state management~~ DONE

**TextBufferBenchmarks.cs**: Moved buffer line reset to `[IterationSetup]` with targeted `Target = nameof(...)` and static readonly string fields.

---

## Summary Table

| Category | High | Moderate | Low | Est. Lines Saved |
|----------|------|----------|-----|-----------------|
| Dead Weight | 0 | 1 (Debug.WriteLine cleanup) | 2 | ~30 |
| Duplication | 3 (tab restore, XAML templates, ThemeManager) | 5 (FindBar toggles, new file/folder, TextBuffer dirty, scroll anchor, PanelShell switches) | 5 | ~300 |
| Over-Engineering | 0 | 0 | 3 | ~15 |
| Structural Complexity | 2 (OnKeyDown, EnsureLineStates) | 4 (OnRender, RestoreSession, drag-to-dock, RenderWrappedSelection) | 2 | ~50 (via extraction, not deletion) |
| Verbose Patterns | 0 | 3 (LINQ replacements, null checks, scrollbar XAML) | 3 | ~40 |
| ~~Tests/Benchmarks~~ | ~~0~~ | ~~2 (setup duplication, assertions)~~ | ~~2~~ | ~~\~60~~ | **ALL DONE** |
| **Totals** | **5** | **13 remaining** | **15 remaining** | **~340–490 remaining** |
