# Unit Test Project — Design Spec

**Date:** 2026-04-03
**Resolves:** G1 (no test framework or tests)

## Problem

No unit test project exists. Core logic classes are well-suited for testing but have no automated verification.

## Design

### Project setup

- New `Volt.Tests` xUnit project targeting `net10.0-windows`
- References `Volt.csproj` (same pattern as `Volt.Benchmarks`)
- One test file per class under test
- Run with `dotnet test Volt.Tests`

### Test files

| Test file | Class under test | Dependencies |
|-----------|-----------------|--------------|
| `TextBufferTests.cs` | `TextBuffer` | None |
| `UndoManagerTests.cs` | `UndoManager` | None |
| `FindManagerTests.cs` | `FindManager` | `TextBuffer` |
| `BracketMatcherTests.cs` | `BracketMatcher` | `TextBuffer` |
| `WrapLayoutTests.cs` | `WrapLayout` | `TextBuffer` |
| `SyntaxManagerTests.cs` | `SyntaxManager` | File system (embedded grammars extracted to AppData) |

All classes are pure logic with no WPF dependencies. No mocking framework needed.

### Test scope (~4-5 tests per class, ~25 total)

**TextBuffer:**
- Insert/delete/replace operations on lines
- `SetContent` + `GetContent` roundtrip
- `JoinWithNext` merges two lines
- Dirty tracking (`IsDirty` set on mutation, cleared by `MarkClean`)

**UndoManager:**
- Push/undo/redo cycle restores correct entry
- Redo stack cleared on new push after undo
- 200-entry cap eviction (push returns true when evicting)
- `Clear()` resets both stacks

**FindManager:**
- Basic search finds expected matches with correct positions
- Case sensitivity toggle
- `MoveNext`/`MovePrevious` wrapping behavior
- No-match case returns zero matches

**BracketMatcher:**
- Matched parentheses on same line
- Nested brackets resolve to correct pair
- Unmatched bracket returns null
- Cross-line bracket matching

**WrapLayout:**
- Identity behavior when word wrap is off (visual line == logical line)
- Correct visual line count with wrap on and long lines
- `VisualToLogical` roundtrip consistency

**SyntaxManager:**
- `GetDefinition` returns grammar for known extension, null for unknown
- Tokenize a simple Perl line produces expected scope tokens
- Multi-line state: unclosed string carries `OpenQuote` across lines
- Null grammar returns empty token list

### SyntaxManager test setup

`Initialize()` extracts embedded grammars to `%AppData%/Volt/Grammars/`. Tests call `Initialize()` directly — no mocking needed. The Perl grammar (shipped as an embedded resource) provides the test fixture.

### What is NOT tested

- Anything touching `FrameworkElement`, `DrawingContext`, or WPF visual tree
- `EditorControl` rendering, scrolling, input handling
- `MainWindow` UI integration
- Theme loading and brush creation
