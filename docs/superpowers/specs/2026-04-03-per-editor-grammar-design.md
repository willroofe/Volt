# Per-Editor Grammar State — Design Spec

**Date:** 2026-04-03
**Resolves:** G3 (thread safety of SyntaxManager), A2 (shared grammar across editors)

## Problem

`SyntaxManager` owns a single `_activeGrammar` field mutated by `SetLanguageByExtension()`. All editors tokenize against this shared field. Switching tabs changes the grammar globally, creating a race condition if background line-state precomputation is running for another tab.

## Design

### SyntaxManager becomes a stateless grammar registry

Remove all mutable grammar state:
- Remove `_activeGrammar` field
- Remove `SetLanguageByExtension(string?)` method
- Remove `ActiveGrammar` property
- Remove `ActiveLanguageName` property

Add a pure lookup method:
- `SyntaxDefinition? GetDefinition(string? extension)` — returns the grammar for a file extension, or null. No side effects.

Change `Tokenize` to accept the grammar explicitly:
- `Tokenize(string line, SyntaxDefinition grammar)` (single-line convenience)
- `Tokenize(string line, SyntaxDefinition? grammar, LineState inState, out LineState outState)` (full signature)
- All private tokenization methods receive `SyntaxDefinition` as a parameter instead of reading `_activeGrammar`

`ReloadGrammars()` no longer restores `_activeGrammar` — it just reloads the grammar list and extension map.

### EditorControl owns its grammar

- Add `SyntaxDefinition? _grammar` field
- Add `void SetGrammar(SyntaxDefinition? grammar)` method that sets `_grammar` and calls `InvalidateSyntax()`
- Add `string LanguageName` property (`_grammar?.Name ?? "Plain Text"`)
- All `SyntaxManager.Tokenize(...)` calls pass `_grammar` as the grammar argument

### MainWindow updates

- `UpdateFileType()` calls `SyntaxManager.GetDefinition(ext)` and passes the result to `Editor.SetGrammar()`
- Status bar language display reads `Editor.LanguageName`

### Benchmark updates

- `TokenizeBenchmarks` stores a `SyntaxDefinition` obtained from `GetDefinition()` and passes it to `Tokenize()`

## Files changed

| File | Change |
|------|--------|
| `Volt/Editor/SyntaxManager.cs` | Remove `_activeGrammar` and related members; add `GetDefinition()`; add `grammar` parameter to `Tokenize` and private methods |
| `Volt/Editor/EditorControl.cs` | Add `_grammar` field, `SetGrammar()`, `LanguageName`; pass `_grammar` to `Tokenize` calls |
| `Volt/UI/MainWindow.xaml.cs` | Update `UpdateFileType()` to use `GetDefinition()` + `SetGrammar()` |
| `Volt.Benchmarks/TokenizeBenchmarks.cs` | Store grammar from `GetDefinition()`; pass to `Tokenize()` |
