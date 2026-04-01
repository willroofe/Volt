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

### EditorControl.cs -- Repetitive edit handler boilerplate

Every edit handler (`HandleReturn`, `HandleBackspace`, `HandleDelete`, `HandleTab`, `HandlePaste`, `OnTextInput`) follows the same pattern:

```csharp
ResetPreferredCol();
int sl = _caretLine, el = _caretLine;
if (_selection.HasSelection)
{
    var (s, _, e2, _) = _selection.GetOrdered(_caretLine, _caretCol);
    sl = s; el = e2;
}
var scope = BeginEdit(sl, el);
if (_selection.HasSelection)
    (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
// ... actual edit logic ...
EndEdit(scope);
_selection.Clear();
UpdateExtent();
EnsureCaretVisible();
ResetCaret();
```

This 12-line boilerplate appears 6 times. A helper method like `EditAction(Action editBody)` that wraps the common pre/post pattern would eliminate ~60 lines and reduce the chance of forgetting a step.

**Rating:** MINOR -- works correctly but is repetitive.

---

### SettingsWindow.xaml.cs -- Constructor parameter count (line 23)

The constructor takes 10 positional parameters of mixed types. This makes call sites fragile and hard to read.

```csharp
// SettingsWindow.xaml.cs:23-25
public SettingsWindow(ThemeManager themeManager, int currentTabSize, bool blockCaret, int caretBlinkMs,
    string currentFontFamily, double currentFontSize, string currentFontWeight, double currentLineHeight,
    string currentColorTheme, string findBarPosition)
```

A record or settings snapshot object would be clearer:

```csharp
// Suggested
public SettingsWindow(ThemeManager themeManager, AppSettings settings)
```

**Rating:** MINOR -- functional but easy to get wrong at call sites.

---

### ThemeManager.cs / SyntaxManager.cs -- Inconsistent resource overwrite strategy

`SyntaxManager.EnsureDefaultGrammars()` (line 548) always overwrites built-in grammars, and `ThemeManager.EnsureDefaultThemes()` (line 148) also always overwrites. However, the CLAUDE.md says "Default resource files in `%AppData%` are only written if absent". The code and documentation are out of sync.

```csharp
// SyntaxManager.cs:552
// Always overwrite built-in grammars so embedded fixes take effect

// ThemeManager.cs:158
// Always overwrite built-in themes so embedded fixes take effect
```

**Rating:** MINOR -- the code behavior is intentional, but the CLAUDE.md documentation is stale.

---

### ColorTheme.cs -- `GetScopeBrush` catches an exception that `ParseBrush` already handles (lines 87-92)

```csharp
// ColorTheme.cs:87-92
public SolidColorBrush? GetScopeBrush(string scope)
{
    if (!Scopes.TryGetValue(scope, out var hex)) return null;
    try { return ParseBrush(hex); }
    catch { return null; }
}
```

`ParseBrush` already catches exceptions and returns a magenta fallback. The outer try-catch is redundant.

**Rating:** MINOR -- no functional impact, just unnecessary defensive code.

---

### MainWindow.xaml.cs -- `_isDragging` name collision with EditorControl

Both `MainWindow` (line 25) and `EditorControl` (line 239) have `_isDragging` fields with completely different semantics (tab drag vs mouse text selection). While they're in different classes and don't conflict at compile time, the shared name can confuse during cross-file searches and debugging.

**Rating:** MINOR -- `_isTabDragging` in MainWindow would be clearer.

---

### EditorControl.cs -- Magic number 67 in `SetPosition` (FindBar.cs:37)

```csharp
// FindBar.xaml.cs:37
Margin = top ? new Thickness(0, 67, 0, 0) : new Thickness(0, 0, 0, 44);
```

These magic numbers (67px top margin, 44px bottom margin) presumably correspond to the title bar + tab bar height and the status bar height respectively. They should be named constants or derived from the actual element sizes.

**Rating:** MINOR -- will break silently if the title bar or status bar height changes.

---

### EditorControl.cs -- `_currentMatchIndex` initialized to 0 in `SetContent` but -1 elsewhere

```csharp
// EditorControl.cs:1774
_currentMatchIndex = 0;   // in SetContent

// EditorControl.cs:1810
_currentMatchIndex = -1;  // in SetFindMatches

// EditorControl.cs:1852
_currentMatchIndex = -1;  // in ClearFindMatches
```

