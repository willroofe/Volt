# Unit Test Project Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a xUnit test project with foundation tests for the 6 core logic classes, resolving G1.

**Architecture:** New `Volt.Tests` project referencing `Volt.csproj`, one test file per class. `WrapLayout` is `internal` so the main project needs `InternalsVisibleTo`. All classes under test are pure logic with no WPF dependencies.

**Tech Stack:** xUnit 2.9+, .NET 10, `dotnet test`

---

### Task 1: Scaffold test project

**Files:**
- Create: `Volt.Tests/Volt.Tests.csproj`
- Modify: `Volt.sln` (add project via `dotnet sln`)
- Modify: `Volt/Volt.csproj` (add `InternalsVisibleTo` for `WrapLayout`)

- [ ] **Step 1: Create the test project**

```bash
dotnet new xunit -n Volt.Tests -o Volt.Tests --framework net10.0-windows
```

- [ ] **Step 2: Update the csproj to add WPF support and project reference**

Replace the contents of `Volt.Tests/Volt.Tests.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Volt\Volt.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Add InternalsVisibleTo for WrapLayout access**

In `Volt/Volt.csproj`, add inside the `<PropertyGroup>`:

```xml
<InternalsVisibleTo Include="Volt.Tests" />
```

Note: In modern .NET SDK-style projects, `InternalsVisibleTo` goes in the csproj as an `<ItemGroup>` entry. Add this after the existing `<PropertyGroup>`:

```xml
<ItemGroup>
    <InternalsVisibleTo Include="Volt.Tests" />
</ItemGroup>
```

- [ ] **Step 4: Add the test project to the solution**

```bash
dotnet sln Volt.sln add Volt.Tests/Volt.Tests.csproj
```

- [ ] **Step 5: Delete the auto-generated UnitTest1.cs**

Delete `Volt.Tests/UnitTest1.cs` (created by `dotnet new xunit`).

- [ ] **Step 6: Build and verify**

```bash
dotnet build Volt.sln
```

Expected: Clean build, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add Volt.Tests/Volt.Tests.csproj Volt/Volt.csproj Volt.sln
git commit -m "chore: add Volt.Tests xUnit project scaffold"
```

---

### Task 2: TextBuffer tests

**Files:**
- Create: `Volt.Tests/TextBufferTests.cs`

- [ ] **Step 1: Write TextBuffer tests**

Create `Volt.Tests/TextBufferTests.cs`:

```csharp
using Volt;

namespace Volt.Tests;

public class TextBufferTests
{
    [Fact]
    public void SetContent_GetContent_Roundtrip()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello\nworld\nfoo", tabSize: 4);

        Assert.Equal(3, buf.Count);
        Assert.Equal("hello", buf[0]);
        Assert.Equal("world", buf[1]);
        Assert.Equal("foo", buf[2]);
        Assert.Equal("hello\nworld\nfoo", buf.GetContent());
    }

    [Fact]
    public void InsertAt_InsertsTextAtColumn()
    {
        var buf = new TextBuffer();
        buf.SetContent("abcd", tabSize: 4);

        buf.InsertAt(0, 2, "XY");

        Assert.Equal("abXYcd", buf[0]);
    }

    [Fact]
    public void DeleteAt_RemovesCharacters()
    {
        var buf = new TextBuffer();
        buf.SetContent("abcdef", tabSize: 4);

        buf.DeleteAt(0, 1, 3);

        Assert.Equal("aef", buf[0]);
    }

    [Fact]
    public void ReplaceAt_ReplacesRange()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello world", tabSize: 4);

        buf.ReplaceAt(0, 6, 5, "there");

        Assert.Equal("hello there", buf[0]);
    }

    [Fact]
    public void JoinWithNext_MergesLines()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello\nworld", tabSize: 4);

        buf.JoinWithNext(0);

        Assert.Equal(1, buf.Count);
        Assert.Equal("helloworld", buf[0]);
    }

    [Fact]
    public void IsDirty_TracksModifications()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello", tabSize: 4);

        Assert.False(buf.IsDirty);

        buf.InsertAt(0, 5, "!");
        buf.IsDirty = true;

        Assert.True(buf.IsDirty);

        buf.IsDirty = false;
        Assert.False(buf.IsDirty);
    }

    [Fact]
    public void ExpandTabs_ConvertsTabsToSpaces()
    {
        Assert.Equal("    hello", TextBuffer.ExpandTabs("\thello", 4));
        Assert.Equal("a   b", TextBuffer.ExpandTabs("a\tb", 4));
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test Volt.Tests --verbosity normal
```

Expected: 7 tests pass.

- [ ] **Step 3: Commit**

```bash
git add Volt.Tests/TextBufferTests.cs
git commit -m "test: add TextBuffer foundation tests"
```

---

### Task 3: UndoManager tests

