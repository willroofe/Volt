# TextEdit Project Maintainability Review

**Date:** 2026-04-01
**Codebase Size:** ~5,200 lines of C# across 16 source files, plus ~1,200 lines of XAML
**Framework:** WPF on .NET 10, custom text editor control with direct `DrawingContext` rendering

---

## CRITICAL

No critical findings. The codebase has no issues that fundamentally block maintainability.

---

## MODERATE

### EditorControl.cs (1,924 lines) -- God class / insufficient decomposition

The single largest maintainability concern in the project. `EditorControl` is a ~1,900 line class that owns rendering, input handling, scrolling, undo/redo orchestration, bracket matching, find/replace, caret management, font management, and syntax state tracking. While the `TextBuffer`, `SelectionManager`, and `UndoManager` have been correctly extracted, the remaining surface area makes it difficult to understand, navigate, or safely modify any single concern.

```csharp
// EditorControl.cs:11
public class EditorControl : FrameworkElement, IScrollInfo
```

**Recommended decomposition targets** (each would reduce EditorControl by 100-300 lines):

1. **FindManager** -- `SetFindMatches`, `ClearFindMatches`, `FindNext`, `FindPrevious`, `ReplaceCurrent`, `ReplaceAll`, `NavigateToCurrentMatch`, `CentreLineInViewport`, and `_findMatches`/`_currentMatchIndex` state (lines 1803-1923). This is a self-contained feature with its own state and no tight coupling to rendering internals beyond `InvalidateVisual()`.

2. **BracketMatcher** -- `FindMatchingBracket`, `FindEnclosingBracket`, `ScanForBracket`, `BracketPairs`, `ClosingBrackets`, `ReverseBracketPairs`, and the bracket match cache (lines 527-640 plus related fields). Clean boundary -- takes buffer + position, returns match result.

3. **FontManager** -- `ApplyFont`, `GetMonospaceFonts`, `_monoFontCache`, `_monoTypeface`, `_glyphTypeface`, font metrics fields, and `DrawGlyphRun` (lines 102-200, 692-713). Currently interleaved with rendering state.

**Rating:** MODERATE -- not blocking current development, but makes every editor feature change riskier than it needs to be.

---

### MainWindow.xaml.cs (1,039 lines) -- Mixed concerns in window code-behind

`MainWindow` combines window chrome management, tab lifecycle, file I/O, settings orchestration, command palette setup, DWM interop, and keyboard shortcut dispatch. The command palette command definitions alone span 90 lines of lambda closures (lines 943-1038).

```csharp
// MainWindow.xaml.cs:943-1038
private void OpenCommandPalette()
{
    var commands = new List<PaletteCommand>
    {
        new("Change Theme", GetOptions: () => { ... }),
        new("Change Font Size", GetOptions: () => { ... }),
        // ... 7 more command definitions with preview/commit/revert lambdas
    };
```

**Specific concerns:**

1. **File type detection is hardcoded** (lines 664-693): A 30-entry switch expression mapping extensions to display names duplicates information that could live in grammar definitions or a simple config.

   ```csharp
   // MainWindow.xaml.cs:664
   var fileType = ext switch
   {
       ".txt" => "Plain Text",
       ".cs" => "C# Source",
       ".pl" or ".cgi" => "Perl Script",
       // ... 25 more entries
   };
   ```

2. **`OnNewTab` and `OnNew` are identical** (lines 762-773):

   ```csharp
   // MainWindow.xaml.cs:762-773
   private void OnNewTab(object sender, RoutedEventArgs e)
   {
       var tab = CreateTab();
       ActivateTab(tab);
   }

   private void OnNew(object sender, RoutedEventArgs e)
   {
       // Ctrl+N creates a new tab
       var tab = CreateTab();
       ActivateTab(tab);
   }
   ```

   One should delegate to the other or they should be the same method.

**Rating:** MODERATE -- the file is navigable with section headers, but the mixed concerns make it harder to reason about changes.

---

### MainWindow.xaml.cs -- Tab header construction in code-behind (lines 195-316)

`CreateTabHeader` builds a complex visual tree (Border > DockPanel > TextBlock + Button with custom ControlTemplate) entirely in procedural C#. This is 120 lines of imperative UI construction that bypasses WPF's declarative templating, making it harder to adjust styling and impossible to preview in a designer.

```csharp
// MainWindow.xaml.cs:226-239
var closeBtnTemplate = new ControlTemplate(typeof(Button));
var closeBorder = new FrameworkElementFactory(typeof(Border), "Bd");
closeBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
closeBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
var closeContent = new FrameworkElementFactory(typeof(ContentPresenter));
// ... 10 more lines of programmatic template construction
```

A `DataTemplate` in XAML with an `ItemsControl` bound to the tab collection would be cleaner and more maintainable.

