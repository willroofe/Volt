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

### 3. Static classes (ThemeManager, SyntaxManager) limit testability and flexibility

Both are static classes with static state. This means:

- You can't run two editors with different themes/grammars
- Unit testing requires global state setup/teardown
- Initialization order is implicit and fragile

Consider making them instance-based and passing them through constructors (or at minimum, make the static state resettable for testing when you add tests).

### 4. Code-behind UI construction (CommandPalette, FindBar)

Both controls build their entire UI tree in C# constructors — 444 and 508 lines respectively. This is harder to read and modify than XAML. Moving the layout to XAML and keeping only behavior in code-behind would improve maintainability. The themed styling could use shared Style resources in App.xaml rather than being set inline on each element.

### 5. No folder structure

All 12 source files sit flat in one directory. A simple grouping would help:

```
TextEdit/
  Editor/
    EditorControl.cs      (or the extracted pieces)
    SyntaxManager.cs
    SyntaxDefinition.cs
  Theme/
    ThemeManager.cs
    ColorTheme.cs
  UI/
    MainWindow.xaml/.cs
    CommandPalette.cs
    FindBar.cs
    SettingsWindow.xaml/.cs
  AppSettings.cs
  App.xaml/.cs
```

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
3. Add folder structure
4. Move embedded JSON to embedded resources
5. Convert CommandPalette/FindBar to XAML + code-behind