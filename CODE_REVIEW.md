# Code Review: TextEdit

## Overall Impression

This is a well-built project for its size. The performance choices (GlyphRun rendering, buffered visuals, transform-based scrolling) are sound, and the theming system is cleanly designed. The main concern is that EditorControl.cs is doing too much — it's a 2000-line god class that handles text storage, rendering, input, scrolling, selection, undo, find, bracket matching, and syntax state all in one file.

## High Priority

### ~~1. EditorControl is a god class~~ ✅ Done

Addressed by extracting three classes and breaking up OnKeyDown:

- **TextBuffer** (`TextBuffer.cs`) — line storage, dirty tracking, content get/set, tab expansion, line ending detection, max line length caching, snapshot/restore for undo.
- **UndoManager** (`UndoManager.cs`) — undo/redo stacks with snapshot-based entries, push/undo/redo operations, stack size cap.
- **SelectionManager** (`SelectionManager.cs`) — anchor/caret selection model, GetSelectedText, DeleteSelection, GetOrderedSelection.
- **OnKeyDown** split into 9 handler methods: HandleReturn, HandleBackspace, HandleDelete, HandleTab, HandleNavigation, HandleSelectAll, HandleCopy, HandleCut, HandlePaste.

CaretManager and InputHandler were not extracted as separate classes — caret position is too deeply coupled to rendering/input/selection to benefit from indirection, and the handler methods already achieve the readability goal of an InputHandler without the coupling overhead. EditorControl reduced from ~1979 to ~1360 lines.

### ~~2. Full-snapshot undo is a scaling risk~~ ✅ Done

Replaced full-buffer snapshots with region-based undo. Each `UndoEntry` now stores only the affected line range (before/after lines + caret positions). `EditorControl` uses a `BeginEdit(startLine, endLine)` / `EndEdit(scope)` pattern at each edit site to capture just the changed region. `TextBuffer` gained `GetLines()` and `ReplaceLines()` helpers for snapshotting and applying region diffs. Undo/redo applies the diff via `ReplaceLines` instead of restoring the entire buffer.

## Medium Priority

### ~~3. Static classes (ThemeManager, SyntaxManager) limit testability and flexibility~~ ✅ Done

Converted both from static classes to regular instance classes:

- **ThemeManager** and **SyntaxManager** are now instance-based — no more static state. Pure utility methods and compiled regexes remain static since they're stateless.
- **App** (`App.xaml.cs`) creates and owns both instances, exposed via `App.Current.ThemeManager` / `App.Current.SyntaxManager` (typed `Current` accessor).
- **EditorControl** receives instances via `ThemeManager`/`SyntaxManager` properties set by MainWindow after `InitializeComponent`. Event subscription moved to `Loaded` handler.
- **MainWindow** accesses instances through convenience properties delegating to `App.Current`.
- **SettingsWindow** receives `ThemeManager` via constructor parameter.

### ~~4. Code-behind UI construction (CommandPalette, FindBar)~~ ✅ Done

Converted both controls from code-behind-only to XAML + code-behind:

- **CommandPalette** — UI tree moved to `CommandPalette.xaml`, class made `partial`. Constructor reduced from ~140 lines of UI building to `InitializeComponent()`. Overlay click and input text change wired via XAML event attributes.
- **FindBar** — UI tree moved to `FindBar.xaml`, class made `partial`. Constructor reduced from ~180 lines to `InitializeComponent()`. All button click handlers wired via XAML event attributes. Four static style-creation methods (`CreateNavButtonTemplate`, `CreateTextButtonTemplate`, `CreateMatchCaseButtonStyle`, `CreateRoundedTextBoxStyle`) and two static factory methods (`MakeNavButton`, `MakeTextButton`) eliminated entirely.
- **Shared styles** added to `App.xaml`: `FindBarNavButton`, `FindBarTextButton`, `MatchCaseButton`, `RoundedTextBox` — replacing the programmatic `FrameworkElementFactory`-based templates with declarative XAML.

### ~~5. No folder structure~~ ✅ Done

Source files organized into three subdirectories:

- **Editor/** — `EditorControl.cs`, `TextBuffer.cs`, `UndoManager.cs`, `SelectionManager.cs`, `SyntaxManager.cs`, `SyntaxDefinition.cs`
- **Theme/** — `ThemeManager.cs`, `ColorTheme.cs`
- **UI/** — `MainWindow.xaml/.cs`, `CommandPalette.xaml/.cs`, `FindBar.xaml/.cs`, `SettingsWindow.xaml/.cs`
- **Root** — `App.xaml/.cs`, `AppSettings.cs`, `AssemblyInfo.cs`

All classes remain in the `TextEdit` namespace — no code changes required, purely a file organization move.

### 6. Hardcoded default themes/grammars as string literals

ThemeManager.cs contains multi-line JSON string literals for default themes. These would be cleaner as embedded resources — easier to edit, validate, and extend.

## Low Priority

### 7. Magic numbers scattered throughout

Values like `GutterPadding = 4`, buffer size ±50 lines, `scroll delta e.Delta / 120.0 * _lineHeight * 3`, dead zone 6px, etc. are hardcoded. Most are fine as constants but a few (like scroll speed) could be settings.

### 8. No abstraction for text buffer operations

```
_lines[_caretLine] = _lines[_caretLine][.._caretCol] + pasteLines[0] + _lines[_caretLine][_caretCol..]
```

appears in several forms throughout OnKeyDown and OnTextInput. A small set of buffer mutation methods would reduce duplication and bugs.

### 9. List<string> for text storage

Fine for now, but if you ever want large file support, a piece table or rope structure would be more appropriate. Not worth changing unless you hit performance walls.

### 10. Missing input validation at boundaries

There are several places where `_caretCol` and `_caretLine` are used to index into `_lines` without bounds checking. The code generally keeps these in sync, but defensive guards on public entry points (SetContent, etc.) would prevent subtle bugs.

## What's Good (keep doing this)

- **Performance-conscious rendering**: The three-layer DrawingVisual approach with transform-based scrolling is well-engineered
- **Clean theme architecture**: One JSON file controls everything, DynamicResource binding is consistent
- **Good separation between theme model and theme application**: ColorTheme (data) vs ThemeManager (behavior)
- **Sensible defaults and graceful fallbacks**: Font fallback, theme fallback, encoding detection
- **DWM integration**: The dark mode title bar/border matching is a nice polish detail
- **Section comments in EditorControl**: The `// ── Section ──` headers make navigation easier in the large file

## Suggested Priorities

If I were to tackle these in order:

1. ~~Extract TextBuffer from EditorControl (biggest bang for maintainability)~~ ✅
2. ~~Break OnKeyDown into smaller handler methods~~ ✅
3. ~~Add folder structure~~ ✅
4. Move embedded JSON to embedded resources
5. ~~Convert CommandPalette/FindBar to XAML + code-behind~~ ✅