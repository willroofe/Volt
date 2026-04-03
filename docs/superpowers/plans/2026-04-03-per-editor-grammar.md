# Per-Editor Grammar State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move grammar ownership from shared `SyntaxManager._activeGrammar` to per-`EditorControl`, eliminating the thread-safety issue (G3) and shared-state bug (A2).

**Architecture:** `SyntaxManager` becomes a stateless grammar registry — it loads grammars and looks them up by extension but holds no mutable state. Each `EditorControl` stores its own `SyntaxDefinition?` and passes it to `SyntaxManager.Tokenize()`. `MainWindow.UpdateFileType()` bridges the two: it looks up the grammar and hands it to the editor.

**Tech Stack:** C# / .NET 10 / WPF

---

### Task 1: Make SyntaxManager stateless

**Files:**
- Modify: `Volt/Editor/SyntaxManager.cs`

- [ ] **Step 1: Add `GetDefinition` method and update `Tokenize` signatures**

Add a pure lookup method. Change `Tokenize` to accept `SyntaxDefinition?` as a parameter. Update all private methods that read `_activeGrammar` to receive it as a parameter instead.

In `SyntaxManager.cs`, make these changes:

1. Add `GetDefinition` method (replaces `SetLanguageByExtension`):

```csharp
public SyntaxDefinition? GetDefinition(string? extension)
{
    if (string.IsNullOrEmpty(extension)) return null;
    var ext = extension.ToLowerInvariant();
    _extensionMap.TryGetValue(ext, out var grammar);
    return grammar;
}
```

2. Change the single-line `Tokenize` convenience overload (line 51-54) to accept a grammar:

```csharp
public List<SyntaxToken> Tokenize(string line, SyntaxDefinition? grammar)
{
    return Tokenize(line, grammar, DefaultState, out _);
}
```

3. Change the full `Tokenize` signature (line 57) to accept a grammar parameter:

```csharp
public List<SyntaxToken> Tokenize(string line, SyntaxDefinition? grammar, LineState inState, out LineState outState)
```

4. Inside the full `Tokenize` method body, replace every `_activeGrammar` reference with `grammar`. There are 4 occurrences:
   - Line 60: `if (_activeGrammar == null) return [];` -> `if (grammar == null) return [];`
   - Line 79: `if (_activeGrammar.Interpolation != null)` -> `if (grammar.Interpolation != null)`
   - Line 80: `tokens = ExpandInterpolation(tokens, line, _activeGrammar.Interpolation);` -> `tokens = ExpandInterpolation(tokens, line, grammar.Interpolation);`
   - Line 92-93: same pattern as 79-80

5. Update `TryTokenizeBlockComment` signature (line 98) to accept grammar:

```csharp
private List<SyntaxToken>? TryTokenizeBlockComment(string line, SyntaxDefinition grammar, LineState inState, ref LineState outState)
```

Replace `_activeGrammar!.BlockComments` (line 100) with `grammar.BlockComments`.

6. Update `TryTokenizeHeredocContinuation` signature (line 127) to accept grammar:

```csharp
private List<SyntaxToken>? TryTokenizeHeredocContinuation(string line, SyntaxDefinition grammar, LineState inState, ref LineState outState)
```

Replace `_activeGrammar!.Interpolation` (lines 136-137) with `grammar.Interpolation`.

7. Update `ApplyGrammarRules` signature (line 183) to accept grammar:

```csharp
private void ApplyGrammarRules(string line, int ruleStart, SyntaxDefinition grammar,
    List<SyntaxToken> tokens, bool[] claimed)
```

Replace `_activeGrammar!.Rules` (lines 191, 193) with `grammar.Rules`.

8. Update `DetectHeredocMarker` signature (line 228) to accept grammar:

```csharp
private void DetectHeredocMarker(string line, SyntaxDefinition grammar,
    List<SyntaxToken> tokens, bool[] claimed, ref LineState outState)
```

Replace `_activeGrammar!.Heredoc` (line 231) with `grammar.Heredoc`.

9. Update all call sites within `Tokenize` to pass `grammar` to these private methods:

