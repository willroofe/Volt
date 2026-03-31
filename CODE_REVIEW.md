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

### ~~6. Hardcoded default themes/grammars as string literals~~ ✅ Done

Default theme and grammar JSON moved from inline string literals to embedded resource files under `Resources/Themes/` and `Resources/Grammars/`. ThemeManager and SyntaxManager now read from `Assembly.GetManifestResourceStream()` at startup. The JSON files are proper files that can be edited, validated, and diffed independently.

## Low Priority

### ~~7. Magic numbers scattered throughout~~ ✅ Done

Extracted named constants in `EditorControl.cs` for all previously-inline magic numbers:

- `GutterRightMargin = 8` — margin between line numbers and gutter separator
- `GutterSeparatorThickness = 0.5` — pen width for the gutter divider line
- `HorizontalScrollPadding = 50` — extra horizontal extent beyond longest line
- `BarCaretWidth = 1` — width of the bar-style caret in pixels
- `DefaultFontSize = 14` — initial font size and monospace font detection size
- `MouseWheelDeltaUnit = 120.0` — standard Windows mouse wheel delta per notch
- `ScrollWheelLines = 3` — lines scrolled per mouse wheel notch

Pre-existing named constants (`GutterPadding`, `RenderBufferLines`, `MaxBracketScanLines`) were already in place. The gutter number right padding (previously a bare `4`) now reuses `GutterPadding`. Dead zone height (6px) remains in XAML where it's self-documenting.

### ~~8. No abstraction for text buffer operations~~ ✅ Done

Added five mutation methods to `TextBuffer`:

- **`InsertAt(line, col, text)`** — insert text at a column position
- **`DeleteAt(line, col, length)`** — delete a range of characters
- **`ReplaceAt(line, col, length, text)`** — replace a range with new text
- **`JoinWithNext(line)`** — join a line with the next line
- **`TruncateAt(line, col)`** — truncate at column, returns removed tail

All inline string-slicing mutations in `EditorControl` (`OnTextInput`, `HandleReturn`, `HandleBackspace`, `HandleDelete`, `HandleTab`, `HandlePaste`, `ReplaceCurrent`, `ReplaceAll`) now use these methods. Each method also calls `NotifyLineChanging` internally, eliminating manual max-length-cache invalidation at each call site.

### 9. List<string> for text storage

Fine for now, but if you ever want large file support, a piece table or rope structure would be more appropriate. Not worth changing unless you hit performance walls.

### ~~10. Missing input validation at boundaries~~ ✅ Done

Added defensive bounds clamping at key boundary points:

- **`ClampCaret()`** helper in `EditorControl` — clamps `_caretLine` and `_caretCol` to valid buffer ranges. Called at the start of `OnRender`, after `Undo`/`Redo` (where restored positions could be stale), and in `FindMatchingBracket` (which indexes `_buffer[_caretLine]` directly).
- **`ClampToBuffer()`** helper in `SelectionManager` — clamps both anchor and caret positions against the buffer. Called at the start of `GetSelectedText` and `DeleteSelection` before any indexing.

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
4. ~~Move embedded JSON to embedded resources~~ ✅
5. ~~Convert CommandPalette/FindBar to XAML + code-behind~~ ✅