# Remediation Log

Changes made in response to `reviews/project-maintainability.md`. Listed by original finding heading.

---

## CRITICAL

### `UI/MainWindow.xaml.cs` — Unguarded null dereference via `Editor` property
**Changed:** Added `if (_activeTab == null) return;` guards at 4 unguarded call sites: `CmdPalette.Closed` lambda, `FindBarControl.Closed` lambda, `OpenCommandPalette()`, and `StepFontSize()`.
**Files:** `Volt/UI/MainWindow.xaml.cs`

### `UI/ExplorerTreeControl.cs` — Unguarded cast in render path
**Changed:** Replaced `(Brush)Application.Current.Resources[key]` with `Application.Current.Resources[key] as Brush ?? Brushes.Magenta` fallback, matching `ColorTheme.ParseBrush` pattern.
**Files:** `Volt/UI/ExplorerTreeControl.cs`

### `UI/CommandPalette.xaml` — Hardcoded overlay color violates theming rule
**Changed:** Added `ThemeOverlayBg` resource key in `App.xaml` (documented as theme-invariant, like close-button red). Changed `CommandPalette.xaml` to use `{DynamicResource ThemeOverlayBg}`.
**Files:** `Volt/App.xaml`, `Volt/UI/CommandPalette.xaml`

---

## MODERATE

### `Editor/EditorControl.cs` — Untyped undo stacks use `List<object>`
**Changed:** Introduced `UndoEntryBase` abstract record with shared caret fields. `UndoEntry` and `IndentEntry` now inherit from it. Stacks changed to `List<UndoEntryBase>`. `Undo()`/`Redo()` return `UndoEntryBase?`. Consumers in `EditorControl.Undo()`/`Redo()` use `switch` with shared caret extraction — no more fallback `throw`.
**Files:** `Volt/Editor/UndoManager.cs`, `Volt/Editor/EditorControl.cs`

### `Editor/EditorControl.cs` — ThemeManager and SyntaxManager null-initialized
**Changed:** Added null guards (`if (ThemeManager != null)`) in both the `Loaded` and `Unloaded` event handlers before subscribing/unsubscribing to `ThemeChanged`.
**Files:** `Volt/Editor/EditorControl.cs`