```csharp
var result = TryTokenizeBlockComment(line, grammar, inState, ref outState);
// ...
result = TryTokenizeHeredocContinuation(line, grammar, inState, ref outState);
// ...
ApplyGrammarRules(line, ruleStart, grammar, tokens, claimed);
// ...
DetectHeredocMarker(line, grammar, tokens, claimed, ref outState);
```

- [ ] **Step 2: Remove `_activeGrammar` and related members**

Delete these members from `SyntaxManager.cs`:

- Field: `private SyntaxDefinition? _activeGrammar;` (line 25)
- Property: `public SyntaxDefinition? ActiveGrammar => _activeGrammar;` (line 37)
- Method: `SetLanguageByExtension` (lines 39-46) — entire method
- Property: `public string ActiveLanguageName => _activeGrammar?.Name ?? "Plain Text";` (line 48)

- [ ] **Step 3: Simplify `ReloadGrammars`**

Change `ReloadGrammars()` (line 544-551) to just reload without restoring active grammar:

```csharp
public void ReloadGrammars()
{
    _grammars.Clear();
    _extensionMap.Clear();
    LoadGrammars();
}
```

- [ ] **Step 4: Build to verify SyntaxManager compiles**

Run: `dotnet build Volt.sln 2>&1 | head -40`

Expected: Build errors in `EditorControl.cs`, `MainWindow.xaml.cs`, and `TokenizeBenchmarks.cs` (callers not yet updated). `SyntaxManager.cs` itself should have no errors.

---

### Task 2: Update EditorControl to own its grammar

**Files:**
- Modify: `Volt/Editor/EditorControl.cs`

- [ ] **Step 1: Add grammar field, setter, and language name property**

Near the other fields at the top of `EditorControl` (after line 22, the `SyntaxManager` property), add:

```csharp
private SyntaxDefinition? _grammar;

public string LanguageName => _grammar?.Name ?? "Plain Text";

public void SetGrammar(SyntaxDefinition? grammar)
{
    _grammar = grammar;
    InvalidateSyntax();
}
```

- [ ] **Step 2: Update all `SyntaxManager.Tokenize` calls to pass `_grammar`**

There are 4 call sites in `EditorControl.cs`. Update each:

1. `IsCaretInsideString` (line 463):
```csharp
// Before:
tokens = SyntaxManager.Tokenize(line, inState, out _);
// After:
tokens = SyntaxManager.Tokenize(line, _grammar, inState, out _);
```

2. `EnsureLineStates` convergence loop (line 718):
```csharp
// Before:
SyntaxManager.Tokenize(_buffer[i], _lineStates[i], out var outState);
// After:
SyntaxManager.Tokenize(_buffer[i], _grammar, _lineStates[i], out var outState);
```

3. `EnsureLineStates` expansion loop (line 730):
```csharp
// Before:
SyntaxManager.Tokenize(_buffer[lineIdx], inState, out var outState);
// After:
SyntaxManager.Tokenize(_buffer[lineIdx], _grammar, inState, out var outState);
```

4. Render text method (line 983):
```csharp
// Before:
var tokens = SyntaxManager.Tokenize(line, inState, out _);
// After:
var tokens = SyntaxManager.Tokenize(line, _grammar, inState, out _);
```

- [ ] **Step 3: Build to verify EditorControl compiles**

Run: `dotnet build Volt/Volt.csproj 2>&1 | head -40`

Expected: Errors remain in `MainWindow.xaml.cs` (still calling removed methods). `EditorControl.cs` should compile cleanly.

---

### Task 3: Update MainWindow and benchmarks

**Files:**
- Modify: `Volt/UI/MainWindow.xaml.cs`
- Modify: `Volt.Benchmarks/TokenizeBenchmarks.cs`

- [ ] **Step 1: Update `MainWindow.UpdateFileType()`**

Change `UpdateFileType()` (line 707-716) to use `GetDefinition` + `SetGrammar`:

```csharp
private void UpdateFileType()
{
    if (_activeTab == null) return;
    var ext = _activeTab.FilePath != null ? Path.GetExtension(_activeTab.FilePath).ToLowerInvariant() : "";
    Editor.SetGrammar(SyntaxManager.GetDefinition(ext));
    FileTypeText.Text = FileHelper.GetFileTypeName(ext);
    EncodingText.Text = GetEncodingLabel();
    LineEndingText.Text = Editor.LineEnding;
}
```