With `_findMatches` cleared, index 0 is technically out of bounds. Should consistently use -1 to mean "no match".

**Rating:** MINOR -- unlikely to cause visible bugs but logically inconsistent.

---

## STYLE

### Inconsistent variable naming in destructured tuples

Some destructuring uses meaningful names, others use discards inconsistently:

```csharp
// EditorControl.cs:1158 -- uses e2 for end line
var (s, _, e2, _) = _selection.GetOrdered(_caretLine, _caretCol);

// EditorControl.cs:1604 -- uses el for the same concept
var (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);

// EditorControl.cs:858 -- uses full names
var (sl, sc, el, ec) = _selection.GetOrdered(_caretLine, _caretCol);
```

Pick one convention and use it consistently. The `(sl, sc, el, ec)` form is clearest.

**Rating:** STYLE

---

### Inconsistent event handler naming

Some handlers use `On` prefix, others don't:

```csharp
// MainWindow.xaml.cs
private void OnNew(...)          // On-prefix
private void OnOpen(...)         // On-prefix
private void CloseTab(...)       // no prefix (also an event handler via click)
private void OnSettings(...)     // On-prefix
private void StepFontSize(...)   // no prefix (called from OnKeyDown)
```

Methods that are direct event handlers should consistently use `On` prefix to distinguish them from business logic methods.

**Rating:** STYLE

---

### Inconsistent brace style for single-line methods

```csharp
// EditorControl.cs:34 -- expression body
public int TabSize { get; set; } = 4;

// EditorControl.cs:102-106 -- block body for simple property
public string FontFamilyName
{
    get => _monoTypeface.FontFamily.Source;
    set { ApplyFont(value, _fontSize, _fontWeight); }
}
```

The codebase generally uses expression bodies for simple members but occasionally mixes in block bodies. This is minor but slightly inconsistent.

**Rating:** STYLE

---

### CommandPalette.xaml.cs -- `_input`, `_list`, `_prefix` field names lack descriptive prefix

```csharp
// CommandPalette.xaml.cs:43-44
_prefix.Text = "";
_input.Text = "";
```

These are XAML `x:Name` references. The underscore prefix is good, but `_input` is extremely generic. `_searchInput` or `_filterInput` would be clearer when reading the C# code in isolation. (This is more of a XAML naming concern.)

**Rating:** STYLE

---

## Summary

### Overall Maintainability Score: **3.5 / 5**

**Justification:** This is a well-architected hobby/personal project that demonstrates strong engineering judgment in its core design decisions: the layered rendering system, region-based undo, line-state convergence optimization, and theming architecture are all thoughtfully designed. The code is generally readable with good use of section headers, descriptive constants, and summary comments where intent isn't obvious. The project structure (Editor/, Theme/, UI/) provides clear navigability.

The main liability is the size of `EditorControl.cs` and `MainWindow.xaml.cs`, which concentrate too many responsibilities. This doesn't block development today, but it raises the cost of every new feature and increases the risk of regressions. The absence of a test framework compounds this -- the only safety net for changes is manual testing.

### Top 3 Highest-Impact Improvements

1. ~~**Extract FindManager, BracketMatcher, and FontManager from EditorControl**~~ **DONE** -- Reduced from ~1,900 to ~1,600 lines. Three extracted classes: `BracketMatcher` (static), `FindManager`, `FontManager`.

2. ~~**Extract DwmHelper, FileHelper, CommandPaletteCommands from MainWindow**~~ **DONE** -- Reduced from 1,039 to 860 lines. DWM interop, file I/O, file type map, and command palette lambdas extracted. `OnNew`/`OnNewTab` deduplication and file type switch → dictionary.

3. **Consolidate the repeated edit-handler boilerplate into a shared helper** -- The 6 edit handlers share ~12 lines of identical setup/teardown code. A single `PerformEdit(int startLine, int endLine, Action editBody)` method would eliminate redundancy and ensure consistency (e.g., always calling `UpdateExtent` and `EnsureCaretVisible`).

4. **Fix the FindBar toggle-replace duplication** -- This is the most likely source of a near-term bug. `OnToggleReplaceClick` reimplements `SetReplaceVisible` inline instead of calling it, meaning any future styling change to the replace toggle must be made in two places.