### `Editor/EditorControl.cs` — IsCaretInsideString dual-path inconsistency
**Changed:** Removed backtick from the fallback quote detection, aligning it with SyntaxManager's `DetectUnclosedString` which only handles `"` and `'`.
**Files:** `Volt/Editor/EditorControl.cs`

### `Editor/EditorControl.cs` — Duplicated rendering logic for wrapped vs. non-wrapped text
**Changed:** Extracted `RenderLineTokens(dc, line, x, y, segStart, segEnd, tokens)` helper. Both wrapped and non-wrapped paths now call this single method.
**Files:** `Volt/Editor/EditorControl.cs`

### `Editor/EditorControl.cs` — Selection rendering deeply nested
**Changed:** Extracted `RenderWrappedSelection(dc, line, selStart, selEnd, sl, sc, el)` method from the inline wrapped-selection block.
**Files:** `Volt/Editor/EditorControl.cs`

### `Editor/EditorControl.cs` — Stale wrap-array access unguarded in helper methods
**Changed:** Added bounds guards (`if (_wrapCumulOffset == null || logLine >= _wrapCumulOffset.Length)`) to all 6 wrap helper methods: `LogicalToVisualLine`, `GetVisualY`, `VisualToLogical`, `GetPixelForPosition`, `VisualLineCount`, `WrapColStart`. Each falls back to the non-wrapped identity operation.
**Files:** `Volt/Editor/EditorControl.cs`

### `Editor/SyntaxManager.cs` — Quadratic claiming check in ApplyGrammarRules
**Not changed.** The quadratic behavior is bounded by regex timeout (50ms) and typical line lengths (<200 chars). Optimizing with interval tracking would add complexity disproportionate to the actual risk. Documented in this log.

### `UI/MainWindow.xaml` — Massively duplicated ItemContainerStyle
**Changed:** Extracted `MenuItemDropdownStyle` (base) and `MenuItemCheckableStyle` (with CheckMark + IsChecked trigger) into `Window.Resources`. Replaced all 4 inline `ItemContainerStyle` blocks with style references. Net reduction of ~120 lines of duplicated XAML.
**Files:** `Volt/UI/MainWindow.xaml`

### `UI/CommandPaletteCommands.cs` — Excessive parameter count (15 parameters)
**Changed:** Created `CommandPaletteContext` record grouping all parameters. Updated `Build()` signature and the call site in `MainWindow.xaml.cs`.
**Files:** `Volt/UI/CommandPaletteCommands.cs`, `Volt/UI/MainWindow.xaml.cs`

### `UI/CommandPaletteCommands.cs` — Semantic misuse of `Toggle` for one-shot commands
**Changed:** Renamed `Toggle` to `Action` in `PaletteCommand` record. Updated all 8 references in `CommandPaletteCommands.cs` and the dispatch logic in `CommandPalette.xaml.cs`.
**Files:** `Volt/UI/CommandPalette.xaml.cs`, `Volt/UI/CommandPaletteCommands.cs`

### `UI/MainWindow.xaml.cs` — PromptForInput uses unthemed Window
**Changed:** Rewrote `PromptForInput` to use `WindowStyle.None`, `AllowsTransparency=true`, `WindowChrome`, and all-DynamicResource themed colors. Layout matches `ThemedMessageBox` with custom title bar, themed border, and styled buttons.
**Files:** `Volt/UI/MainWindow.xaml.cs`

### `UI/MainWindow.xaml.cs` — Duplicate file-open logic
**Changed:** Extracted `OpenFileInTab(string path, bool reuseUntitled)` helper. `OnExplorerFileOpen` and `OnOpen` both call it.
**Files:** `Volt/UI/MainWindow.xaml.cs`

### `UI/ProjectManager.cs` — No validation of deserialized JSON data
**Changed:** Added `project.Folders ??= [];` and `project.VirtualFolders ??= [];` after deserialization.
**Files:** `Volt/UI/ProjectManager.cs`

### `AppSettings.cs` — MigrateOldFormat has side effect of writing to disk
**Changed:** Wrapped `Save()` call in try/catch with `Debug.WriteLine` logging. Added comments documenting the intentional side effect. Renamed variable `s` to `settings`.
**Files:** `Volt/AppSettings.cs`

---

## MINOR

### `Editor/EditorControl.cs` — CentreLineInViewport not wrap-aware
**Changed:** Replaced `line * _font.LineHeight` with `GetVisualY(line)`. Added early return for horizontal scroll when `_wordWrap` is true.
**Files:** `Volt/Editor/EditorControl.cs`

### `Editor/EditorControl.cs` — HandleCut bypasses FinishEdit pattern
**Changed:** Replaced manual `EndEdit(scope); UpdateExtent(); EnsureCaretVisible(); ResetCaret();` with `FinishEdit(scope);`.
**Files:** `Volt/Editor/EditorControl.cs`

### `Editor/EditorControl.cs` — `_prevCaretLine` declared mid-region
**Changed:** Moved field declaration to the caret fields section near the top of the class.
**Files:** `Volt/Editor/EditorControl.cs`

### `Editor/FontManager.cs` — DrawGlyphRun allocates ushort[] per call
**Not changed.** Initial attempt to pool the `ushort[]` glyph index buffer caused a rendering corruption bug: `GlyphRun` retains a reference to the array (does not copy), so reusing the buffer caused all previously-created `GlyphRun` objects in the same render pass to display the glyph indices from the last call (gutter line numbers bled into text). Reverted to per-call allocation. The `_uniformAdvanceWidths` pool is safe because all values are identical (`CharWidth`), but glyph indices are unique per token.
**Files:** `Volt/Editor/FontManager.cs`

### `Editor/FontManager.cs` — EditorFontWeight getter allocates converter each time
**Changed:** Added `private static readonly FontWeightConverter _fontWeightConverter` static field. Getter and setter reuse it.
**Files:** `Volt/Editor/FontManager.cs`

### `Editor/FontManager.cs` — Apply creates a DrawingVisual solely to read DPI
**Not changed.** The DPI read on construction is needed before the visual tree is available. Subsequent calls via `Loaded` and `OnDpiChanged` overwrite it. Low-impact allocation.

### `Editor/FindManager.cs` — GetMatchesReversed is misleadingly named
**Changed:** Renamed to `GetMatchesForReverseIteration`.
**Files:** `Volt/Editor/FindManager.cs`

### `Editor/BracketMatcher.cs` — FindEnclosing and ScanForBracket use while(true) loops
**Changed:** Replaced `while (true)` with explicit bounded conditions: `while (line >= minLine)` and `while (line >= minLine && line <= maxLine)`. Added `return null;` after loop bodies.
**Files:** `Volt/Editor/BracketMatcher.cs`

### `Editor/SyntaxManager.cs` — Magic string "msixpodualngcer" duplicated
**Changed:** Extracted `private const string PerlRegexModifiers = "msixpodualngcer";` and replaced both occurrences.
**Files:** `Volt/Editor/SyntaxManager.cs`

### `Editor/SyntaxManager.cs` — EnsureDefaultGrammars swallows exceptions silently
**Changed:** Added `Debug.WriteLine` to both catch blocks.
**Files:** `Volt/Editor/SyntaxManager.cs`

### `Editor/SyntaxDefinition.cs` — Compile swallows block comment exceptions silently
**Changed:** Added `Debug.WriteLine` to the block comment catch block.
**Files:** `Volt/Editor/SyntaxDefinition.cs`

### `Editor/TextBuffer.cs` — JoinWithNext uses += instead of string.Concat
**Changed:** Replaced `_lines[line] += _lines[line + 1]` with `_lines[line] = string.Concat(_lines[line], _lines[line + 1])`.
**Files:** `Volt/Editor/TextBuffer.cs`

### `Editor/TextBuffer.cs` — CharCount recomputes on every access
**Changed:** Added dirty-flag caching (`_charCount`, `_charCountDirty`) matching the `MaxLineLength` pattern. All mutation points invalidate both caches.
**Files:** `Volt/Editor/TextBuffer.cs`

### `Editor/TextBuffer.cs` — Indexer setter bypasses NotifyLineChanging
**Changed:** Added `NotifyLineChanging(index)` call inside the setter.
**Files:** `Volt/Editor/TextBuffer.cs`

### `Editor/SelectionManager.cs` — GetSelectedText uses Environment.NewLine
**Not changed.** `Environment.NewLine` is the correct clipboard convention on Windows. The behavior is intentional.

### `UI/FindBar.xaml.cs` — Hardcoded layout constants
**Changed:** Added detailed comments documenting the derivation from MainWindow.xaml dimensions with "Sync with MainWindow.xaml" prefix.
**Files:** `Volt/UI/FindBar.xaml.cs`

### `UI/SettingsWindow.xaml.cs` — Index-based combo box reads are fragile
**Changed:** Wrapped all `SelectedIndex` array accesses with `Math.Max(0, ...)` to guard against -1.
**Files:** `Volt/UI/SettingsWindow.xaml.cs`

### `Theme/ColorTheme.cs` — ParseBrush catches bare Exception
**Changed:** Narrowed to `catch (FormatException ex)`.
**Files:** `Volt/Theme/ColorTheme.cs`

### `AppSettings.cs` — SessionSettings mixes static and instance concerns
**Not changed.** The mix is a consequence of `SaveTabContent` needing instance data (`Tabs` list) while `LoadTabContent` and `ClearSessionDir` are standalone operations. Refactoring would add complexity without meaningful benefit.

### `UI/TabInfo.cs` — HeaderElement initialized with null!
**Not changed.** The `required` modifier would break the construction pattern where `HeaderElement` is set after construction in `CreateTab`. The `null!` is acceptable given the well-defined lifecycle.

### `UI/FileTreeItem.cs` — INotifyPropertyChanged implemented but appears unused
**Not changed.** The interface may be needed for future data-binding scenarios and removing it could break extensibility. Low risk.

### `UI/MainWindow.xaml.cs` — SaveTab and SaveTabAs duplicate post-save logic
**Changed:** Extracted `WriteAndFinishSave(TabInfo tab)` returning bool. Both `SaveTab` and `SaveTabAs` call it.
**Files:** `Volt/UI/MainWindow.xaml.cs`

---

## STYLE (addressed where falling naturally into edited files)

### `Theme/ThemeManager.cs` — ThemesDir field uses PascalCase
**Changed:** Renamed `ThemesDir` to `_themesDir` across the file.
**Files:** `Volt/Theme/ThemeManager.cs`

### `AppSettings.cs` — Single-letter variable `s` in MigrateOldFormat
**Changed:** Renamed `s` to `settings`.
**Files:** `Volt/AppSettings.cs`

---

## Build Verification

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Top 3 Highest-Impact Improvements — Confirmation

1. **Extract duplicated logic in MainWindow.xaml.cs** — All three areas addressed: `OpenFileInTab()`, `WriteAndFinishSave()`, and shared XAML styles.
2. **Introduce a typed undo entry hierarchy** — `UndoEntryBase` abstract record with `UndoEntry`/`IndentEntry` inheriting. Stacks typed. Consumers use `switch`.
3. **Guard implicit null contracts** — `Editor` property callers guarded, `ThemeManager` null-checked in handlers, `GetBrush` uses safe cast.