This replaces the two separate calls (`SyntaxManager.SetLanguageByExtension(ext)` + `Editor.InvalidateSyntax()`) with a single `Editor.SetGrammar()` call (which internally calls `InvalidateSyntax()`).

- [ ] **Step 2: Update status bar language display if it reads `ActiveLanguageName`**

Search for any usage of `ActiveLanguageName` in `MainWindow.xaml.cs`. If it exists, replace with `Editor.LanguageName`. (Based on the grep results, `ActiveLanguageName` is only defined in `SyntaxManager.cs` and may not be used in MainWindow — verify and update if needed.)

Run: Search for `ActiveLanguageName` or `LanguageName` in MainWindow to confirm.

- [ ] **Step 3: Update TokenizeBenchmarks**

Change `TokenizeBenchmarks.cs` to store a `SyntaxDefinition` and pass it to `Tokenize`:

```csharp
[MemoryDiagnoser]
public class TokenizeBenchmarks
{
    private SyntaxManager _mgr = null!;
    private SyntaxDefinition _grammar = null!;
    private string _simpleLine = null!;
    private string _complexLine = null!;
    private string _longLine = null!;
    private LineState _defaultState = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mgr = new SyntaxManager();
        _mgr.Initialize();
        _grammar = _mgr.GetDefinition(".pl")!;
        _defaultState = _mgr.DefaultState;

        _simpleLine = "    my $foo = 42;  # comment";
        _complexLine = "    my $foo = Bar::Baz->new({ key => $val, other => \"hello $world\" });  # comment";
        _longLine = string.Concat(Enumerable.Range(0, 50).Select(i => $"my $v{i} = {i}; "));
    }

    [Benchmark(Description = "Tokenize simple line")]
    public List<SyntaxToken> TokenizeSimple()
        => _mgr.Tokenize(_simpleLine, _grammar, _defaultState, out _);

    [Benchmark(Description = "Tokenize complex line")]
    public List<SyntaxToken> TokenizeComplex()
        => _mgr.Tokenize(_complexLine, _grammar, _defaultState, out _);

    [Benchmark(Description = "Tokenize long line (~500 chars)")]
    public List<SyntaxToken> TokenizeLong()
        => _mgr.Tokenize(_longLine, _grammar, _defaultState, out _);
}
```

- [ ] **Step 4: Build the full solution**

Run: `dotnet build Volt.sln`

Expected: Clean build, 0 errors, 0 warnings (or only pre-existing warnings).

- [ ] **Step 5: Commit**

```bash
git add Volt/Editor/SyntaxManager.cs Volt/Editor/EditorControl.cs Volt/UI/MainWindow.xaml.cs Volt.Benchmarks/TokenizeBenchmarks.cs
git commit -m "refactor: move grammar state from SyntaxManager to per-EditorControl (G3/A2)"
```

---

### Task 4: Update CODE_QUALITY_REVIEW.md

**Files:**
- Modify: `CODE_QUALITY_REVIEW.md`

- [ ] **Step 1: Mark G3 and A2 as resolved**

In `CODE_QUALITY_REVIEW.md`:

1. Update the summary table counts (line 20-28): change Critical remaining from 3 to 1, resolved from 0 to 2.

2. Update G3 section (line 95-97):
```markdown
### G3. Thread safety of SyntaxManager (Critical) — RESOLVED
`_activeGrammar` removed from `SyntaxManager`. Grammar state is now per-`EditorControl` — each editor stores its own `SyntaxDefinition?` and passes it to `Tokenize()`. `SyntaxManager` is now a stateless grammar registry.
```

3. Update A2 section (line 117-118):
```markdown
### A2. Shared grammar across all editors (Critical) — RESOLVED
`_activeGrammar` removed. Each `EditorControl` owns its grammar via `_grammar` field, set by `SetGrammar()`. `SyntaxManager.GetDefinition()` provides pure lookups with no shared mutable state.
```

4. Update the summary table rows for G3 and A2 (lines 169-170) to show **RESOLVED** instead of **OPEN**.

- [ ] **Step 2: Commit**

```bash
git add CODE_QUALITY_REVIEW.md
git commit -m "docs: mark G3 and A2 as resolved in code quality review"
```