**Files:**
- Create: `Volt.Tests/UndoManagerTests.cs`

- [ ] **Step 1: Write UndoManager tests**

Create `Volt.Tests/UndoManagerTests.cs`:

```csharp
using Volt;
using static Volt.UndoManager;

namespace Volt.Tests;

public class UndoManagerTests
{
    private static UndoEntry MakeEntry(int startLine = 0) =>
        new(startLine, ["before"], ["after"], 0, 0, 0, 5);

    [Fact]
    public void PushUndo_RestoresEntry()
    {
        var mgr = new UndoManager();
        var entry = MakeEntry();

        mgr.Push(entry);

        Assert.True(mgr.CanUndo);
        Assert.Equal(1, mgr.UndoCount);

        var restored = mgr.Undo();

        Assert.Same(entry, restored);
        Assert.False(mgr.CanUndo);
    }

    [Fact]
    public void Redo_RestoresAfterUndo()
    {
        var mgr = new UndoManager();
        mgr.Push(MakeEntry());

        mgr.Undo();

        Assert.True(mgr.CanRedo);

        var redone = mgr.Redo();

        Assert.NotNull(redone);
        Assert.True(mgr.CanUndo);
        Assert.False(mgr.CanRedo);
    }

    [Fact]
    public void Push_ClearsRedoStack()
    {
        var mgr = new UndoManager();
        mgr.Push(MakeEntry());
        mgr.Undo();

        Assert.True(mgr.CanRedo);

        mgr.Push(MakeEntry());

        Assert.False(mgr.CanRedo);
    }

    [Fact]
    public void Push_EvictsOldestAt200()
    {
        var mgr = new UndoManager();
        for (int i = 0; i < 200; i++)
            Assert.False(mgr.Push(MakeEntry(i)));

        Assert.Equal(200, mgr.UndoCount);

        Assert.True(mgr.Push(MakeEntry(200)));

        Assert.Equal(200, mgr.UndoCount);
    }

    [Fact]
    public void Clear_ResetsBothStacks()
    {
        var mgr = new UndoManager();
        mgr.Push(MakeEntry());
        mgr.Push(MakeEntry());
        mgr.Undo();

        mgr.Clear();

        Assert.False(mgr.CanUndo);
        Assert.False(mgr.CanRedo);
        Assert.Equal(0, mgr.UndoCount);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test Volt.Tests --verbosity normal
```

Expected: All tests pass (7 previous + 5 new = 12 total).

- [ ] **Step 3: Commit**

```bash
git add Volt.Tests/UndoManagerTests.cs
git commit -m "test: add UndoManager foundation tests"
```

---

### Task 4: FindManager tests

**Files:**
- Create: `Volt.Tests/FindManagerTests.cs`

- [ ] **Step 1: Write FindManager tests**

Create `Volt.Tests/FindManagerTests.cs`:

```csharp
using Volt;

namespace Volt.Tests;

public class FindManagerTests
{
    private static TextBuffer MakeBuffer(string content)
    {
        var buf = new TextBuffer();
        buf.SetContent(content, tabSize: 4);
        return buf;
    }

    [Fact]
    public void Search_FindsMatchesWithPositions()
    {
        var buf = MakeBuffer("hello world\nhello again");
        var find = new FindManager();

        find.Search(buf, "hello", matchCase: false, caretLine: 0, caretCol: 0);

        Assert.Equal(2, find.MatchCount);
        Assert.Equal((0, 0, 5), find.Matches[0]);
        Assert.Equal((1, 0, 5), find.Matches[1]);
    }

    [Fact]
    public void Search_CaseSensitive_FiltersCorrectly()
    {
        var buf = MakeBuffer("Hello hello HELLO");
        var find = new FindManager();

        find.Search(buf, "hello", matchCase: true, caretLine: 0, caretCol: 0);

        Assert.Equal(1, find.MatchCount);
        Assert.Equal((0, 6, 5), find.Matches[0]);
    }

    [Fact]
    public void MoveNext_WrapsAround()
    {
        var buf = MakeBuffer("aaa\naaa");
        var find = new FindManager();
        find.Search(buf, "aaa", matchCase: false, caretLine: 0, caretCol: 0);

        Assert.Equal(0, find.CurrentIndex);

        find.MoveNext();
        Assert.Equal(1, find.CurrentIndex);

        find.MoveNext();
        Assert.Equal(0, find.CurrentIndex);
    }

    [Fact]
    public void MovePrevious_WrapsAround()
    {
        var buf = MakeBuffer("aaa\naaa");
        var find = new FindManager();
        find.Search(buf, "aaa", matchCase: false, caretLine: 0, caretCol: 0);

        Assert.Equal(0, find.CurrentIndex);

        find.MovePrevious();
        Assert.Equal(1, find.CurrentIndex);
    }

    [Fact]
    public void Search_NoMatch_ReturnsZero()
    {
        var buf = MakeBuffer("hello world");
        var find = new FindManager();

        find.Search(buf, "xyz", matchCase: false, caretLine: 0, caretCol: 0);

        Assert.Equal(0, find.MatchCount);
        Assert.Null(find.GetCurrentMatch());
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test Volt.Tests --verbosity normal
```

