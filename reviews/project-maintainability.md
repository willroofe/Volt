# TextEdit Project Maintainability Review

**Date:** 2026-04-01
**Codebase Size:** ~5,200 lines of C# across 16 source files, plus ~1,200 lines of XAML
**Framework:** WPF on .NET 10, custom text editor control with direct `DrawingContext` rendering

---

## CRITICAL

No critical findings. The codebase has no issues that fundamentally block maintainability.

---

## MODERATE

### ~~EditorControl.cs (1,924 lines) -- God class / insufficient decomposition~~ ADDRESSED

The single largest maintainability concern in the project. `EditorControl` is a ~1,900 line class that owns rendering, input handling, scrolling, undo/redo orchestration, bracket matching, find/replace, caret management, font management, and syntax state tracking. While the `TextBuffer`, `SelectionManager`, and `UndoManager` have been correctly extracted, the remaining surface area makes it difficult to understand, navigate, or safely modify any single concern.

```csharp
// EditorControl.cs:11
public class EditorControl : FrameworkElement, IScrollInfo
```

**Recommended decomposition targets** (each would reduce EditorControl by 100-300 lines):

1. **FindManager** -- `SetFindMatches`, `ClearFindMatches`, `FindNext`, `FindPrevious`, `ReplaceCurrent`, `ReplaceAll`, `NavigateToCurrentMatch`, `CentreLineInViewport`, and `_findMatches`/`_currentMatchIndex` state (lines 1803-1923). This is a self-contained feature with its own state and no tight coupling to rendering internals beyond `InvalidateVisual()`.

2. **BracketMatcher** -- `FindMatchingBracket`, `FindEnclosingBracket`, `ScanForBracket`, `BracketPairs`, `ClosingBrackets`, `ReverseBracketPairs`, and the bracket match cache (lines 527-640 plus related fields). Clean boundary -- takes buffer + position, returns match result.

3. **FontManager** -- `ApplyFont`, `GetMonospaceFonts`, `_monoFontCache`, `_monoTypeface`, `_glyphTypeface`, font metrics fields, and `DrawGlyphRun` (lines 102-200, 692-713). Currently interleaved with rendering state.

~~**Rating:** MODERATE~~ Extracted `BracketMatcher`, `FindManager`, and `FontManager`. EditorControl reduced from 1,924 to ~1,600 lines.

---

### ~~MainWindow.xaml.cs (1,039 lines) -- Mixed concerns in window code-behind~~ ADDRESSED

Extracted `DwmHelper` (DWM interop), `FileHelper` (file I/O utilities, file type map, encoding detection), and `CommandPaletteCommands` (90 lines of palette lambdas). `OnNew` delegates to `OnNewTab`. File type switch replaced with `Dictionary` lookup. MainWindow reduced from 1,039 to 860 lines.

~~**Rating:** MODERATE~~

---

### ~~MainWindow.xaml.cs -- Tab header construction in code-behind (lines 195-316)~~ ADDRESSED

Close button template moved to `TabCloseButton` XAML style in App.xaml. Procedural `ControlTemplate`/`FrameworkElementFactory` code replaced with a single `Style = FindResource("TabCloseButton")` call.

~~**Rating:** MODERATE~~

---

### ~~SyntaxManager.cs -- `Tokenize` method complexity (lines 63-268)~~ ADDRESSED

Decomposed into 8 focused private methods: `TryTokenizeBlockComment`, `TryTokenizeHeredocContinuation`, `ContinueOpenRegex`, `ContinueOpenString`, `ApplyGrammarRules`, `DetectHeredocMarker`, `DetectRegexPatterns`, `DetectUnclosedStringAtEOL`. The main `Tokenize` method is now a ~30-line orchestrator.

~~**Rating:** MODERATE~~

---

### ~~SelectionManager.cs -- `ClampToBuffer` mutates anchor via side effect (lines 29-36)~~ ADDRESSED

Made `public` with XML doc comment explicitly documenting the dual mutation of both anchor and caret positions.

~~**Rating:** MODERATE~~

---

### ~~UndoManager.cs -- Duplicate XML doc comments (lines 27-32)~~ ADDRESSED

Removed the stale duplicate `<summary>` tag.

~~**Rating:** MODERATE~~

---

### ~~FindBar.cs -- Duplicated replace-toggle logic (lines 51-68 vs 99-105)~~ ADDRESSED

`OnToggleReplaceClick` now calls `SetReplaceVisible(show)` instead of reimplementing the visibility/angle/margin logic inline.

~~**Rating:** MODERATE~~

---

### ~~EditorControl.cs -- `ColToPixelX` is dead weight (lines 687-690)~~ ADDRESSED

Removed during the FontManager extraction — all call sites now use `col * _font.CharWidth` directly.

~~**Rating:** MODERATE~~

---

## MINOR

### ~~EditorControl.cs -- Repetitive edit handler boilerplate~~ ADDRESSED

Extracted three shared helpers: `GetEditRange()` (returns selection-aware line range), `DeleteSelectionIfPresent()`, and `FinishEdit(scope)` (wraps `EndEdit` + `_selection.Clear()` + `UpdateExtent()` + `EnsureCaretVisible()` + `ResetCaret()`). All 6 edit handlers now use these helpers, eliminating ~40 lines of duplicated boilerplate.