**Rating:** MODERATE -- functional but fights the framework.

---

### SyntaxManager.cs -- `Tokenize` method complexity (lines 63-268)

The main `Tokenize` method is 205 lines with deep nesting, multiple early returns, and interleaved handling of block comments, heredocs, regexes, strings, and normal rules. The method manages 6 different tokenization modes and mutates local arrays in complex ways.

```csharp
// SyntaxManager.cs:63
public List<SyntaxToken> Tokenize(string line, LineState inState, out LineState outState)
{
    outState = DefaultState;
    if (_activeGrammar == null) return [];
    // ... 205 lines of interleaved tokenization logic
}
```

The individual sections (block comment continuation, heredoc continuation, regex continuation, rule matching, unclosed string detection) are mostly self-contained and could be extracted into private methods to reduce cognitive load.

**Rating:** MODERATE -- the method works correctly but is difficult to follow and risky to modify.

---

### SelectionManager.cs -- `ClampToBuffer` mutates anchor via side effect (lines 29-36)

`ClampToBuffer` takes `caretLine`/`caretCol` as `ref` parameters but also silently mutates the instance's `AnchorLine`/`AnchorCol`. This is a non-obvious side effect that a caller would not expect from the method name.

```csharp
// SelectionManager.cs:29-36
private void ClampToBuffer(TextBuffer buffer, ref int caretLine, ref int caretCol)
{
    int maxLine = Math.Max(0, buffer.Count - 1);
    AnchorLine = Math.Clamp(AnchorLine, 0, maxLine);  // side effect on instance state
    AnchorCol = Math.Clamp(AnchorCol, 0, buffer[AnchorLine].Length);
    caretLine = Math.Clamp(caretLine, 0, maxLine);
    caretCol = Math.Clamp(caretCol, 0, buffer[caretLine].Length);
}
```

**Rating:** MODERATE -- could cause subtle bugs if anchor mutation is not expected.

---

### UndoManager.cs -- Duplicate XML doc comments (lines 27-32)

```csharp
// UndoManager.cs:27-32
/// <summary>
/// Push a region-based undo entry. Clears the redo stack.
/// </summary>
/// <summary>
/// Push a region-based undo entry. Clears the redo stack.
/// Returns true if the oldest entry was evicted due to the size cap.
/// </summary>
public bool Push(UndoEntry entry)
```

Two consecutive `<summary>` tags -- the first is stale and should be removed.

**Rating:** MODERATE -- misleading documentation.

---

### FindBar.cs -- Duplicated replace-toggle logic (lines 51-68 vs 99-105)

`ToggleReplace` and `OnToggleReplaceClick` independently implement the same toggle behavior with slight differences in structure. If one is modified, the other must be updated in sync.

```csharp
// FindBar.xaml.cs:51-62 (ToggleReplace)
bool show = _replaceRow.Visibility != Visibility.Visible;
SetReplaceVisible(show);
if (show)
    Dispatcher.BeginInvoke(..., () => Keyboard.Focus(_replaceInput));

// FindBar.xaml.cs:99-105 (OnToggleReplaceClick)
bool show = _replaceRow.Visibility != Visibility.Visible;
_replaceRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
_toggleTransform.Angle = show ? 180 : 0;
_findRow.Margin = new Thickness(8, 6, 8, show ? 2 : 6);
```

`OnToggleReplaceClick` should call `SetReplaceVisible(show)` instead of reimplementing the logic inline.

**Rating:** MODERATE -- violation of DRY that will cause bugs when one path is updated but not the other.

---

### EditorControl.cs -- `ColToPixelX` is dead weight (lines 687-690)

```csharp
// EditorControl.cs:687-690
private double ColToPixelX(string line, int col)
{
    return col * _charWidth;
}
```

The `line` parameter is never used. Every call site could be replaced with `col * _charWidth` directly, or the parameter should be removed. The method exists in case non-monospace support is added, but currently it obscures what's actually happening.

**Rating:** MODERATE -- misleading API that suggests line content affects positioning.

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

1. **Extract FindManager, BracketMatcher, and FontManager from EditorControl** -- This would reduce the class from ~1,900 lines to ~1,100 lines and make each concern independently understandable and testable. Each extraction has clean boundaries and minimal coupling to the rest of the class.

2. **Consolidate the repeated edit-handler boilerplate into a shared helper** -- The 6 edit handlers share ~12 lines of identical setup/teardown code. A single `PerformEdit(int startLine, int endLine, Action editBody)` method would eliminate redundancy and ensure consistency (e.g., always calling `UpdateExtent` and `EnsureCaretVisible`).

3. **Fix the FindBar toggle-replace duplication** -- This is the most likely source of a near-term bug. `OnToggleReplaceClick` reimplements `SetReplaceVisible` inline instead of calling it, meaning any future styling change to the replace toggle must be made in two places.