Expected: All tests pass (12 previous + 5 new = 17 total).

- [ ] **Step 3: Commit**

```bash
git add Volt.Tests/FindManagerTests.cs
git commit -m "test: add FindManager foundation tests"
```

---

### Task 5: BracketMatcher tests

**Files:**
- Create: `Volt.Tests/BracketMatcherTests.cs`

- [ ] **Step 1: Write BracketMatcher tests**

Create `Volt.Tests/BracketMatcherTests.cs`:

```csharp
using Volt;

namespace Volt.Tests;

public class BracketMatcherTests
{
    private static TextBuffer MakeBuffer(string content)
    {
        var buf = new TextBuffer();
        buf.SetContent(content, tabSize: 4);
        return buf;
    }

    [Fact]
    public void FindMatch_MatchesParensOnSameLine()
    {
        var buf = MakeBuffer("foo(bar)");
        // Caret on '(' at col 3
        var result = BracketMatcher.FindMatch(buf, 0, 3);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.line);
        Assert.Equal(3, result.Value.col);
        Assert.Equal(0, result.Value.matchLine);
        Assert.Equal(7, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_MatchesNestedBrackets()
    {
        var buf = MakeBuffer("{a[b(c)d]e}");
        // Caret on outer '{' at col 0
        var result = BracketMatcher.FindMatch(buf, 0, 0);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.col);
        Assert.Equal(10, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_CrossLineMatching()
    {
        var buf = MakeBuffer("if {\n  x\n}");
        // Caret on '{' at line 0 col 3
        var result = BracketMatcher.FindMatch(buf, 0, 3);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.line);
        Assert.Equal(3, result.Value.col);
        Assert.Equal(2, result.Value.matchLine);
        Assert.Equal(0, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_UnmatchedBracket_ReturnsNull()
    {
        var buf = MakeBuffer("(unclosed");
        // Caret on '(' at col 0
        var result = BracketMatcher.FindMatch(buf, 0, 0);

        Assert.Null(result);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test Volt.Tests --verbosity normal
```

Expected: All tests pass (17 previous + 4 new = 21 total).

- [ ] **Step 3: Commit**

```bash
git add Volt.Tests/BracketMatcherTests.cs
git commit -m "test: add BracketMatcher foundation tests"
```

---

### Task 6: WrapLayout tests

**Files:**
- Create: `Volt.Tests/WrapLayoutTests.cs`

- [ ] **Step 1: Write WrapLayout tests**

Create `Volt.Tests/WrapLayoutTests.cs`:

```csharp
using Volt;

namespace Volt.Tests;

public class WrapLayoutTests
{
    private static TextBuffer MakeBuffer(string content)
    {
        var buf = new TextBuffer();
        buf.SetContent(content, tabSize: 4);
        return buf;
    }

    [Fact]
    public void WrapOff_VisualLineEqualsLogicalLine()
    {
        var buf = MakeBuffer("short\nlines\nhere");
        var wrap = new WrapLayout();

        wrap.Recalculate(wordWrap: false, buf, textAreaWidth: 500, charWidth: 8);

        Assert.Equal(3, wrap.TotalVisualLines);
        Assert.Equal(0, wrap.LogicalToVisualLine(wordWrap: false, 0));
        Assert.Equal(1, wrap.LogicalToVisualLine(wordWrap: false, 1));
        Assert.Equal(2, wrap.LogicalToVisualLine(wordWrap: false, 2));
    }

    [Fact]
    public void WrapOn_LongLineProducesMultipleVisualLines()
    {
        // 20 chars per line, charWidth=8, textAreaWidth=80 → 10 chars per visual line
        var buf = MakeBuffer("12345678901234567890\nshort");
        var wrap = new WrapLayout();

        wrap.Recalculate(wordWrap: true, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(10, wrap.CharsPerVisualLine);
        Assert.Equal(2, wrap.VisualLineCount(wordWrap: true, 0)); // 20 chars / 10 = 2 visual lines
        Assert.Equal(1, wrap.VisualLineCount(wordWrap: true, 1)); // "short" fits in 1
        Assert.Equal(3, wrap.TotalVisualLines); // 2 + 1
    }

    [Fact]
    public void VisualToLogical_Roundtrip()
    {
        var buf = MakeBuffer("12345678901234567890\nshort");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, buf, textAreaWidth: 80, charWidth: 8);

        // Visual line 0 → logical line 0, wrap 0
        var (log0, wrapIdx0) = wrap.VisualToLogical(wordWrap: true, 0, buf.Count);
        Assert.Equal(0, log0);
        Assert.Equal(0, wrapIdx0);

        // Visual line 1 → logical line 0, wrap 1
        var (log1, wrapIdx1) = wrap.VisualToLogical(wordWrap: true, 1, buf.Count);
        Assert.Equal(0, log1);
        Assert.Equal(1, wrapIdx1);

        // Visual line 2 → logical line 1, wrap 0
        var (log2, wrapIdx2) = wrap.VisualToLogical(wordWrap: true, 2, buf.Count);
        Assert.Equal(1, log2);
        Assert.Equal(0, wrapIdx2);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test Volt.Tests --verbosity normal
```