~~**Rating:** MINOR~~

---

### ~~SettingsWindow.xaml.cs -- Constructor parameter count (line 23)~~ ADDRESSED

Introduced `SettingsSnapshot` record to bundle the 9 settings parameters. Constructor now takes `(ThemeManager themeManager, SettingsSnapshot snapshot)`.

~~**Rating:** MINOR~~

---

### ~~ThemeManager.cs / SyntaxManager.cs -- Inconsistent resource overwrite strategy~~ ADDRESSED

CLAUDE.md was already updated in a prior session to say "Built-in resource files are always overwritten on startup". Code and documentation are now in sync.

~~**Rating:** MINOR~~

---

### ~~ColorTheme.cs -- `GetScopeBrush` catches an exception that `ParseBrush` already handles (lines 87-92)~~ ADDRESSED

Removed the redundant outer `try-catch`. `GetScopeBrush` now calls `ParseBrush` directly.

~~**Rating:** MINOR~~

---

### ~~MainWindow.xaml.cs -- `_isDragging` name collision with EditorControl~~ ADDRESSED

Renamed to `_isTabDragging` in MainWindow (all 6 references).

~~**Rating:** MINOR~~

---

### ~~EditorControl.cs -- Magic number 67 in `SetPosition` (FindBar.cs:37)~~ ADDRESSED

Replaced with named constants `FindBarTopMargin` (67 = title bar + tab bar height) and `FindBarBottomMargin` (44 = status bar height) in FindBar.

~~**Rating:** MINOR~~

---

### ~~EditorControl.cs -- `_currentMatchIndex` initialized to 0 in `SetContent` but -1 elsewhere~~ ADDRESSED

Resolved by the FindManager extraction. `SetContent` now calls `_find.Clear()` which consistently sets `_currentIndex = -1`. No direct `_currentMatchIndex` field remains in EditorControl.

~~**Rating:** MINOR~~

---

## STYLE

### ~~Inconsistent variable naming in destructured tuples~~ ADDRESSED

Standardized all `GetOrdered` destructuring to `(sl, sc, el, ec)` or `(sl, _, el, _)` convention. The `(s, _, e2, _)` form in `GetEditRange()` was the last outlier.

~~**Rating:** STYLE~~

---

### ~~Inconsistent event handler naming~~ ADDRESSED

`CloseTab(TabInfo)` and `StepFontSize(int)` are business logic methods (no `(sender, args)` signature) called BY event handlers, not event handlers themselves. The `On` prefix is correctly reserved for methods with the event handler signature. Naming is already consistent.

~~**Rating:** STYLE~~

---

### ~~Inconsistent brace style for single-line methods~~ ADDRESSED

Resolved during the FontManager extraction. `FontFamilyName` setter now uses expression body (`set => Apply(...)`) consistent with the rest of the codebase.

~~**Rating:** STYLE~~

---

### ~~CommandPalette.xaml.cs -- `_input`, `_list`, `_prefix` field names lack descriptive prefix~~ ADDRESSED

Renamed XAML `x:Name` references and corresponding C# fields: `_input` → `_filterInput`, `_list` → `_commandList`, `_prefix` → `_filterPrefix`.

~~**Rating:** STYLE~~

---

## Summary

### Overall Maintainability Score: **3.5 / 5**

**Justification:** This is a well-architected hobby/personal project that demonstrates strong engineering judgment in its core design decisions: the layered rendering system, region-based undo, line-state convergence optimization, and theming architecture are all thoughtfully designed. The code is generally readable with good use of section headers, descriptive constants, and summary comments where intent isn't obvious. The project structure (Editor/, Theme/, UI/) provides clear navigability.

The main liability is the size of `EditorControl.cs` and `MainWindow.xaml.cs`, which concentrate too many responsibilities. This doesn't block development today, but it raises the cost of every new feature and increases the risk of regressions. The absence of a test framework compounds this -- the only safety net for changes is manual testing.

### Top 3 Highest-Impact Improvements

1. ~~**Extract FindManager, BracketMatcher, and FontManager from EditorControl**~~ **DONE** -- Reduced from ~1,900 to ~1,600 lines. Three extracted classes: `BracketMatcher` (static), `FindManager`, `FontManager`.

2. ~~**Extract DwmHelper, FileHelper, CommandPaletteCommands from MainWindow**~~ **DONE** -- Reduced from 1,039 to 860 lines. DWM interop, file I/O, file type map, and command palette lambdas extracted. `OnNew`/`OnNewTab` deduplication and file type switch → dictionary.

3. ~~**Consolidate the repeated edit-handler boilerplate into a shared helper**~~ **DONE** -- Extracted `GetEditRange()`, `DeleteSelectionIfPresent()`, and `FinishEdit(scope)`. All 6 edit handlers refactored to use them.

4. ~~**Fix the FindBar toggle-replace duplication**~~ **DONE** -- `OnToggleReplaceClick` now delegates to `SetReplaceVisible`.
