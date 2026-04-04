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

### ~~HIGH-IMPACT: Tab restoration logic (MainWindow.xaml.cs)~~ DONE

Extracted `RestoreTabsFromSession(RestoredSession)` helper. `RestoreSession()` and `RestoreFolderTabs()` now delegate to it. `RestoreWorkspaceSession()` left separate — uses a different data model (`WorkspaceSessionTab`), different scroll restore mechanism (`Dispatcher.InvokeAsync` + `ScrollHost`), and no dirty-state handling.

### ~~HIGH-IMPACT: XAML button templates~~ PARTIALLY DONE

Moved identical `DialogButton` style from `ThemedInputBox.xaml` and `ThemedMessageBox.xaml` into `App.xaml`. TabRegion inline templates differ in `CornerRadius` (3 vs 2) from `MatchCaseButton`, so not consolidated. SettingsWindow/FindBar templates are already using shared styles from App.xaml.

### ~~MODERATE-IMPACT: ThemeManager resource assignments~~ DONE

`UpdateAppResources()` now uses a data-driven `ReadOnlySpan<(string, string)>` mapping with a `foreach` loop.

### ~~MODERATE-IMPACT: FindBar toggle handlers~~ DONE

Extracted `UpdateToggleButton(Button btn, bool active)` helper. Four handlers now call it instead of duplicating `SetResourceReference` pairs.

### ~~MODERATE-IMPACT: FileExplorerPanel new file/folder~~ DONE

`DoNewFile()` and `DoNewFolder()` now delegate to `CreateFileSystemItem(string parentDir, bool isDirectory)`.

### MODERATE-IMPACT: TextBuffer dirty-flag assignments — SKIPPED

`InvalidateMaxLineLength()` already exists and sets both dirty flags. The internal mutation methods also increment `_editGeneration`, making a single unified call awkward. Not worth adding another helper.

### MODERATE-IMPACT: Scroll-anchor restoration (EditorControl.cs) — SKIPPED

The two anchor sites (`WordWrap` setter, `UpdateExtent`) differ in initial-state handling (wrap-on vs wrap-off), maxY calculation, and restore conditions. Extracting helpers would just move the complexity.

### MODERATE-IMPACT: PanelShell placement switches — SKIPPED

The four switches map placements to XAML-named elements with different property types (Width vs Height). A dictionary lookup would require the same mapping and wouldn't reduce complexity.

### LOW-IMPACT: AppSettings migration — SKIPPED

Migration code runs once per settings upgrade. Not worth adding a generic helper for one-time code.

### ~~LOW-IMPACT: Session directory clearing~~ DONE

Extracted `SafeDeleteFiles(string dir)` — both `ClearSessionDir()` and `ClearFolderSessionDir()` now delegate to it.

### LOW-IMPACT: SyntaxDefinition error handling — SKIPPED

Four try-catch blocks have different error recovery (null field, remove entry, null parent). Not unifiable with a single `TryCompileRegex` helper.

### ~~LOW-IMPACT: Duplicate brush caching~~ DONE

Removed `_scopeBrushes` dictionary and `UpdateScopeBrushes()` method from `ThemeManager`. `GetScopeBrush()` now delegates directly to `ColorTheme.GetScopeBrush()` which has its own cache.

---

## 3. Over-Engineering — ALL SKIPPED

### LOW-IMPACT: PanelShell nested classes — SKIPPED

`PanelRegistration` already uses a primary constructor and has mutable properties (`Placement`, `IsVisible`). Already concise.

### LOW-IMPACT: TabRegion TabEntry — SKIPPED

Event handler lambdas need unsubscription tracking. Inherent WPF complexity.

### LOW-IMPACT: ExplorerTreeControl FlatRow — SKIPPED

Named struct is more readable than a tuple in the rendering code.

---

## 4. Structural Complexity — ALL SKIPPED (verified as already reasonable)

### HIGH-IMPACT: EditorControl.OnKeyDown — SKIPPED

Only ~70 lines. Each case already delegates to a named handler method. A dispatch table would be more complex for mixed modifier patterns.

### HIGH-IMPACT: EditorControl.EnsureLineStates — SKIPPED

Only ~37 lines, not 60 as originally estimated. Nesting is manageable. Splitting would add more lines than it saves.

### ~~MODERATE-IMPACT: MainWindow.RestoreSession~~ DONE (via Duplication section)

Tab-creation loop extracted to `RestoreTabsFromSession()`.

### MODERATE-IMPACT: ExplorerTreeControl.OnRender — SKIPPED

Render-path code benefits from being inline for performance visibility. Helper extraction would just scatter the logic.

### MODERATE-IMPACT: PanelShell drag-to-dock — SKIPPED

Position threshold logic is specific to the drop zone calculation and wouldn't simplify meaningfully as separate methods.

### MODERATE-IMPACT: RenderWrappedSelection — SKIPPED

Only 28 lines. The `extendToEdge` logic is 4 lines — not worth extracting.

### LOW-IMPACT: FileExplorerPanel Undo/Redo switches — SKIPPED

Undo and redo have opposite operations per case. Combining with an `isUndo` flag would add internal branching without reducing complexity.

### LOW-IMPACT: AppSettings.Load nesting — SKIPPED

Simple try-catch-try-catch pattern. Extracting a helper for one callsite is unnecessary.

---

## 5. Verbose Patterns — ALL SKIPPED (verified as false positives or not worth changing)

### MODERATE-IMPACT: Manual loops replaceable with LINQ — SKIPPED

- Quote detection (EditorControl.cs) tracks escape state — not replaceable with `LastIndexOf`
- Binary searches (EditorControl.cs, FindManager.cs) use composite keys (line, col) — `Array.BinarySearch` would need a custom comparer and wouldn't be cleaner
- Workspace folder loop (FileExplorerPanel.xaml.cs) has side effects (event subscription, conditional expansion) — not suitable for `AddRange` + LINQ

### MODERATE-IMPACT: Verbose null-check patterns — SKIPPED

Standard defensive patterns. Consolidating would reduce readability without meaningful benefit.

### LOW-IMPACT: Complex boolean expressions — SKIPPED

The conditions are domain-specific logic that reads clearly in context.

### LOW-IMPACT: App.xaml scrollbar template duplication — SKIPPED

WPF requires separate templates for vertical/horizontal scrollbar orientations. The templates differ in layout direction properties. This is inherent WPF boilerplate.

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

| Category | Status | Changes Made |
|----------|--------|-------------|
| Dead Weight | **ALL DONE** | ~27 Debug.WriteLine removed, 2 redundant null checks removed |
| Duplication | **6 DONE, 4 SKIPPED** | Tab restore helper, DialogButton to App.xaml, ThemeManager data-driven loop, FindBar toggle helper, CreateFileSystemItem, SafeDeleteFiles, scope brush cache dedup |
| Over-Engineering | **ALL SKIPPED** | Verified as already reasonable |
| Structural Complexity | **1 DONE (via duplication), REST SKIPPED** | RestoreSession simplified. Others verified as already clean |
| Verbose Patterns | **ALL SKIPPED** | Verified as false positives or not worth changing |
| Tests/Benchmarks | **ALL DONE** | Test helper extraction, assertion helpers, benchmark setup |