Expected: All tests pass (21 previous + 3 new = 24 total).

- [ ] **Step 3: Commit**

```bash
git add Volt.Tests/WrapLayoutTests.cs
git commit -m "test: add WrapLayout foundation tests"
```

---

### Task 7: SyntaxManager tests

**Files:**
- Create: `Volt.Tests/SyntaxManagerTests.cs`

- [ ] **Step 1: Write SyntaxManager tests**

Create `Volt.Tests/SyntaxManagerTests.cs`:

```csharp
using Volt;

namespace Volt.Tests;

public class SyntaxManagerTests
{
    private static SyntaxManager CreateInitialized()
    {
        var mgr = new SyntaxManager();
        mgr.Initialize();
        return mgr;
    }

    [Fact]
    public void GetDefinition_KnownExtension_ReturnsGrammar()
    {
        var mgr = CreateInitialized();

        var def = mgr.GetDefinition(".pl");

        Assert.NotNull(def);
        Assert.Equal("Perl", def!.Name);
    }

    [Fact]
    public void GetDefinition_UnknownExtension_ReturnsNull()
    {
        var mgr = CreateInitialized();

        Assert.Null(mgr.GetDefinition(".xyz"));
    }

    [Fact]
    public void GetDefinition_NullOrEmpty_ReturnsNull()
    {
        var mgr = CreateInitialized();

        Assert.Null(mgr.GetDefinition(null));
        Assert.Null(mgr.GetDefinition(""));
    }

    [Fact]
    public void Tokenize_NullGrammar_ReturnsEmpty()
    {
        var mgr = CreateInitialized();

        var tokens = mgr.Tokenize("my $foo = 42;", grammar: null);

        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_SimplePerlLine_ProducesTokens()
    {
        var mgr = CreateInitialized();
        var grammar = mgr.GetDefinition(".pl")!;

        var tokens = mgr.Tokenize("my $foo = 42;  # comment", grammar);

        Assert.NotEmpty(tokens);
        Assert.Contains(tokens, t => t.Scope == "keyword");
        Assert.Contains(tokens, t => t.Scope == "variable");
        Assert.Contains(tokens, t => t.Scope == "number");
        Assert.Contains(tokens, t => t.Scope == "comment");
    }

    [Fact]
    public void Tokenize_UnclosedString_CarriesStateAcrossLines()
    {
        var mgr = CreateInitialized();
        var grammar = mgr.GetDefinition(".pl")!;

        mgr.Tokenize("my $x = \"hello", grammar, mgr.DefaultState, out var midState);

        Assert.NotNull(midState.OpenQuote);
        Assert.Equal('"', midState.OpenQuote);

        mgr.Tokenize("world\";", grammar, midState, out var endState);

        Assert.Null(endState.OpenQuote);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test Volt.Tests --verbosity normal
```

Expected: All tests pass (24 previous + 6 new = 30 total).

- [ ] **Step 3: Commit**

```bash
git add Volt.Tests/SyntaxManagerTests.cs
git commit -m "test: add SyntaxManager foundation tests"
```

---

### Task 8: Update CODE_QUALITY_REVIEW.md

**Files:**
- Modify: `CODE_QUALITY_REVIEW.md`

- [ ] **Step 1: Mark G1 as resolved**

In `CODE_QUALITY_REVIEW.md`:

1. Update the summary table counts: Critical remaining from 1 to 0, resolved from 2 to 3.

2. Update G1 section (around line 87-88):
```markdown
### G1. No test framework configured (Critical) — RESOLVED
Added `Volt.Tests` xUnit project with ~30 foundation tests covering `TextBuffer`, `UndoManager`, `FindManager`, `BracketMatcher`, `WrapLayout`, and `SyntaxManager`. Run with `dotnet test Volt.Tests`.
```

3. Update the summary table row for G1 to show **RESOLVED** instead of **OPEN**.

- [ ] **Step 2: Commit**

```bash
git add CODE_QUALITY_REVIEW.md
git commit -m "docs: mark G1 as resolved in code quality review"
```
