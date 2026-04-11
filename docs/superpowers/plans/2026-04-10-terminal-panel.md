# Terminal Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an integrated multi-session terminal to Volt as a dockable `TerminalPanel` that uses ConPTY for the pty, a hand-rolled Paul Williams VT state machine for parsing, and Volt's existing `FontManager.DrawGlyphRun` path for rendering.

**Architecture:** Pure-C# core (`Volt/Terminal/`) separated from WPF layer (`Volt/UI/Terminal/`). Three pipelines: shell output → parser → dispatcher → grid → view; keyboard → encoder → pty; resize → pty. Multi-session hosted inside one `TerminalPanel` instance because Volt's panel system allows only one instance per panel type.

**Tech Stack:** C# / .NET 10 / WPF, direct ConPTY P/Invoke (no external dep), xUnit for tests, BenchmarkDotNet for benchmarks.

**Reference spec:** `docs/superpowers/specs/2026-04-10-terminal-panel-design.md`

**Test commands:**
- `dotnet test Volt.Tests` — all unit tests (integration tests skipped by trait)
- `dotnet test Volt.Tests --filter Category=Integration` — ConPTY integration tests (opt-in)
- `dotnet build Volt.sln` — full build

---

## Stage 1 — Grid Foundations

### Task 1: Cell struct + CellAttr flags

**Files:**
- Create: `Volt/Terminal/Buffer/Cell.cs`
- Test: `Volt.Tests/Terminal/CellTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Volt.Tests/Terminal/CellTests.cs
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class CellTests
{
    [Fact]
    public void DefaultCell_HasBlankGlyphAndDefaultColors()
    {
        var cell = new Cell();
        Assert.Equal('\0', cell.Glyph);
        Assert.Equal(-1, cell.FgIndex);
        Assert.Equal(-1, cell.BgIndex);
        Assert.Equal(CellAttr.None, cell.Attr);
    }

    [Fact]
    public void CellAttr_SupportsFlagCombination()
    {
        var combined = CellAttr.Bold | CellAttr.Italic | CellAttr.Underline;
        Assert.True(combined.HasFlag(CellAttr.Bold));
        Assert.True(combined.HasFlag(CellAttr.Italic));
        Assert.True(combined.HasFlag(CellAttr.Underline));
        Assert.False(combined.HasFlag(CellAttr.Inverse));
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test Volt.Tests --filter CellTests`
Expected: FAIL with "The type or namespace name 'Cell' could not be found".

- [ ] **Step 3: Implement Cell + CellAttr**

```csharp
// Volt/Terminal/Buffer/Cell.cs
using System;

namespace Volt;

[Flags]
public enum CellAttr : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Inverse = 1 << 4,
    Strikethrough = 1 << 5,
}

/// <summary>
/// One terminal grid cell. FgIndex/BgIndex semantics:
/// -1            = default (theme fg/bg)
/// 0..15         = ANSI 16 palette (from theme)
/// 16..255       = xterm 256 cube + grayscale ramp
/// &lt; -1       = truecolor; -(index+2) into TerminalGrid's truecolor side-table
/// </summary>
public struct Cell
{
    public char Glyph;
    public int FgIndex;
    public int BgIndex;
    public CellAttr Attr;

    public static Cell Blank => new Cell { Glyph = ' ', FgIndex = -1, BgIndex = -1, Attr = CellAttr.None };
}
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test Volt.Tests --filter CellTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Buffer/Cell.cs Volt.Tests/Terminal/CellTests.cs
git commit -m "feat(terminal): add Cell struct and CellAttr flags"
```

---

### Task 2: GridRegion dirty tracking

**Files:**
- Create: `Volt/Terminal/Buffer/GridRegion.cs`
- Test: `Volt.Tests/Terminal/GridRegionTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Volt.Tests/Terminal/GridRegionTests.cs
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class GridRegionTests
{
    [Fact]
    public void Empty_ReturnsSentinel()
    {
        var r = new GridRegion();
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void MarkDirty_ExpandsRange()
    {
        var r = new GridRegion();
        r.MarkDirty(5);
        r.MarkDirty(3);
        r.MarkDirty(10);
        Assert.False(r.IsEmpty);
        Assert.Equal(3, r.MinRow);
        Assert.Equal(10, r.MaxRow);
    }

    [Fact]
    public void Clear_ResetsToEmpty()
    {
        var r = new GridRegion();
        r.MarkDirty(5);
        r.Clear();
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void MarkRange_ExpandsOverMultipleRows()
    {
        var r = new GridRegion();
        r.MarkDirtyRange(4, 8);
        Assert.Equal(4, r.MinRow);
        Assert.Equal(8, r.MaxRow);
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test Volt.Tests --filter GridRegionTests`
Expected: FAIL — type not found.

- [ ] **Step 3: Implement GridRegion**

```csharp
// Volt/Terminal/Buffer/GridRegion.cs
namespace Volt;

/// <summary>
/// Tracks a contiguous inclusive row range that needs redrawing.
/// Not thread-safe — only touched from the UI thread.
/// </summary>
public struct GridRegion
{
    public int MinRow { get; private set; }
    public int MaxRow { get; private set; }
    public bool IsEmpty { get; private set; }

    public GridRegion()
    {
        MinRow = int.MaxValue;
        MaxRow = int.MinValue;
        IsEmpty = true;
    }

    public void MarkDirty(int row)
    {
        if (IsEmpty)
        {
            MinRow = row;
            MaxRow = row;
            IsEmpty = false;
            return;
        }
        if (row < MinRow) MinRow = row;
        if (row > MaxRow) MaxRow = row;
    }

    public void MarkDirtyRange(int startRow, int endRow)
    {
        MarkDirty(startRow);
        MarkDirty(endRow);
    }

    public void Clear()
    {
        MinRow = int.MaxValue;
        MaxRow = int.MinValue;
        IsEmpty = true;
    }
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test Volt.Tests --filter GridRegionTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Buffer/GridRegion.cs Volt.Tests/Terminal/GridRegionTests.cs
git commit -m "feat(terminal): add GridRegion dirty row tracker"
```

---

### Task 3: TerminalGrid basics (construction, WriteCell, CellAt, dirty hook)

**Files:**
- Create: `Volt/Terminal/Buffer/TerminalGrid.cs`
- Test: `Volt.Tests/Terminal/TerminalGridTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Volt.Tests/Terminal/TerminalGridTests.cs
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class TerminalGridTests
{
    [Fact]
    public void Constructor_InitializesBlankGrid()
    {
        var g = new TerminalGrid(rows: 24, cols: 80, scrollbackLines: 100);
        Assert.Equal(24, g.Rows);
        Assert.Equal(80, g.Cols);
        Assert.Equal(' ', g.CellAt(0, 0).Glyph);
        Assert.Equal(' ', g.CellAt(23, 79).Glyph);
        Assert.Equal((0, 0), g.Cursor);
        Assert.True(g.CursorVisible);
        Assert.False(g.UsingAltBuffer);
    }

    [Fact]
    public void WriteCell_StoresGlyphAndMarksDirty()
    {
        var g = new TerminalGrid(24, 80, 100);
        g.WriteCell(5, 10, 'X', CellAttr.Bold);
        Assert.Equal('X', g.CellAt(5, 10).Glyph);
        Assert.Equal(CellAttr.Bold, g.CellAt(5, 10).Attr);
        Assert.Equal(5, g.Dirty.MinRow);
        Assert.Equal(5, g.Dirty.MaxRow);
    }

    [Fact]
    public void WriteCell_OutsideBounds_IsClamped()
    {
        var g = new TerminalGrid(24, 80, 100);
        g.WriteCell(-1, -1, 'A', CellAttr.None);
        g.WriteCell(999, 999, 'B', CellAttr.None);
        // Should not throw; out-of-bounds writes are silently dropped
        Assert.Equal(' ', g.CellAt(0, 0).Glyph);
    }

    [Fact]
    public void ClearDirty_ResetsRegion()
    {
        var g = new TerminalGrid(24, 80, 100);
        g.WriteCell(3, 5, 'Z', CellAttr.None);
        g.ClearDirty();
        Assert.True(g.Dirty.IsEmpty);
    }

    [Fact]
    public void ChangedEvent_FiresOnWrite()
    {
        var g = new TerminalGrid(24, 80, 100);
        int fired = 0;
        g.Changed += () => fired++;
        g.WriteCell(1, 1, 'X', CellAttr.None);
        Assert.Equal(1, fired);
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: FAIL — type not found.

- [ ] **Step 3: Implement TerminalGrid scaffold**

```csharp
// Volt/Terminal/Buffer/TerminalGrid.cs
using System;
using System.Collections.Generic;

namespace Volt;

public sealed partial class TerminalGrid
{
    private Cell[,] _main;
    private Cell[,]? _alt;
    private readonly int _scrollbackLines;
    private GridRegion _dirty;

    public int Rows { get; private set; }
    public int Cols { get; private set; }
    public (int row, int col) Cursor { get; private set; }
    public bool CursorVisible { get; set; } = true;
    public bool UsingAltBuffer { get; private set; }
    public GridRegion Dirty => _dirty;

    public event Action? Changed;

    public TerminalGrid(int rows, int cols, int scrollbackLines)
    {
        Rows = Math.Max(1, rows);
        Cols = Math.Max(1, cols);
        _scrollbackLines = Math.Max(0, scrollbackLines);
        _main = AllocBlank(Rows, Cols);
        _dirty = new GridRegion();
    }

    private static Cell[,] AllocBlank(int rows, int cols)
    {
        var buf = new Cell[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                buf[r, c] = Cell.Blank;
        return buf;
    }

    public ref Cell CellAt(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols)
            return ref _sink;
        return ref ActiveBuffer[row, col];
    }

    private static Cell _sink = Cell.Blank;
    private Cell[,] ActiveBuffer => UsingAltBuffer ? _alt! : _main;

    public void WriteCell(int row, int col, char ch, CellAttr attr)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return;
        ref var cell = ref ActiveBuffer[row, col];
        cell.Glyph = ch;
        cell.Attr = attr;
        _dirty.MarkDirty(row);
        Changed?.Invoke();
    }

    public void ClearDirty() => _dirty.Clear();
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Buffer/TerminalGrid.cs Volt.Tests/Terminal/TerminalGridTests.cs
git commit -m "feat(terminal): add TerminalGrid scaffold with WriteCell/CellAt"
```

---

### Task 4: TerminalGrid cursor movement, pen state, autowrap

**Files:**
- Modify: `Volt/Terminal/Buffer/TerminalGrid.cs`
- Modify: `Volt.Tests/Terminal/TerminalGridTests.cs`

- [ ] **Step 1: Add failing tests for cursor + pen + autowrap**

Append to `Volt.Tests/Terminal/TerminalGridTests.cs`:

```csharp
    [Fact]
    public void SetCursor_ClampsToGrid()
    {
        var g = new TerminalGrid(24, 80, 100);
        g.SetCursor(5, 10);
        Assert.Equal((5, 10), g.Cursor);
        g.SetCursor(999, 999);
        Assert.Equal((23, 79), g.Cursor);
        g.SetCursor(-5, -5);
        Assert.Equal((0, 0), g.Cursor);
    }

    [Fact]
    public void PutGlyph_WritesAtCursorAndAdvances()
    {
        var g = new TerminalGrid(24, 80, 100);
        g.SetCursor(2, 3);
        g.PutGlyph('H');
        g.PutGlyph('i');
        Assert.Equal('H', g.CellAt(2, 3).Glyph);
        Assert.Equal('i', g.CellAt(2, 4).Glyph);
        Assert.Equal((2, 5), g.Cursor);
    }

    [Fact]
    public void PutGlyph_AtRightEdge_SetsPendingWrap()
    {
        var g = new TerminalGrid(24, 80, 100);
        g.SetCursor(0, 79);
        g.PutGlyph('A');
        // Cursor stays at last col with pending wrap; next glyph wraps to next line col 0
        Assert.Equal('A', g.CellAt(0, 79).Glyph);
        g.PutGlyph('B');
        Assert.Equal('B', g.CellAt(1, 0).Glyph);
        Assert.Equal((1, 1), g.Cursor);
    }

    [Fact]
    public void Pen_CarriesAttributesIntoPutGlyph()
    {
        var g = new TerminalGrid(24, 80, 100);
        g.Pen = new Cell { FgIndex = 1, BgIndex = 4, Attr = CellAttr.Bold };
        g.PutGlyph('X');
        var cell = g.CellAt(0, 0);
        Assert.Equal('X', cell.Glyph);
        Assert.Equal(1, cell.FgIndex);
        Assert.Equal(4, cell.BgIndex);
        Assert.Equal(CellAttr.Bold, cell.Attr);
    }
```

- [ ] **Step 2: Run new tests, verify they fail**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: 4 new failures (methods not defined).

- [ ] **Step 3: Extend TerminalGrid with cursor/pen/wrap**

Add to `Volt/Terminal/Buffer/TerminalGrid.cs` inside the class body:

```csharp
    public Cell Pen = Cell.Blank with { FgIndex = -1, BgIndex = -1, Attr = CellAttr.None };
    private bool _pendingWrap;

    public void SetCursor(int row, int col)
    {
        int r = Math.Clamp(row, 0, Rows - 1);
        int c = Math.Clamp(col, 0, Cols - 1);
        Cursor = (r, c);
        _pendingWrap = false;
    }

    public void PutGlyph(char ch)
    {
        var (r, c) = Cursor;
        if (_pendingWrap)
        {
            _pendingWrap = false;
            if (r + 1 < Rows)
            {
                r++;
                c = 0;
            }
            else
            {
                ScrollUp(1);
                c = 0;
            }
        }

        ref var cell = ref ActiveBuffer[r, c];
        cell.Glyph = ch;
        cell.FgIndex = Pen.FgIndex;
        cell.BgIndex = Pen.BgIndex;
        cell.Attr = Pen.Attr;
        _dirty.MarkDirty(r);

        if (c + 1 >= Cols)
        {
            _pendingWrap = true;
            Cursor = (r, c);
        }
        else
        {
            Cursor = (r, c + 1);
        }
        Changed?.Invoke();
    }

    public void ScrollUp(int n)
    {
        // Stub — fully implemented in Task 5; this minimum lets PutGlyph wrap at bottom.
        for (int row = 0; row < Rows - n; row++)
            for (int col = 0; col < Cols; col++)
                ActiveBuffer[row, col] = ActiveBuffer[row + n, col];
        for (int row = Rows - n; row < Rows; row++)
            for (int col = 0; col < Cols; col++)
                ActiveBuffer[row, col] = Cell.Blank;
        _dirty.MarkDirtyRange(0, Rows - 1);
    }
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: PASS (9 tests total).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Buffer/TerminalGrid.cs Volt.Tests/Terminal/TerminalGridTests.cs
git commit -m "feat(terminal): add cursor, pen, and autowrap to TerminalGrid"
```

---

### Task 5: TerminalGrid scroll region + ScrollUp/Down + Insert/DeleteLines

**Files:**
- Modify: `Volt/Terminal/Buffer/TerminalGrid.cs`
- Modify: `Volt.Tests/Terminal/TerminalGridTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void SetScrollRegion_ClampsAndApplies()
    {
        var g = new TerminalGrid(24, 80, 100);
        g.SetScrollRegion(5, 15);
        Assert.Equal(5, g.ScrollTop);
        Assert.Equal(15, g.ScrollBottom);
    }

    [Fact]
    public void ScrollUp_WithinRegion_LeavesOutsideRowsAlone()
    {
        var g = new TerminalGrid(10, 5, 100);
        for (int r = 0; r < 10; r++)
            for (int c = 0; c < 5; c++)
                g.WriteCell(r, c, (char)('0' + r), CellAttr.None);
        g.SetScrollRegion(3, 6);
        g.ScrollUp(1);
        Assert.Equal('0', g.CellAt(0, 0).Glyph); // untouched
        Assert.Equal('2', g.CellAt(2, 0).Glyph); // untouched
        Assert.Equal('4', g.CellAt(3, 0).Glyph); // row 4 moved into row 3
        Assert.Equal('6', g.CellAt(5, 0).Glyph); // row 6 moved into row 5
        Assert.Equal(' ', g.CellAt(6, 0).Glyph); // row 6 cleared
        Assert.Equal('7', g.CellAt(7, 0).Glyph); // untouched
    }

    [Fact]
    public void ScrollDown_ShiftsRowsAndBlanksTop()
    {
        var g = new TerminalGrid(5, 3, 100);
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 3; c++)
                g.WriteCell(r, c, (char)('A' + r), CellAttr.None);
        g.ScrollDown(2);
        Assert.Equal(' ', g.CellAt(0, 0).Glyph);
        Assert.Equal(' ', g.CellAt(1, 0).Glyph);
        Assert.Equal('A', g.CellAt(2, 0).Glyph);
        Assert.Equal('B', g.CellAt(3, 0).Glyph);
        Assert.Equal('C', g.CellAt(4, 0).Glyph);
    }

    [Fact]
    public void InsertLines_ShiftsDownAndBlanksAtCursor()
    {
        var g = new TerminalGrid(5, 3, 100);
        for (int r = 0; r < 5; r++) g.WriteCell(r, 0, (char)('A' + r), CellAttr.None);
        g.SetCursor(1, 0);
        g.InsertLines(2);
        Assert.Equal('A', g.CellAt(0, 0).Glyph);
        Assert.Equal(' ', g.CellAt(1, 0).Glyph);
        Assert.Equal(' ', g.CellAt(2, 0).Glyph);
        Assert.Equal('B', g.CellAt(3, 0).Glyph);
        Assert.Equal('C', g.CellAt(4, 0).Glyph);
    }

    [Fact]
    public void DeleteLines_ShiftsUpAndBlanksAtBottom()
    {
        var g = new TerminalGrid(5, 3, 100);
        for (int r = 0; r < 5; r++) g.WriteCell(r, 0, (char)('A' + r), CellAttr.None);
        g.SetCursor(1, 0);
        g.DeleteLines(2);
        Assert.Equal('A', g.CellAt(0, 0).Glyph);
        Assert.Equal('D', g.CellAt(1, 0).Glyph);
        Assert.Equal('E', g.CellAt(2, 0).Glyph);
        Assert.Equal(' ', g.CellAt(3, 0).Glyph);
        Assert.Equal(' ', g.CellAt(4, 0).Glyph);
    }
```

- [ ] **Step 2: Run tests, verify failures**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: failures for new tests.

- [ ] **Step 3: Replace the Task-4 stub of ScrollUp with full scroll-region impl + add new methods**

Replace the stub `ScrollUp` from Task 4 and add new members:

```csharp
    public int ScrollTop { get; private set; }
    public int ScrollBottom { get; private set; }

    // Called from ctor — add at end of constructor: ScrollTop = 0; ScrollBottom = Rows - 1;

    public void SetScrollRegion(int top, int bottom)
    {
        ScrollTop = Math.Clamp(top, 0, Rows - 1);
        ScrollBottom = Math.Clamp(bottom, ScrollTop, Rows - 1);
    }

    public void ScrollUp(int n)
    {
        int top = ScrollTop;
        int bot = ScrollBottom;
        n = Math.Clamp(n, 0, bot - top + 1);
        if (n == 0) return;
        // Push scrolled-off rows into scrollback (main buffer only, full-screen scroll only)
        if (!UsingAltBuffer && top == 0 && bot == Rows - 1)
            PushToScrollback(n);

        for (int row = top; row <= bot - n; row++)
            for (int col = 0; col < Cols; col++)
                ActiveBuffer[row, col] = ActiveBuffer[row + n, col];
        for (int row = bot - n + 1; row <= bot; row++)
            for (int col = 0; col < Cols; col++)
                ActiveBuffer[row, col] = Cell.Blank;
        _dirty.MarkDirtyRange(top, bot);
        Changed?.Invoke();
    }

    public void ScrollDown(int n)
    {
        int top = ScrollTop;
        int bot = ScrollBottom;
        n = Math.Clamp(n, 0, bot - top + 1);
        if (n == 0) return;
        for (int row = bot; row >= top + n; row--)
            for (int col = 0; col < Cols; col++)
                ActiveBuffer[row, col] = ActiveBuffer[row - n, col];
        for (int row = top; row < top + n; row++)
            for (int col = 0; col < Cols; col++)
                ActiveBuffer[row, col] = Cell.Blank;
        _dirty.MarkDirtyRange(top, bot);
        Changed?.Invoke();
    }

    public void InsertLines(int n)
    {
        var (r, _) = Cursor;
        if (r < ScrollTop || r > ScrollBottom) return;
        int savedTop = ScrollTop;
        SetScrollRegion(r, ScrollBottom);
        ScrollDown(n);
        SetScrollRegion(savedTop, ScrollBottom);
    }

    public void DeleteLines(int n)
    {
        var (r, _) = Cursor;
        if (r < ScrollTop || r > ScrollBottom) return;
        int savedTop = ScrollTop;
        SetScrollRegion(r, ScrollBottom);
        ScrollUp(n);
        SetScrollRegion(savedTop, ScrollBottom);
    }

    // Scrollback — actual implementation in Task 6; stub here so ScrollUp compiles.
    private void PushToScrollback(int n) { /* Task 6 */ }
```

Also update the constructor to initialize `ScrollTop`/`ScrollBottom`:

```csharp
    public TerminalGrid(int rows, int cols, int scrollbackLines)
    {
        Rows = Math.Max(1, rows);
        Cols = Math.Max(1, cols);
        _scrollbackLines = Math.Max(0, scrollbackLines);
        _main = AllocBlank(Rows, Cols);
        _dirty = new GridRegion();
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
    }
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: PASS (14 tests total).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Buffer/TerminalGrid.cs Volt.Tests/Terminal/TerminalGridTests.cs
git commit -m "feat(terminal): add scroll region, ScrollUp/Down, Insert/DeleteLines"
```

---

### Task 6: TerminalGrid scrollback ring buffer

**Files:**
- Modify: `Volt/Terminal/Buffer/TerminalGrid.cs`
- Modify: `Volt.Tests/Terminal/TerminalGridTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void ScrollbackRow_RetrievesOldestFirst()
    {
        var g = new TerminalGrid(3, 3, scrollbackLines: 10);
        // Fill and scroll 5 times
        for (int iter = 0; iter < 5; iter++)
        {
            g.WriteCell(0, 0, (char)('A' + iter), CellAttr.None);
            g.ScrollUp(1);
        }
        Assert.Equal(5, g.ScrollbackCount);
        // Row -1 is newest in scrollback; row -5 is oldest
        Assert.Equal('E', g.CellAt(-1, 0).Glyph);
        Assert.Equal('A', g.CellAt(-5, 0).Glyph);
    }

    [Fact]
    public void Scrollback_EvictsOldestOnOverflow()
    {
        var g = new TerminalGrid(3, 3, scrollbackLines: 4);
        for (int iter = 0; iter < 10; iter++)
        {
            g.WriteCell(0, 0, (char)('0' + iter), CellAttr.None);
            g.ScrollUp(1);
        }
        Assert.Equal(4, g.ScrollbackCount);
        // Last 4 scrolled off are '6','7','8','9'
        Assert.Equal('9', g.CellAt(-1, 0).Glyph);
        Assert.Equal('6', g.CellAt(-4, 0).Glyph);
    }

    [Fact]
    public void AltBuffer_DoesNotWriteToScrollback()
    {
        var g = new TerminalGrid(3, 3, scrollbackLines: 10);
        g.SwitchToAltBuffer();
        for (int i = 0; i < 5; i++)
        {
            g.WriteCell(0, 0, 'X', CellAttr.None);
            g.ScrollUp(1);
        }
        Assert.Equal(0, g.ScrollbackCount);
    }
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: failures (`ScrollbackCount`, `SwitchToAltBuffer` missing; `CellAt` negative-row access).

- [ ] **Step 3: Implement scrollback ring**

Add to `TerminalGrid`:

```csharp
    private Cell[][] _scrollback = Array.Empty<Cell[]>();
    private int _scrollbackHead; // points at oldest
    private int _scrollbackCount;

    public int ScrollbackCount => _scrollbackCount;

    private void EnsureScrollbackCapacity()
    {
        if (_scrollback.Length == _scrollbackLines) return;
        _scrollback = new Cell[_scrollbackLines][];
    }

    private void PushRowToScrollback(Cell[] row)
    {
        if (_scrollbackLines == 0) return;
        EnsureScrollbackCapacity();
        int writeIndex = (_scrollbackHead + _scrollbackCount) % _scrollbackLines;
        _scrollback[writeIndex] = row;
        if (_scrollbackCount < _scrollbackLines)
            _scrollbackCount++;
        else
            _scrollbackHead = (_scrollbackHead + 1) % _scrollbackLines;
    }

    // Replace Task-5 stub
    private new void PushToScrollback(int n)
    {
        // NB: Task 5 stub is now gone — remove the earlier stub definition.
        for (int i = 0; i < n; i++)
        {
            var row = new Cell[Cols];
            for (int c = 0; c < Cols; c++) row[c] = ActiveBuffer[i, c];
            PushRowToScrollback(row);
        }
    }
```

**Important:** Delete the Task-5 stub `private void PushToScrollback(int n) { /* Task 6 */ }` — there should only be one definition, without `new`. The `new` keyword shown above is just to flag "replacing the stub" — the real method should be declared as `private void PushToScrollback(int n)`.

Extend `CellAt` to support negative rows into scrollback:

```csharp
    public ref Cell CellAt(int row, int col)
    {
        if (col < 0 || col >= Cols) return ref _sink;
        if (row >= 0 && row < Rows)
            return ref ActiveBuffer[row, col];
        // Negative row = scrollback; -1 = newest, -N = oldest still live
        int scrollbackIndex = _scrollbackCount + row; // row is negative → positive offset from head
        if (scrollbackIndex < 0 || scrollbackIndex >= _scrollbackCount) return ref _sink;
        int ringIndex = (_scrollbackHead + scrollbackIndex) % _scrollbackLines;
        return ref _scrollback[ringIndex][col];
    }
```

Add alt-buffer stubs (real impl in Task 7 — declare so this task's tests compile):

```csharp
    public void SwitchToAltBuffer()
    {
        if (UsingAltBuffer) return;
        _alt = AllocBlank(Rows, Cols);
        UsingAltBuffer = true;
    }

    public void SwitchToMainBuffer()
    {
        if (!UsingAltBuffer) return;
        _alt = null;
        UsingAltBuffer = false;
    }
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: PASS (17 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Buffer/TerminalGrid.cs Volt.Tests/Terminal/TerminalGridTests.cs
git commit -m "feat(terminal): add scrollback ring buffer to TerminalGrid"
```

---

### Task 7: TerminalGrid alt buffer save/restore + erase operations

**Files:**
- Modify: `Volt/Terminal/Buffer/TerminalGrid.cs`
- Modify: `Volt.Tests/Terminal/TerminalGridTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void SwitchToAltBuffer_PreservesMainBuffer()
    {
        var g = new TerminalGrid(3, 3, 10);
        g.WriteCell(0, 0, 'A', CellAttr.None);
        g.WriteCell(1, 1, 'B', CellAttr.None);
        g.SwitchToAltBuffer();
        g.WriteCell(0, 0, 'X', CellAttr.None);
        Assert.Equal('X', g.CellAt(0, 0).Glyph);
        g.SwitchToMainBuffer();
        Assert.Equal('A', g.CellAt(0, 0).Glyph);
        Assert.Equal('B', g.CellAt(1, 1).Glyph);
    }

    [Fact]
    public void EraseInLine_ToEnd_ClearsFromCursor()
    {
        var g = new TerminalGrid(3, 5, 10);
        for (int c = 0; c < 5; c++) g.WriteCell(1, c, 'X', CellAttr.None);
        g.SetCursor(1, 2);
        g.EraseInLine(EraseMode.ToEnd);
        Assert.Equal('X', g.CellAt(1, 0).Glyph);
        Assert.Equal('X', g.CellAt(1, 1).Glyph);
        Assert.Equal(' ', g.CellAt(1, 2).Glyph);
        Assert.Equal(' ', g.CellAt(1, 4).Glyph);
    }

    [Fact]
    public void EraseInLine_ToStart_ClearsUpToCursor()
    {
        var g = new TerminalGrid(3, 5, 10);
        for (int c = 0; c < 5; c++) g.WriteCell(1, c, 'X', CellAttr.None);
        g.SetCursor(1, 2);
        g.EraseInLine(EraseMode.ToStart);
        Assert.Equal(' ', g.CellAt(1, 0).Glyph);
        Assert.Equal(' ', g.CellAt(1, 2).Glyph);
        Assert.Equal('X', g.CellAt(1, 3).Glyph);
    }

    [Fact]
    public void EraseInLine_All_ClearsRow()
    {
        var g = new TerminalGrid(3, 5, 10);
        for (int c = 0; c < 5; c++) g.WriteCell(1, c, 'X', CellAttr.None);
        g.SetCursor(1, 2);
        g.EraseInLine(EraseMode.All);
        for (int c = 0; c < 5; c++) Assert.Equal(' ', g.CellAt(1, c).Glyph);
    }

    [Fact]
    public void EraseInDisplay_All_ClearsEverything()
    {
        var g = new TerminalGrid(3, 3, 10);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                g.WriteCell(r, c, 'X', CellAttr.None);
        g.EraseInDisplay(EraseMode.All);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(' ', g.CellAt(r, c).Glyph);
    }
```

- [ ] **Step 2: Run tests, verify failures**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: `EraseMode` and `EraseInLine`/`EraseInDisplay` not found; alt-buffer save/restore broken.

- [ ] **Step 3: Implement alt-buffer save/restore and erase**

Replace the Task-6 alt-buffer stubs with:

```csharp
    // Saved main-buffer state when alt buffer is active
    private (int row, int col) _mainSavedCursor;
    private Cell _mainSavedPen;

    public void SwitchToAltBuffer()
    {
        if (UsingAltBuffer) return;
        _mainSavedCursor = Cursor;
        _mainSavedPen = Pen;
        _alt = AllocBlank(Rows, Cols);
        UsingAltBuffer = true;
        Cursor = (0, 0);
        _pendingWrap = false;
        _dirty.MarkDirtyRange(0, Rows - 1);
        Changed?.Invoke();
    }

    public void SwitchToMainBuffer()
    {
        if (!UsingAltBuffer) return;
        _alt = null;
        UsingAltBuffer = false;
        Cursor = _mainSavedCursor;
        Pen = _mainSavedPen;
        _pendingWrap = false;
        _dirty.MarkDirtyRange(0, Rows - 1);
        Changed?.Invoke();
    }
```

Add `EraseMode` enum (at the top of the file, outside the class):

```csharp
public enum EraseMode { ToEnd, ToStart, All }
```

Add erase methods to `TerminalGrid`:

```csharp
    public void EraseInLine(EraseMode mode)
    {
        var (r, c) = Cursor;
        int start, end;
        switch (mode)
        {
            case EraseMode.ToEnd:   start = c; end = Cols - 1; break;
            case EraseMode.ToStart: start = 0; end = c;        break;
            default:                start = 0; end = Cols - 1; break;
        }
        for (int col = start; col <= end; col++)
            ActiveBuffer[r, col] = Cell.Blank;
        _dirty.MarkDirty(r);
        Changed?.Invoke();
    }

    public void EraseInDisplay(EraseMode mode)
    {
        var (r, c) = Cursor;
        int rowStart, rowEnd;
        switch (mode)
        {
            case EraseMode.ToEnd:
                EraseInLine(EraseMode.ToEnd);
                rowStart = r + 1; rowEnd = Rows - 1;
                break;
            case EraseMode.ToStart:
                EraseInLine(EraseMode.ToStart);
                rowStart = 0; rowEnd = r - 1;
                break;
            default:
                rowStart = 0; rowEnd = Rows - 1;
                break;
        }
        for (int row = rowStart; row <= rowEnd; row++)
            for (int col = 0; col < Cols; col++)
                ActiveBuffer[row, col] = Cell.Blank;
        if (rowEnd >= rowStart) _dirty.MarkDirtyRange(rowStart, rowEnd);
        Changed?.Invoke();
    }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: PASS (22 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Buffer/TerminalGrid.cs Volt.Tests/Terminal/TerminalGridTests.cs
git commit -m "feat(terminal): add alt buffer save/restore and erase operations"
```

---

### Task 8: TerminalGrid Resize with cursor preservation

**Files:**
- Modify: `Volt/Terminal/Buffer/TerminalGrid.cs`
- Modify: `Volt.Tests/Terminal/TerminalGridTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void Resize_Larger_PadsWithBlanks()
    {
        var g = new TerminalGrid(3, 3, 10);
        g.WriteCell(0, 0, 'A', CellAttr.None);
        g.Resize(5, 5);
        Assert.Equal(5, g.Rows);
        Assert.Equal(5, g.Cols);
        Assert.Equal('A', g.CellAt(0, 0).Glyph);
        Assert.Equal(' ', g.CellAt(4, 4).Glyph);
    }

    [Fact]
    public void Resize_Smaller_TruncatesAndClampsCursor()
    {
        var g = new TerminalGrid(10, 10, 10);
        g.SetCursor(9, 9);
        g.Resize(5, 5);
        Assert.Equal(5, g.Rows);
        Assert.Equal(5, g.Cols);
        Assert.Equal((4, 4), g.Cursor);
    }

    [Fact]
    public void Resize_PreservesCursorIfInBounds()
    {
        var g = new TerminalGrid(10, 10, 10);
        g.SetCursor(3, 3);
        g.Resize(5, 5);
        Assert.Equal((3, 3), g.Cursor);
    }

    [Fact]
    public void Resize_ZeroDimensions_ClampsToOne()
    {
        var g = new TerminalGrid(5, 5, 10);
        g.Resize(0, 0);
        Assert.Equal(1, g.Rows);
        Assert.Equal(1, g.Cols);
    }
```

- [ ] **Step 2: Run tests, verify failures**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: `Resize` not found.

- [ ] **Step 3: Implement Resize**

```csharp
    public void Resize(int rows, int cols)
    {
        rows = Math.Max(1, rows);
        cols = Math.Max(1, cols);
        if (rows == Rows && cols == Cols) return;

        var newMain = AllocBlank(rows, cols);
        int copyRows = Math.Min(Rows, rows);
        int copyCols = Math.Min(Cols, cols);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                newMain[r, c] = _main[r, c];
        _main = newMain;

        if (_alt != null)
            _alt = AllocBlank(rows, cols);

        Rows = rows;
        Cols = cols;
        ScrollTop = 0;
        ScrollBottom = Rows - 1;
        var (cr, cc) = Cursor;
        Cursor = (Math.Clamp(cr, 0, Rows - 1), Math.Clamp(cc, 0, Cols - 1));
        _pendingWrap = false;
        _dirty.MarkDirtyRange(0, Rows - 1);
        Changed?.Invoke();
    }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter TerminalGridTests`
Expected: PASS (26 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Buffer/TerminalGrid.cs Volt.Tests/Terminal/TerminalGridTests.cs
git commit -m "feat(terminal): add Resize to TerminalGrid with cursor preservation"
```

---

**Stage 1 done.** `TerminalGrid` is now a working, tested grid with cursor, pen, autowrap, scroll region, scrollback, alt buffer, erase, and resize. Zero WPF dependencies.

---

## Stage 2 — VT Parser (Paul Williams State Machine)

**Reference for all Stage 2 tasks:** https://vt100.net/emu/dec_ansi_parser — work from the state diagram. Also useful: Microsoft Terminal's `src/terminal/parser/stateMachine.cpp` as a quality reference, not a literal port.

### Task 9: IVtEventHandler interface

**Files:**
- Create: `Volt/Terminal/Vt/IVtEventHandler.cs`

- [ ] **Step 1: Create interface (no test — it's just a contract)**

```csharp
// Volt/Terminal/Vt/IVtEventHandler.cs
using System;

namespace Volt;

/// <summary>
/// Receives parser events from VtStateMachine. Parameter spans are only valid
/// for the duration of the call — do not capture them.
/// </summary>
public interface IVtEventHandler
{
    /// <summary>Printable character (already UTF-8 decoded) to place at cursor.</summary>
    void Print(char ch);

    /// <summary>C0/C1 control byte (BEL, BS, HT, LF, CR, etc.).</summary>
    void Execute(byte ctrl);

    /// <summary>
    /// CSI final byte dispatched. Params list may contain 0 for omitted params
    /// (e.g. CSI ; 5 H → [0, 5]). Intermediates are any '?', '!', ' ', etc.
    /// collected between CSI and params.
    /// </summary>
    void CsiDispatch(char final, ReadOnlySpan&lt;int&gt; parameters, ReadOnlySpan&lt;char&gt; intermediates);

    /// <summary>ESC sequence that wasn't a CSI/OSC/DCS (e.g. ESC 7, ESC D).</summary>
    void EscDispatch(char final, ReadOnlySpan&lt;char&gt; intermediates);

    /// <summary>OSC command dispatched. First numeric param is the command id.</summary>
    void OscDispatch(int command, string data);
}
```

**Note on HTML-escaping:** the code uses `ReadOnlySpan<int>` and `ReadOnlySpan<char>` — replace `&lt;` and `&gt;` with literal angle brackets when you paste into the file. (This note exists because the plan doc is markdown.)

- [ ] **Step 2: Verify build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS (no new tests yet).

- [ ] **Step 3: Commit**

```bash
git add Volt/Terminal/Vt/IVtEventHandler.cs
git commit -m "feat(terminal): add IVtEventHandler interface"
```

---

### Task 10: VtStateMachine — Ground state, Print, Execute

**Files:**
- Create: `Volt/Terminal/Vt/VtStateMachine.cs`
- Create: `Volt.Tests/Terminal/VtStateMachineTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Volt.Tests/Terminal/VtStateMachineTests.cs
using System.Collections.Generic;
using System.Text;
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class VtStateMachineTests
{
    private sealed class RecordingHandler : IVtEventHandler
    {
        public List<string> Events = new();
        public void Print(char ch) => Events.Add($"Print:{ch}");
        public void Execute(byte ctrl) => Events.Add($"Exec:{ctrl:X2}");
        public void CsiDispatch(char final, System.ReadOnlySpan<int> p, System.ReadOnlySpan<char> i)
        {
            var ps = string.Join(",", p.ToArray());
            var ins = new string(i);
            Events.Add($"Csi:{ins}[{ps}]{final}");
        }
        public void EscDispatch(char final, System.ReadOnlySpan<char> i)
            => Events.Add($"Esc:{new string(i)}{final}");
        public void OscDispatch(int cmd, string data) => Events.Add($"Osc:{cmd}:{data}");
    }

    private static List<string> Feed(string bytes)
    {
        var h = new RecordingHandler();
        var sm = new VtStateMachine(h);
        sm.Feed(Encoding.ASCII.GetBytes(bytes));
        return h.Events;
    }

    [Fact]
    public void PlainAscii_EmitsPrintPerChar()
    {
        var events = Feed("Hi!");
        Assert.Equal(new[] { "Print:H", "Print:i", "Print:!" }, events);
    }

    [Fact]
    public void ControlBytes_EmitExecute()
    {
        var events = Feed("\b\t\n\r\a");
        Assert.Equal(new[] { "Exec:08", "Exec:09", "Exec:0A", "Exec:0D", "Exec:07" }, events);
    }
}
```

- [ ] **Step 2: Run tests, verify failures**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: FAIL — type not found.

- [ ] **Step 3: Implement VtStateMachine scaffolding + Ground state**

```csharp
// Volt/Terminal/Vt/VtStateMachine.cs
using System;

namespace Volt;

/// <summary>
/// Paul Williams ANSI parser. See https://vt100.net/emu/dec_ansi_parser for the reference diagram.
/// Thread affinity: single thread (call Feed from one thread only).
/// </summary>
public sealed class VtStateMachine
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsIgnore,
        SosPmApcString,
    }

    private readonly IVtEventHandler _h;
    private State _state = State.Ground;

    // Collected parameters for CSI/DCS
    private readonly int[] _params = new int[32];
    private int _paramCount;
    private bool _paramHasDigits;

    // Collected intermediates
    private readonly char[] _intermediates = new char[4];
    private int _intermediateCount;

    // OSC string buffer
    private readonly System.Text.StringBuilder _oscBuf = new(256);
    private const int OscMaxLength = 64 * 1024;

    public VtStateMachine(IVtEventHandler handler) { _h = handler; }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            Step(bytes[i]);
    }

    private void Step(byte b)
    {
        // Global anywhere-transitions (from Williams diagram)
        if (b == 0x18 || b == 0x1A) { _state = State.Ground; _h.Execute(b); return; }
        if (b == 0x1B) { EnterEscape(); return; }

        switch (_state)
        {
            case State.Ground: StepGround(b); break;
            // other states added in later tasks
            default: StepGround(b); break;
        }
    }

    private void StepGround(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b == 0x7F) { /* DEL — ignored in Ground */ return; }
        // 0x20..0x7E printable ASCII; 0x80+ UTF-8 continuation handled in Task 14
        _h.Print((char)b);
    }

    private void EnterEscape()
    {
        _paramCount = 0;
        _paramHasDigits = false;
        _intermediateCount = 0;
        _state = State.Escape;
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtStateMachine.cs Volt.Tests/Terminal/VtStateMachineTests.cs
git commit -m "feat(terminal): add VtStateMachine Ground state"
```

---

### Task 11: Escape + CSI states

**Files:**
- Modify: `Volt/Terminal/Vt/VtStateMachine.cs`
- Modify: `Volt.Tests/Terminal/VtStateMachineTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void SimpleEscape_EmitsEscDispatch()
    {
        var events = Feed("\u001b7");
        Assert.Equal(new[] { "Esc:7" }, events);
    }

    [Fact]
    public void Csi_CursorUp_NoParam()
    {
        var events = Feed("\u001b[A");
        Assert.Equal(new[] { "Csi:[0]A" }, events);
    }

    [Fact]
    public void Csi_CursorPositionTwoParams()
    {
        var events = Feed("\u001b[32;45H");
        Assert.Equal(new[] { "Csi:[32,45]H" }, events);
    }

    [Fact]
    public void Csi_EmptyLeadingParam_IsZero()
    {
        var events = Feed("\u001b[;5H");
        Assert.Equal(new[] { "Csi:[0,5]H" }, events);
    }

    [Fact]
    public void Csi_PrivateMode_WithIntermediate()
    {
        var events = Feed("\u001b[?1049h");
        Assert.Equal(new[] { "Csi:?[1049]h" }, events);
    }

    [Fact]
    public void Csi_ParamOverflow_IsClamped()
    {
        var events = Feed("\u001b[99999999999999999A");
        Assert.Single(events);
        Assert.StartsWith("Csi:", events[0]);
        Assert.EndsWith("A", events[0]);
        // Value is clamped, not overflowed into garbage
    }
```

- [ ] **Step 2: Run tests, verify failures**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: failures.

- [ ] **Step 3: Extend VtStateMachine with Escape, CsiEntry/Param/Intermediate/Ignore states**

Replace the `Step` method and add state-handler methods. Full replacement of `Step`:

```csharp
    private void Step(byte b)
    {
        if (b == 0x18 || b == 0x1A) { _state = State.Ground; _h.Execute(b); return; }
        if (b == 0x1B) { EnterEscape(); return; }

        switch (_state)
        {
            case State.Ground:             StepGround(b); break;
            case State.Escape:             StepEscape(b); break;
            case State.EscapeIntermediate: StepEscapeIntermediate(b); break;
            case State.CsiEntry:           StepCsiEntry(b); break;
            case State.CsiParam:           StepCsiParam(b); break;
            case State.CsiIntermediate:    StepCsiIntermediate(b); break;
            case State.CsiIgnore:          StepCsiIgnore(b); break;
        }
    }

    private void StepEscape(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); _state = State.EscapeIntermediate; return; }
        if (b == 0x5B) { _state = State.CsiEntry; return; }      // [
        if (b == 0x5D) { _oscBuf.Clear(); _state = State.OscString; return; } // ]
        if (b >= 0x30 && b <= 0x7E)
        {
            _h.EscDispatch((char)b, _intermediates.AsSpan(0, _intermediateCount));
            _state = State.Ground;
            return;
        }
    }

    private void StepEscapeIntermediate(byte b)
    {
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); return; }
        if (b >= 0x30 && b <= 0x7E)
        {
            _h.EscDispatch((char)b, _intermediates.AsSpan(0, _intermediateCount));
            _state = State.Ground;
            return;
        }
        if (b <= 0x1F) { _h.Execute(b); return; }
    }

    private void StepCsiEntry(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b >= 0x30 && b <= 0x39) { EnsureFirstParam(); AccumulateParamDigit(b); _state = State.CsiParam; return; }
        if (b == 0x3B) { EnsureFirstParam(); NextParam(); _state = State.CsiParam; return; }
        if (b >= 0x3C && b <= 0x3F) { CollectIntermediate((char)b); _state = State.CsiParam; return; }
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); _state = State.CsiIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E) { DispatchCsi((char)b); return; }
        _state = State.CsiIgnore;
    }

    private void StepCsiParam(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b >= 0x30 && b <= 0x39) { AccumulateParamDigit(b); return; }
        if (b == 0x3B) { NextParam(); return; }
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); _state = State.CsiIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E) { DispatchCsi((char)b); return; }
        _state = State.CsiIgnore;
    }

    private void StepCsiIntermediate(byte b)
    {
        if (b <= 0x1F) { _h.Execute(b); return; }
        if (b >= 0x20 && b <= 0x2F) { CollectIntermediate((char)b); return; }
        if (b >= 0x40 && b <= 0x7E) { DispatchCsi((char)b); return; }
        _state = State.CsiIgnore;
    }

    private void StepCsiIgnore(byte b)
    {
        if (b >= 0x40 && b <= 0x7E) { _state = State.Ground; return; }
    }

    private void EnsureFirstParam() { if (_paramCount == 0) { _paramCount = 1; _params[0] = 0; _paramHasDigits = false; } }
    private void NextParam() { if (_paramCount < _params.Length) { _paramCount++; _params[_paramCount - 1] = 0; _paramHasDigits = false; } }

    private void AccumulateParamDigit(byte b)
    {
        if (_paramCount == 0) _paramCount = 1;
        int idx = _paramCount - 1;
        long v = (long)_params[idx] * 10 + (b - (byte)'0');
        if (v > int.MaxValue) v = int.MaxValue;   // clamp overflow
        _params[idx] = (int)v;
        _paramHasDigits = true;
    }

    private void CollectIntermediate(char c)
    {
        if (_intermediateCount < _intermediates.Length)
            _intermediates[_intermediateCount++] = c;
    }

    private void DispatchCsi(char final)
    {
        _h.CsiDispatch(final,
            _params.AsSpan(0, Math.Max(_paramCount, 1)),
            _intermediates.AsSpan(0, _intermediateCount));
        // Reset
        _paramCount = 0;
        _paramHasDigits = false;
        _intermediateCount = 0;
        _state = State.Ground;
    }
```

**Note:** `EnsureFirstParam` creates a zero-initialized first slot so `CSI ; 5 H` yields `[0, 5]`. `DispatchCsi` uses `Math.Max(_paramCount, 1)` so `CSI A` dispatches as `[0]` (default param).

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtStateMachine.cs Volt.Tests/Terminal/VtStateMachineTests.cs
git commit -m "feat(terminal): add Escape and CSI states to VtStateMachine"
```

---

### Task 12: OSC state

**Files:**
- Modify: `Volt/Terminal/Vt/VtStateMachine.cs`
- Modify: `Volt.Tests/Terminal/VtStateMachineTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void Osc_SetTitle_BelTerminated()
    {
        var events = Feed("\u001b]0;My Title\a");
        Assert.Equal(new[] { "Osc:0:My Title" }, events);
    }

    [Fact]
    public void Osc_SetTitle_StTerminated()
    {
        var events = Feed("\u001b]2;Hello\u001b\\");
        Assert.Equal(new[] { "Osc:2:Hello" }, events);
    }

    [Fact]
    public void Osc_NoSemicolon_EmitsEmptyData()
    {
        var events = Feed("\u001b]0\a");
        Assert.Equal(new[] { "Osc:0:" }, events);
    }

    [Fact]
    public void Osc_OversizedString_Truncated()
    {
        var big = new string('X', 70_000);
        var events = Feed($"\u001b]0;{big}\a");
        Assert.Single(events);
        Assert.StartsWith("Osc:0:", events[0]);
        // Data portion is capped at OscMaxLength (64 KB)
        Assert.True(events[0].Length <= 64 * 1024 + 10);
    }
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: failures.

- [ ] **Step 3: Extend the switch and add OSC handling**

Add `State.OscString` case to the `Step` switch:

```csharp
            case State.OscString:          StepOsc(b); break;
```

Add method:

```csharp
    private void StepOsc(byte b)
    {
        // Terminator: BEL (0x07) or ST (ESC \). ESC alone restarts escape handling.
        if (b == 0x07) { DispatchOsc(); return; }
        if (b == 0x1B) { /* handled by global ESC check */ return; }
        if (b == 0x5C && _state == State.OscString && _oscBuf.Length > 0 && _oscBuf[_oscBuf.Length - 1] == '\u001b')
        {
            // ST form ESC\ — the ESC was consumed by global check, so we never get here in practice.
            // Handled below.
        }
        if (_oscBuf.Length < OscMaxLength)
            _oscBuf.Append((char)b);
    }
```

**The ST-termination is subtle.** The global ESC check resets to Escape state and consumes the ESC. To detect `ESC \`, we need the Escape state to check for `\` and, if we were in OSC, dispatch it.

Add handling in `StepEscape`:

```csharp
    private void StepEscape(byte b)
    {
        // If we jumped into Escape while OSC was accumulating, ESC\ closes the OSC.
        if (_oscBuf.Length > 0 && b == 0x5C) { DispatchOsc(); return; }
        // ... (rest of original StepEscape body unchanged)
```

Wait — the simpler approach is to have an `_oscPending` flag. Replace the OSC handling:

```csharp
    // State: set when we entered OscString mode
    private bool _inOsc;

    // In StepEscape, detect the ] to open OSC:
    //     if (b == 0x5D) { _oscBuf.Clear(); _inOsc = true; _state = State.OscString; return; }
    //
    // In the global ESC check at the top of Step(), if _inOsc is set, don't EnterEscape —
    // instead set a "waiting for \" sub-flag. If the next byte is \, dispatch OSC and go to Ground.

    private bool _oscEscapePending;

    private void DispatchOsc()
    {
        // Parse "cmd;data" out of the OSC buffer
        var full = _oscBuf.ToString();
        int semi = full.IndexOf(';');
        int cmd = 0;
        string data = "";
        if (semi < 0)
        {
            int.TryParse(full, out cmd);
        }
        else
        {
            int.TryParse(full.AsSpan(0, semi), out cmd);
            data = full.Substring(semi + 1);
        }
        _h.OscDispatch(cmd, data);
        _oscBuf.Clear();
        _inOsc = false;
        _oscEscapePending = false;
        _state = State.Ground;
    }
```

Now rewrite `Step` to handle the ESC-while-in-OSC case:

```csharp
    private void Step(byte b)
    {
        // ST (ESC\) terminator handling for OSC
        if (_state == State.OscString && _oscEscapePending)
        {
            if (b == 0x5C) { DispatchOsc(); return; }
            _oscEscapePending = false;
            if (_oscBuf.Length < OscMaxLength) _oscBuf.Append('\u001b');
            // fall through
        }

        if (b == 0x18 || b == 0x1A) { _state = State.Ground; _inOsc = false; _h.Execute(b); return; }
        if (b == 0x1B)
        {
            if (_state == State.OscString) { _oscEscapePending = true; return; }
            EnterEscape();
            return;
        }

        switch (_state)
        {
            case State.Ground:             StepGround(b); break;
            case State.Escape:             StepEscape(b); break;
            case State.EscapeIntermediate: StepEscapeIntermediate(b); break;
            case State.CsiEntry:           StepCsiEntry(b); break;
            case State.CsiParam:           StepCsiParam(b); break;
            case State.CsiIntermediate:    StepCsiIntermediate(b); break;
            case State.CsiIgnore:          StepCsiIgnore(b); break;
            case State.OscString:          StepOsc(b); break;
        }
    }
```

And fix `StepEscape` to open OSC:

```csharp
        if (b == 0x5D) { _oscBuf.Clear(); _inOsc = true; _state = State.OscString; return; }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: PASS (12 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtStateMachine.cs Volt.Tests/Terminal/VtStateMachineTests.cs
git commit -m "feat(terminal): add OSC string state with BEL/ST terminators"
```

---

### Task 13: DCS states (consumed without dispatch)

**Files:**
- Modify: `Volt/Terminal/Vt/VtStateMachine.cs`
- Modify: `Volt.Tests/Terminal/VtStateMachineTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void Dcs_ConsumedWithoutEvents_PrintAfterDcsStillWorks()
    {
        // DCS q ... ST (sixel-like) then plain 'A'
        var events = Feed("\u001bPq1;2;3data\u001b\\A");
        Assert.Equal(new[] { "Print:A" }, events);
    }

    [Fact]
    public void Dcs_LongStringNeverDispatches()
    {
        var big = new string('Z', 5000);
        var events = Feed($"\u001bP{big}\u001b\\hi");
        Assert.Equal(new[] { "Print:h", "Print:i" }, events);
    }
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: failures — DCS leaks bytes as Print events.

- [ ] **Step 3: Add DCS states**

In `StepEscape`, detect `P` (0x50) to enter DCS:

```csharp
        if (b == 0x50) { _state = State.DcsEntry; return; }
```

Add DCS state handlers. DCS v1 strategy: consume bytes and ignore, waiting for ST terminator.

```csharp
    private void StepDcsEntry(byte b)
    {
        if (b >= 0x30 && b <= 0x39) { _state = State.DcsParam; return; }
        if (b == 0x3B) { _state = State.DcsParam; return; }
        if (b >= 0x3C && b <= 0x3F) { _state = State.DcsParam; return; }
        if (b >= 0x20 && b <= 0x2F) { _state = State.DcsIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E) { _state = State.DcsPassthrough; return; }
        _state = State.DcsIgnore;
    }

    private void StepDcsParam(byte b)
    {
        if (b >= 0x30 && b <= 0x39) return;
        if (b == 0x3B) return;
        if (b >= 0x20 && b <= 0x2F) { _state = State.DcsIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E) { _state = State.DcsPassthrough; return; }
        if (b >= 0x3C && b <= 0x3F) { _state = State.DcsIgnore; return; }
    }

    private void StepDcsIntermediate(byte b)
    {
        if (b >= 0x20 && b <= 0x2F) return;
        if (b >= 0x40 && b <= 0x7E) { _state = State.DcsPassthrough; return; }
        _state = State.DcsIgnore;
    }

    private void StepDcsPassthrough(byte b)
    {
        // Consume silently until ST; ESC is handled globally and will transition us to Ground via ST.
    }

    private void StepDcsIgnore(byte b) { /* consume */ }
```

Add cases to the `Step` switch:

```csharp
            case State.DcsEntry:           StepDcsEntry(b); break;
            case State.DcsParam:           StepDcsParam(b); break;
            case State.DcsIntermediate:    StepDcsIntermediate(b); break;
            case State.DcsPassthrough:     StepDcsPassthrough(b); break;
            case State.DcsIgnore:          StepDcsIgnore(b); break;
```

Handle ST termination for DCS. Extend the ESC logic in `Step`:

```csharp
        if (b == 0x1B)
        {
            if (_state == State.OscString) { _oscEscapePending = true; return; }
            if (_state == State.DcsPassthrough || _state == State.DcsIgnore
                || _state == State.DcsEntry || _state == State.DcsParam || _state == State.DcsIntermediate)
            {
                _dcsEscapePending = true;
                return;
            }
            EnterEscape();
            return;
        }
```

Add the DCS-ST handler at the top of `Step`:

```csharp
        if (_dcsEscapePending)
        {
            if (b == 0x5C) { _state = State.Ground; _dcsEscapePending = false; return; }
            _dcsEscapePending = false;
            // fall through — byte continues DCS consumption
        }
```

And declare the flag:

```csharp
    private bool _dcsEscapePending;
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: PASS (14 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtStateMachine.cs Volt.Tests/Terminal/VtStateMachineTests.cs
git commit -m "feat(terminal): consume DCS sequences without dispatching"
```

---

### Task 14: UTF-8 decoding at the Print boundary

**Files:**
- Modify: `Volt/Terminal/Vt/VtStateMachine.cs`
- Modify: `Volt.Tests/Terminal/VtStateMachineTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    private static List<string> FeedBytes(byte[] bytes)
    {
        var h = new RecordingHandler();
        var sm = new VtStateMachine(h);
        sm.Feed(bytes);
        return h.Events;
    }

    [Fact]
    public void Utf8_TwoByte_EmitsOnePrint()
    {
        // é = U+00E9 = 0xC3 0xA9
        var events = FeedBytes(new byte[] { 0xC3, 0xA9 });
        Assert.Equal(new[] { "Print:é" }, events);
    }

    [Fact]
    public void Utf8_ThreeByte_EmitsOnePrint()
    {
        // ★ = U+2605 = 0xE2 0x98 0x85
        var events = FeedBytes(new byte[] { 0xE2, 0x98, 0x85 });
        Assert.Equal(new[] { "Print:★" }, events);
    }

    [Fact]
    public void Utf8_InvalidLead_EmitsReplacement()
    {
        var events = FeedBytes(new byte[] { 0xFF });
        Assert.Equal(new[] { "Print:\uFFFD" }, events);
    }

    [Fact]
    public void Utf8_TruncatedSequence_EmitsReplacement()
    {
        // 0xC3 expects one continuation; feeding ASCII 'A' instead
        var events = FeedBytes(new byte[] { 0xC3, 0x41 });
        Assert.Equal(new[] { "Print:\uFFFD", "Print:A" }, events);
    }
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: failures — current `StepGround` casts `byte` directly to `char`.

- [ ] **Step 3: Add UTF-8 state + decoding**

Add fields:

```csharp
    // UTF-8 decode state
    private int _utf8Remaining;
    private int _utf8Codepoint;
```

Replace `StepGround`:

```csharp
    private void StepGround(byte b)
    {
        if (b <= 0x1F) { FlushUtf8Error(); _h.Execute(b); return; }
        if (b == 0x7F) { FlushUtf8Error(); return; }

        if (_utf8Remaining > 0)
        {
            if ((b & 0xC0) != 0x80) { _h.Print('\uFFFD'); _utf8Remaining = 0; StepGround(b); return; }
            _utf8Codepoint = (_utf8Codepoint << 6) | (b & 0x3F);
            _utf8Remaining--;
            if (_utf8Remaining == 0)
            {
                if (_utf8Codepoint <= 0xFFFF)
                    _h.Print((char)_utf8Codepoint);
                else
                    _h.Print('\uFFFD'); // surrogate pairs deferred; v1 treats them as replacement
            }
            return;
        }

        if (b < 0x80) { _h.Print((char)b); return; }

        if ((b & 0xE0) == 0xC0) { _utf8Codepoint = b & 0x1F; _utf8Remaining = 1; return; }
        if ((b & 0xF0) == 0xE0) { _utf8Codepoint = b & 0x0F; _utf8Remaining = 2; return; }
        if ((b & 0xF8) == 0xF0) { _utf8Codepoint = b & 0x07; _utf8Remaining = 3; return; }
        _h.Print('\uFFFD');
    }

    private void FlushUtf8Error()
    {
        if (_utf8Remaining > 0) { _h.Print('\uFFFD'); _utf8Remaining = 0; }
    }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtStateMachineTests`
Expected: PASS (18 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtStateMachine.cs Volt.Tests/Terminal/VtStateMachineTests.cs
git commit -m "feat(terminal): add UTF-8 decoding to VtStateMachine Print boundary"
```

---

**Stage 2 done.** `VtStateMachine` is a working, tested Paul Williams parser supporting Ground/Escape/CSI/OSC/DCS states with UTF-8 decoding.

---

## Stage 3 — Theme & Palette

### Task 15: ColorTheme `terminal` section

**Files:**
- Modify: `Volt/Theme/ColorTheme.cs`
- Modify: `Volt/Theme/ThemeManager.cs`

- [ ] **Step 1: Read current ColorTheme.cs**

Read: `Volt/Theme/ColorTheme.cs` to see the existing JSON model (editor / chrome / scopes sections).

- [ ] **Step 2: Add TerminalColors record**

At the bottom of `ColorTheme.cs` (or wherever nested records live), add:

```csharp
public record TerminalColors(
    string? Background,
    string? Foreground,
    string[]? Ansi
);
```

Add a property to the main `ColorTheme` record:

```csharp
public TerminalColors? Terminal { get; init; }
```

(Exact syntax depends on whether `ColorTheme` uses record primary constructor or properties — match the existing style.)

- [ ] **Step 3: Expose the palette from ThemeManager**

In `Volt/Theme/ThemeManager.cs`, add a public read-only property:

```csharp
public TerminalColors? TerminalColors { get; private set; }
```

In the existing `Apply` method (wherever it deserializes into `_current`), after the theme loads, set:

```csharp
TerminalColors = _current?.Terminal;
```

Make sure this re-fires `ThemeChanged` as the existing code already does.

- [ ] **Step 4: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 5: Commit**

```bash
git add Volt/Theme/ColorTheme.cs Volt/Theme/ThemeManager.cs
git commit -m "feat(theme): add terminal color section to ColorTheme"
```

---

### Task 16: AnsiPalette

**Files:**
- Create: `Volt/Terminal/Vt/AnsiPalette.cs`
- Create: `Volt.Tests/Terminal/AnsiPaletteTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Volt.Tests/Terminal/AnsiPaletteTests.cs
using System.Windows.Media;
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class AnsiPaletteTests
{
    [Fact]
    public void Resolve_AnsiIndex0_ReturnsBlack()
    {
        var c = AnsiPalette.ResolveDefault(0);
        Assert.Equal((byte)0, c.R);
        Assert.Equal((byte)0, c.G);
        Assert.Equal((byte)0, c.B);
    }

    [Fact]
    public void Resolve_XtermCube_Index16_IsBlack()
    {
        var c = AnsiPalette.ResolveDefault(16);
        Assert.Equal((byte)0, c.R);
    }

    [Fact]
    public void Resolve_XtermCube_Index231_IsNearWhite()
    {
        var c = AnsiPalette.ResolveDefault(231);
        Assert.Equal((byte)0xFF, c.R);
        Assert.Equal((byte)0xFF, c.G);
        Assert.Equal((byte)0xFF, c.B);
    }

    [Fact]
    public void Resolve_Grayscale_Index232_IsDarkGray()
    {
        var c = AnsiPalette.ResolveDefault(232);
        Assert.Equal((byte)8, c.R);
    }

    [Fact]
    public void Resolve_Grayscale_Index255_IsLightGray()
    {
        var c = AnsiPalette.ResolveDefault(255);
        Assert.Equal((byte)238, c.R);
    }

    [Fact]
    public void ResolveTrueColor_UnpacksArgb()
    {
        uint argb = 0xFF102030;
        var c = AnsiPalette.ResolveTrueColor(argb);
        Assert.Equal((byte)0x10, c.R);
        Assert.Equal((byte)0x20, c.G);
        Assert.Equal((byte)0x30, c.B);
    }
}
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter AnsiPaletteTests`
Expected: type not found.

- [ ] **Step 3: Implement AnsiPalette**

```csharp
// Volt/Terminal/Vt/AnsiPalette.cs
using System.Windows.Media;

namespace Volt;

public static class AnsiPalette
{
    // Built-in fallback palette (xterm defaults) used when theme has no terminal section.
    private static readonly uint[] FallbackAnsi16 = new uint[]
    {
        0xFF000000, 0xFFCD0000, 0xFF00CD00, 0xFFCDCD00,
        0xFF0000EE, 0xFFCD00CD, 0xFF00CDCD, 0xFFE5E5E5,
        0xFF7F7F7F, 0xFFFF0000, 0xFF00FF00, 0xFFFFFF00,
        0xFF5C5CFF, 0xFFFF00FF, 0xFF00FFFF, 0xFFFFFFFF,
    };

    private static readonly uint[] XtermCube = BuildXtermCube();

    public static Color ResolveDefault(int index)
    {
        if (index < 0) return Color.FromRgb(0xD4, 0xD4, 0xD4);
        if (index < 16) return FromTheme(index) ?? Unpack(FallbackAnsi16[index]);
        if (index < 256) return Unpack(XtermCube[index]);
        return Color.FromRgb(0xD4, 0xD4, 0xD4);
    }

    public static Color ResolveTrueColor(uint argb) => Unpack(argb);

    public static Color DefaultFg()
    {
        var tm = App.Current?.ThemeManager;
        var hex = tm?.TerminalColors?.Foreground;
        return ParseHex(hex) ?? Color.FromRgb(0xD4, 0xD4, 0xD4);
    }

    public static Color DefaultBg()
    {
        var tm = App.Current?.ThemeManager;
        var hex = tm?.TerminalColors?.Background;
        return ParseHex(hex) ?? Color.FromRgb(0x1E, 0x1E, 0x1E);
    }

    private static Color? FromTheme(int index)
    {
        var tm = App.Current?.ThemeManager;
        var arr = tm?.TerminalColors?.Ansi;
        if (arr == null || index >= arr.Length) return null;
        return ParseHex(arr[index]);
    }

    private static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var obj = ColorConverter.ConvertFromString(hex);
            return obj is Color c ? c : (Color?)null;
        }
        catch { return null; }
    }

    private static Color Unpack(uint argb)
        => Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

    private static uint[] BuildXtermCube()
    {
        var arr = new uint[256];
        for (int i = 0; i < 16; i++) arr[i] = FallbackAnsi16[i];
        int[] steps = { 0, 95, 135, 175, 215, 255 };
        int idx = 16;
        for (int r = 0; r < 6; r++)
            for (int g = 0; g < 6; g++)
                for (int b = 0; b < 6; b++)
                    arr[idx++] = 0xFF000000u | ((uint)steps[r] << 16) | ((uint)steps[g] << 8) | (uint)steps[b];
        for (int i = 0; i < 24; i++)
        {
            int v = 8 + i * 10;
            arr[232 + i] = 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v;
        }
        return arr;
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter AnsiPaletteTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/AnsiPalette.cs Volt.Tests/Terminal/AnsiPaletteTests.cs
git commit -m "feat(terminal): add AnsiPalette with xterm 256-color table"
```

---

### Task 17: Add terminal sections to shipped themes

**Files:**
- Modify: `Volt/Resources/Themes/default-dark.json`
- Modify: `Volt/Resources/Themes/default-light.json`
- Modify: `Volt/Resources/Themes/gruvbox-dark.json`

- [ ] **Step 1: Add `terminal` section to each theme file**

For `default-dark.json`, add at the top level:

```json
"terminal": {
  "background": "#1e1e1e",
  "foreground": "#d4d4d4",
  "ansi": [
    "#000000", "#cd3131", "#0dbc79", "#e5e510",
    "#2472c8", "#bc3fbc", "#11a8cd", "#e5e5e5",
    "#666666", "#f14c4c", "#23d18b", "#f5f543",
    "#3b8eea", "#d670d6", "#29b8db", "#e5e5e5"
  ]
}
```

For `default-light.json`:

```json
"terminal": {
  "background": "#ffffff",
  "foreground": "#383a42",
  "ansi": [
    "#000000", "#cd3131", "#00bc00", "#949800",
    "#0451a5", "#bc05bc", "#0598bc", "#555555",
    "#666666", "#cd3131", "#14ce14", "#b5ba00",
    "#0451a5", "#bc05bc", "#0598bc", "#a5a5a5"
  ]
}
```

For `gruvbox-dark.json`:

```json
"terminal": {
  "background": "#282828",
  "foreground": "#ebdbb2",
  "ansi": [
    "#282828", "#cc241d", "#98971a", "#d79921",
    "#458588", "#b16286", "#689d6a", "#a89984",
    "#928374", "#fb4934", "#b8bb26", "#fabd2f",
    "#83a598", "#d3869b", "#8ec07c", "#ebdbb2"
  ]
}
```

- [ ] **Step 2: Validate JSON**

Run: `dotnet build Volt.sln`
Expected: SUCCESS; embedded resources update.

- [ ] **Step 3: Smoke test from code** — add a quick ad-hoc test:

```csharp
    [Fact]
    public void DefaultDark_Terminal_Resolves()
    {
        // This test relies on App being initialized; if it isn't in the test harness,
        // this test can be marked [Fact(Skip=...)] — the theme loading happens at app startup.
        var c = AnsiPalette.ResolveDefault(1);
        Assert.NotEqual((byte)0, c.R); // red ANSI index 1 should have nonzero red
    }
```

If `App.Current` isn't available in tests, skip this test — it's a smoke check, not core behavior.

- [ ] **Step 4: Commit**

```bash
git add Volt/Resources/Themes/
git commit -m "feat(theme): add terminal palette to shipped themes"
```

---

**Stage 3 done.** Theme system now exposes terminal colors; `AnsiPalette` resolves indices to `Color`.

---

## Stage 4 — VT Dispatcher

### Task 18: VtDispatcher skeleton — Print, Execute, OSC title

**Files:**
- Create: `Volt/Terminal/Vt/VtDispatcher.cs`
- Create: `Volt.Tests/Terminal/VtDispatcherTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Volt.Tests/Terminal/VtDispatcherTests.cs
using System.Text;
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class VtDispatcherTests
{
    private static (TerminalGrid g, VtDispatcher d, VtStateMachine sm) Make(int rows = 10, int cols = 20)
    {
        var g = new TerminalGrid(rows, cols, 100);
        var d = new VtDispatcher(g);
        var sm = new VtStateMachine(d);
        return (g, d, sm);
    }

    private static void Feed(VtStateMachine sm, string s) => sm.Feed(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Print_WritesCharAtCursor()
    {
        var (g, _, sm) = Make();
        Feed(sm, "Hi");
        Assert.Equal('H', g.CellAt(0, 0).Glyph);
        Assert.Equal('i', g.CellAt(0, 1).Glyph);
        Assert.Equal((0, 2), g.Cursor);
    }

    [Fact]
    public void LineFeed_MovesCursorDownSameColumn()
    {
        var (g, _, sm) = Make();
        Feed(sm, "A\nB");
        Assert.Equal('A', g.CellAt(0, 0).Glyph);
        Assert.Equal('B', g.CellAt(1, 1).Glyph);
    }

    [Fact]
    public void CarriageReturn_MovesCursorToColumnZero()
    {
        var (g, _, sm) = Make();
        Feed(sm, "AB\rC");
        Assert.Equal('C', g.CellAt(0, 0).Glyph);
    }

    [Fact]
    public void Backspace_MovesCursorLeft()
    {
        var (g, _, sm) = Make();
        Feed(sm, "AB\bC");
        Assert.Equal('C', g.CellAt(0, 1).Glyph);
    }

    [Fact]
    public void Osc0_UpdatesTitle()
    {
        var (g, d, sm) = Make();
        string? title = null;
        d.TitleChanged += t => title = t;
        Feed(sm, "\u001b]0;Window Title\a");
        Assert.Equal("Window Title", title);
    }
}
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: type not found.

- [ ] **Step 3: Implement VtDispatcher skeleton**

```csharp
// Volt/Terminal/Vt/VtDispatcher.cs
using System;

namespace Volt;

public sealed class VtDispatcher : IVtEventHandler
{
    private readonly TerminalGrid _grid;
    public event Action<string>? TitleChanged;

    public VtDispatcher(TerminalGrid grid) { _grid = grid; }

    public void Print(char ch) => _grid.PutGlyph(ch);

    public void Execute(byte ctrl)
    {
        switch (ctrl)
        {
            case 0x07: _grid.Bell(); break;
            case 0x08: Backspace(); break;
            case 0x09: HorizontalTab(); break;
            case 0x0A: case 0x0B: case 0x0C: LineFeed(); break;
            case 0x0D: CarriageReturn(); break;
            default: break;
        }
    }

    public void CsiDispatch(char final, ReadOnlySpan<int> p, ReadOnlySpan<char> i)
    {
        // Task 19+ handles each CSI final; v1 skeleton ignores.
    }

    public void EscDispatch(char final, ReadOnlySpan<char> i) { }

    public void OscDispatch(int command, string data)
    {
        if (command == 0 || command == 1 || command == 2)
            TitleChanged?.Invoke(data);
    }

    private void Backspace()
    {
        var (r, c) = _grid.Cursor;
        if (c > 0) _grid.SetCursor(r, c - 1);
    }

    private void CarriageReturn()
    {
        var (r, _) = _grid.Cursor;
        _grid.SetCursor(r, 0);
    }

    private void LineFeed()
    {
        var (r, c) = _grid.Cursor;
        if (r + 1 < _grid.Rows)
            _grid.SetCursor(r + 1, c);
        else
            _grid.ScrollUp(1);
    }

    private void HorizontalTab()
    {
        var (r, c) = _grid.Cursor;
        int next = ((c / 8) + 1) * 8;
        if (next >= _grid.Cols) next = _grid.Cols - 1;
        _grid.SetCursor(r, next);
    }
}
```

Note: `_grid.Bell()` doesn't exist yet — add a stub to `TerminalGrid`:

```csharp
    public event Action? BellRang;
    public void Bell() => BellRang?.Invoke();
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtDispatcher.cs Volt/Terminal/Buffer/TerminalGrid.cs Volt.Tests/Terminal/VtDispatcherTests.cs
git commit -m "feat(terminal): add VtDispatcher skeleton with Print/Execute/OSC title"
```

---

### Task 19: VtDispatcher — cursor movement CSI finals (A/B/C/D/E/F/G/H/f)

**Files:**
- Modify: `Volt/Terminal/Vt/VtDispatcher.cs`
- Modify: `Volt.Tests/Terminal/VtDispatcherTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void CsiA_CursorUp()
    {
        var (g, _, sm) = Make();
        g.SetCursor(5, 3);
        Feed(sm, "\u001b[2A");
        Assert.Equal((3, 3), g.Cursor);
    }

    [Fact]
    public void CsiB_CursorDown_Default1()
    {
        var (g, _, sm) = Make();
        g.SetCursor(5, 3);
        Feed(sm, "\u001b[B");
        Assert.Equal((6, 3), g.Cursor);
    }

    [Fact]
    public void CsiC_CursorForward()
    {
        var (g, _, sm) = Make();
        g.SetCursor(5, 3);
        Feed(sm, "\u001b[3C");
        Assert.Equal((5, 6), g.Cursor);
    }

    [Fact]
    public void CsiD_CursorBack()
    {
        var (g, _, sm) = Make();
        g.SetCursor(5, 8);
        Feed(sm, "\u001b[3D");
        Assert.Equal((5, 5), g.Cursor);
    }

    [Fact]
    public void CsiH_CursorPosition_OneIndexed()
    {
        var (g, _, sm) = Make();
        Feed(sm, "\u001b[5;10H");
        // VT is 1-indexed, grid 0-indexed
        Assert.Equal((4, 9), g.Cursor);
    }

    [Fact]
    public void CsiH_NoParams_GoesToOrigin()
    {
        var (g, _, sm) = Make();
        g.SetCursor(5, 5);
        Feed(sm, "\u001b[H");
        Assert.Equal((0, 0), g.Cursor);
    }

    [Fact]
    public void CsiG_CursorHorizontalAbsolute()
    {
        var (g, _, sm) = Make();
        g.SetCursor(3, 5);
        Feed(sm, "\u001b[10G");
        Assert.Equal((3, 9), g.Cursor);
    }
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: 7 failures.

- [ ] **Step 3: Implement cursor-movement dispatch**

Replace `VtDispatcher.CsiDispatch` with a dispatch switch and helpers:

```csharp
    public void CsiDispatch(char final, ReadOnlySpan<int> p, ReadOnlySpan<char> i)
    {
        int p0 = P(p, 0, 1);
        int p1 = P(p, 1, 1);
        switch (final)
        {
            case 'A': CursorUp(p0); break;
            case 'B': CursorDown(p0); break;
            case 'C': CursorForward(p0); break;
            case 'D': CursorBack(p0); break;
            case 'E': CarriageReturn(); CursorDown(p0); break;
            case 'F': CarriageReturn(); CursorUp(p0); break;
            case 'G': CursorHorizontalAbsolute(p0); break;
            case 'H': case 'f': CursorPosition(p0, p1); break;
            default: break; // later tasks add more finals
        }
    }

    private static int P(ReadOnlySpan<int> p, int index, int defaultIfZeroOrMissing)
    {
        if (index >= p.Length) return defaultIfZeroOrMissing;
        int v = p[index];
        return v == 0 ? defaultIfZeroOrMissing : v;
    }

    private void CursorUp(int n)      { var (r, c) = _grid.Cursor; _grid.SetCursor(r - n, c); }
    private void CursorDown(int n)    { var (r, c) = _grid.Cursor; _grid.SetCursor(r + n, c); }
    private void CursorForward(int n) { var (r, c) = _grid.Cursor; _grid.SetCursor(r, c + n); }
    private void CursorBack(int n)    { var (r, c) = _grid.Cursor; _grid.SetCursor(r, c - n); }
    private void CursorHorizontalAbsolute(int col) { var (r, _) = _grid.Cursor; _grid.SetCursor(r, col - 1); }
    private void CursorPosition(int row, int col)  { _grid.SetCursor(row - 1, col - 1); }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: PASS (12 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtDispatcher.cs Volt.Tests/Terminal/VtDispatcherTests.cs
git commit -m "feat(terminal): add cursor movement CSI finals to VtDispatcher"
```

---

### Task 20: VtDispatcher — erase (J/K) and insert/delete (L/M/P/@) and scroll (S/T)

**Files:**
- Modify: `Volt/Terminal/Vt/VtDispatcher.cs`
- Modify: `Volt.Tests/Terminal/VtDispatcherTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void CsiJ0_ErasesDisplayToEnd()
    {
        var (g, _, sm) = Make(5, 5);
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 5; c++)
                g.WriteCell(r, c, 'X', CellAttr.None);
        g.SetCursor(2, 2);
        Feed(sm, "\u001b[0J");
        Assert.Equal('X', g.CellAt(2, 1).Glyph);
        Assert.Equal(' ', g.CellAt(2, 2).Glyph);
        Assert.Equal(' ', g.CellAt(4, 4).Glyph);
    }

    [Fact]
    public void CsiJ2_ClearsScreen()
    {
        var (g, _, sm) = Make(5, 5);
        for (int r = 0; r < 5; r++) g.WriteCell(r, 0, 'X', CellAttr.None);
        Feed(sm, "\u001b[2J");
        for (int r = 0; r < 5; r++) Assert.Equal(' ', g.CellAt(r, 0).Glyph);
    }

    [Fact]
    public void CsiK0_ErasesLineToEnd()
    {
        var (g, _, sm) = Make(3, 5);
        for (int c = 0; c < 5; c++) g.WriteCell(1, c, 'X', CellAttr.None);
        g.SetCursor(1, 2);
        Feed(sm, "\u001b[0K");
        Assert.Equal('X', g.CellAt(1, 1).Glyph);
        Assert.Equal(' ', g.CellAt(1, 2).Glyph);
    }

    [Fact]
    public void CsiL_InsertLines()
    {
        var (g, _, sm) = Make(5, 3);
        for (int r = 0; r < 5; r++) g.WriteCell(r, 0, (char)('A' + r), CellAttr.None);
        g.SetCursor(1, 0);
        Feed(sm, "\u001b[2L");
        Assert.Equal('A', g.CellAt(0, 0).Glyph);
        Assert.Equal(' ', g.CellAt(1, 0).Glyph);
        Assert.Equal('B', g.CellAt(3, 0).Glyph);
    }

    [Fact]
    public void CsiS_ScrollUp()
    {
        var (g, _, sm) = Make(3, 3);
        for (int r = 0; r < 3; r++) g.WriteCell(r, 0, (char)('A' + r), CellAttr.None);
        Feed(sm, "\u001b[1S");
        Assert.Equal('B', g.CellAt(0, 0).Glyph);
        Assert.Equal('C', g.CellAt(1, 0).Glyph);
    }
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: 5 failures.

- [ ] **Step 3: Extend CsiDispatch switch**

Add cases:

```csharp
            case 'J': EraseDisplay(p0); break;
            case 'K': EraseLine(p0); break;
            case 'L': _grid.InsertLines(p0); break;
            case 'M': _grid.DeleteLines(p0); break;
            case 'S': _grid.ScrollUp(p0); break;
            case 'T': _grid.ScrollDown(p0); break;
            case 'r': _grid.SetScrollRegion(p0 - 1, p1 - 1); break;
```

Add helpers:

```csharp
    private void EraseDisplay(int mode)
    {
        _grid.EraseInDisplay(mode switch { 1 => EraseMode.ToStart, 2 => EraseMode.All, _ => EraseMode.ToEnd });
    }

    private void EraseLine(int mode)
    {
        _grid.EraseInLine(mode switch { 1 => EraseMode.ToStart, 2 => EraseMode.All, _ => EraseMode.ToEnd });
    }
```

**Param default nuance:** the `P` helper returns the default when the value is 0, but for `J` and `K`, 0 is a *valid* mode (ToEnd). Use raw access for these:

```csharp
    public void CsiDispatch(char final, ReadOnlySpan<int> p, ReadOnlySpan<char> i)
    {
        int p0default1 = P(p, 0, 1);
        int p1default1 = P(p, 1, 1);
        int p0raw = p.Length > 0 ? p[0] : 0;
        switch (final)
        {
            // ... cursor cases use p0default1 / p1default1
            case 'J': EraseDisplay(p0raw); break;
            case 'K': EraseLine(p0raw); break;
            // ... scroll cases use p0default1
            case 'r':
                if (p.Length == 0) _grid.SetScrollRegion(0, _grid.Rows - 1);
                else _grid.SetScrollRegion(p0default1 - 1, p1default1 - 1);
                break;
            // ...
        }
    }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: PASS (17 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtDispatcher.cs Volt.Tests/Terminal/VtDispatcherTests.cs
git commit -m "feat(terminal): add erase, insert/delete lines, scroll CSI finals"
```

---

### Task 21: VtDispatcher — SGR (CSI m) colors and attributes

**Files:**
- Modify: `Volt/Terminal/Vt/VtDispatcher.cs`
- Modify: `Volt.Tests/Terminal/VtDispatcherTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void Sgr_Reset_RestoresDefaults()
    {
        var (g, _, sm) = Make();
        Feed(sm, "\u001b[1;31m");
        Feed(sm, "\u001b[0m");
        Feed(sm, "X");
        Assert.Equal(CellAttr.None, g.CellAt(0, 0).Attr);
        Assert.Equal(-1, g.CellAt(0, 0).FgIndex);
    }

    [Fact]
    public void Sgr_Bold_SetsAttr()
    {
        var (g, _, sm) = Make();
        Feed(sm, "\u001b[1mX");
        Assert.Equal(CellAttr.Bold, g.CellAt(0, 0).Attr);
    }

    [Fact]
    public void Sgr_AnsiFg_31_SetsRed()
    {
        var (g, _, sm) = Make();
        Feed(sm, "\u001b[31mX");
        Assert.Equal(1, g.CellAt(0, 0).FgIndex);
    }

    [Fact]
    public void Sgr_AnsiBrightFg_91_SetsBrightRed()
    {
        var (g, _, sm) = Make();
        Feed(sm, "\u001b[91mX");
        Assert.Equal(9, g.CellAt(0, 0).FgIndex);
    }

    [Fact]
    public void Sgr_Xterm256_Fg()
    {
        var (g, _, sm) = Make();
        Feed(sm, "\u001b[38;5;214mX");
        Assert.Equal(214, g.CellAt(0, 0).FgIndex);
    }

    [Fact]
    public void Sgr_TrueColor_Fg()
    {
        var (g, _, sm) = Make();
        Feed(sm, "\u001b[38;2;10;20;30mX");
        int fg = g.CellAt(0, 0).FgIndex;
        Assert.True(fg < -1, "Truecolor index should be encoded as < -1");
    }

    [Fact]
    public void Sgr_CombinedBoldRed()
    {
        var (g, _, sm) = Make();
        Feed(sm, "\u001b[1;31mX");
        Assert.Equal(CellAttr.Bold, g.CellAt(0, 0).Attr);
        Assert.Equal(1, g.CellAt(0, 0).FgIndex);
    }
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: failures.

- [ ] **Step 3: Implement SGR**

Add a truecolor side-table to `TerminalGrid`:

```csharp
    private readonly List<uint> _trueColors = new();

    public int RegisterTrueColor(uint argb)
    {
        _trueColors.Add(argb);
        return -(_trueColors.Count + 1); // -2, -3, -4, ...
    }

    public uint GetTrueColor(int encodedIndex)
    {
        int idx = -encodedIndex - 2;
        if (idx < 0 || idx >= _trueColors.Count) return 0xFFFFFFFF;
        return _trueColors[idx];
    }
```

Add `case 'm':` to `CsiDispatch`:

```csharp
            case 'm': HandleSgr(p); break;
```

Implement `HandleSgr`:

```csharp
    private void HandleSgr(ReadOnlySpan<int> p)
    {
        if (p.Length == 0)
        {
            ResetPen();
            return;
        }
        for (int i = 0; i < p.Length; i++)
        {
            int n = p[i];
            switch (n)
            {
                case 0: ResetPen(); break;
                case 1: AddAttr(CellAttr.Bold); break;
                case 2: AddAttr(CellAttr.Dim); break;
                case 3: AddAttr(CellAttr.Italic); break;
                case 4: AddAttr(CellAttr.Underline); break;
                case 7: AddAttr(CellAttr.Inverse); break;
                case 9: AddAttr(CellAttr.Strikethrough); break;
                case 22: RemoveAttr(CellAttr.Bold | CellAttr.Dim); break;
                case 23: RemoveAttr(CellAttr.Italic); break;
                case 24: RemoveAttr(CellAttr.Underline); break;
                case 27: RemoveAttr(CellAttr.Inverse); break;
                case 29: RemoveAttr(CellAttr.Strikethrough); break;
                case 39: SetPenFg(-1); break;
                case 49: SetPenBg(-1); break;
                default:
                    if (n >= 30 && n <= 37) SetPenFg(n - 30);
                    else if (n >= 40 && n <= 47) SetPenBg(n - 40);
                    else if (n >= 90 && n <= 97) SetPenFg(8 + (n - 90));
                    else if (n >= 100 && n <= 107) SetPenBg(8 + (n - 100));
                    else if (n == 38 && i + 1 < p.Length)
                    {
                        if (p[i + 1] == 5 && i + 2 < p.Length) { SetPenFg(p[i + 2]); i += 2; }
                        else if (p[i + 1] == 2 && i + 4 < p.Length)
                        {
                            uint argb = 0xFF000000u | ((uint)p[i + 2] << 16) | ((uint)p[i + 3] << 8) | (uint)p[i + 4];
                            SetPenFg(_grid.RegisterTrueColor(argb));
                            i += 4;
                        }
                    }
                    else if (n == 48 && i + 1 < p.Length)
                    {
                        if (p[i + 1] == 5 && i + 2 < p.Length) { SetPenBg(p[i + 2]); i += 2; }
                        else if (p[i + 1] == 2 && i + 4 < p.Length)
                        {
                            uint argb = 0xFF000000u | ((uint)p[i + 2] << 16) | ((uint)p[i + 3] << 8) | (uint)p[i + 4];
                            SetPenBg(_grid.RegisterTrueColor(argb));
                            i += 4;
                        }
                    }
                    break;
            }
        }
    }

    private void ResetPen() { _grid.Pen = new Cell { FgIndex = -1, BgIndex = -1, Attr = CellAttr.None, Glyph = ' ' }; }
    private void AddAttr(CellAttr a) { var p = _grid.Pen; p.Attr |= a; _grid.Pen = p; }
    private void RemoveAttr(CellAttr a) { var p = _grid.Pen; p.Attr &= ~a; _grid.Pen = p; }
    private void SetPenFg(int i) { var p = _grid.Pen; p.FgIndex = i; _grid.Pen = p; }
    private void SetPenBg(int i) { var p = _grid.Pen; p.BgIndex = i; _grid.Pen = p; }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: PASS (24 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtDispatcher.cs Volt/Terminal/Buffer/TerminalGrid.cs Volt.Tests/Terminal/VtDispatcherTests.cs
git commit -m "feat(terminal): add SGR (colors and attributes) to VtDispatcher"
```

---

### Task 22: VtDispatcher — DEC modes (?1049 alt buffer, ?25 cursor visibility)

**Files:**
- Modify: `Volt/Terminal/Vt/VtDispatcher.cs`
- Modify: `Volt.Tests/Terminal/VtDispatcherTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void Dec1049h_SwitchesToAltBuffer()
    {
        var (g, _, sm) = Make();
        Feed(sm, "main");
        Feed(sm, "\u001b[?1049h");
        Feed(sm, "alt");
        Assert.True(g.UsingAltBuffer);
        Assert.Equal('a', g.CellAt(0, 0).Glyph);
        Feed(sm, "\u001b[?1049l");
        Assert.False(g.UsingAltBuffer);
        Assert.Equal('m', g.CellAt(0, 0).Glyph);
    }

    [Fact]
    public void Dec25l_HidesCursor()
    {
        var (g, _, sm) = Make();
        Feed(sm, "\u001b[?25l");
        Assert.False(g.CursorVisible);
        Feed(sm, "\u001b[?25h");
        Assert.True(g.CursorVisible);
    }
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: 2 failures.

- [ ] **Step 3: Add private-mode handling**

In `CsiDispatch`, detect `?` intermediate:

```csharp
            case 'h': HandleMode(p, i, set: true); break;
            case 'l': HandleMode(p, i, set: false); break;
```

Add:

```csharp
    private void HandleMode(ReadOnlySpan<int> p, ReadOnlySpan<char> i, bool set)
    {
        bool isPrivate = i.Length > 0 && i[0] == '?';
        if (!isPrivate) return;
        for (int k = 0; k < p.Length; k++)
        {
            int mode = p[k];
            switch (mode)
            {
                case 25:
                    _grid.CursorVisible = set;
                    break;
                case 1049:
                    if (set) _grid.SwitchToAltBuffer();
                    else _grid.SwitchToMainBuffer();
                    break;
                case 1000: case 1002: case 1003: case 1006: case 2004:
                    // Mouse/bracketed-paste — deferred, silently acknowledged
                    break;
                default: break;
            }
        }
    }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: PASS (26 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtDispatcher.cs Volt.Tests/Terminal/VtDispatcherTests.cs
git commit -m "feat(terminal): add DEC private modes (alt buffer, cursor visibility)"
```

---

### Task 23: VtDispatcher — ICH/DCH (insert/delete chars) and device status report

**Files:**
- Modify: `Volt/Terminal/Vt/VtDispatcher.cs`
- Modify: `Volt.Tests/Terminal/VtDispatcherTests.cs`

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact]
    public void CsiAt_InsertsBlanksAtCursor()
    {
        var (g, _, sm) = Make(3, 6);
        Feed(sm, "ABCDEF");
        g.SetCursor(0, 2);
        Feed(sm, "\u001b[2@");
        Assert.Equal('A', g.CellAt(0, 0).Glyph);
        Assert.Equal('B', g.CellAt(0, 1).Glyph);
        Assert.Equal(' ', g.CellAt(0, 2).Glyph);
        Assert.Equal(' ', g.CellAt(0, 3).Glyph);
        Assert.Equal('C', g.CellAt(0, 4).Glyph);
    }

    [Fact]
    public void CsiP_DeletesCharsAtCursor()
    {
        var (g, _, sm) = Make(3, 6);
        Feed(sm, "ABCDEF");
        g.SetCursor(0, 2);
        Feed(sm, "\u001b[2P");
        Assert.Equal('A', g.CellAt(0, 0).Glyph);
        Assert.Equal('B', g.CellAt(0, 1).Glyph);
        Assert.Equal('E', g.CellAt(0, 2).Glyph);
        Assert.Equal('F', g.CellAt(0, 3).Glyph);
        Assert.Equal(' ', g.CellAt(0, 4).Glyph);
    }

    [Fact]
    public void CsiN6_ReportsCursorPosition()
    {
        var (g, d, sm) = Make();
        g.SetCursor(4, 9);
        byte[]? sent = null;
        d.ResponseRequested += r => sent = r;
        Feed(sm, "\u001b[6n");
        Assert.NotNull(sent);
        // Response is "ESC[row;colR" in 1-indexed form
        var s = System.Text.Encoding.ASCII.GetString(sent!);
        Assert.Equal("\u001b[5;10R", s);
    }
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: failures.

- [ ] **Step 3: Implement ICH, DCH, and DSR**

Add to `TerminalGrid`:

```csharp
    public void InsertChars(int n)
    {
        var (r, c) = Cursor;
        n = Math.Clamp(n, 0, Cols - c);
        for (int col = Cols - 1; col >= c + n; col--)
            ActiveBuffer[r, col] = ActiveBuffer[r, col - n];
        for (int col = c; col < c + n; col++)
            ActiveBuffer[r, col] = Cell.Blank;
        _dirty.MarkDirty(r);
        Changed?.Invoke();
    }

    public void DeleteChars(int n)
    {
        var (r, c) = Cursor;
        n = Math.Clamp(n, 0, Cols - c);
        for (int col = c; col < Cols - n; col++)
            ActiveBuffer[r, col] = ActiveBuffer[r, col + n];
        for (int col = Cols - n; col < Cols; col++)
            ActiveBuffer[r, col] = Cell.Blank;
        _dirty.MarkDirty(r);
        Changed?.Invoke();
    }
```

Add to `VtDispatcher`:

```csharp
    public event Action<byte[]>? ResponseRequested;

    // In CsiDispatch switch:
    //   case '@': _grid.InsertChars(p0default1); break;
    //   case 'P': _grid.DeleteChars(p0default1); break;
    //   case 'n': HandleDsr(p0raw); break;

    private void HandleDsr(int mode)
    {
        if (mode != 6) return;
        var (r, c) = _grid.Cursor;
        var response = $"\u001b[{r + 1};{c + 1}R";
        ResponseRequested?.Invoke(System.Text.Encoding.ASCII.GetBytes(response));
    }
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter VtDispatcherTests`
Expected: PASS (29 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Vt/VtDispatcher.cs Volt/Terminal/Buffer/TerminalGrid.cs Volt.Tests/Terminal/VtDispatcherTests.cs
git commit -m "feat(terminal): add ICH, DCH, and device status report to VtDispatcher"
```

---

**Stage 4 done.** `VtDispatcher` translates parsed events into grid operations — cursor, erase, scroll, SGR, modes, ICH/DCH/DSR. The pure-C# core (Stages 1–4) is a working terminal that can accept bytes, parse, and update a grid. Still no ConPTY and no rendering.

---

## Stage 5 — ConPTY

### Task 24: NativeMethods P/Invoke signatures

**Files:**
- Create: `Volt/Terminal/ConPty/NativeMethods.cs`

- [ ] **Step 1: Add signatures**

```csharp
// Volt/Terminal/ConPty/NativeMethods.cs
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Volt;

internal static class NativeMethods
{
    public const int STARTF_USESTDHANDLES = 0x00000100;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    public const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalAlloc(uint uFlags, IntPtr uBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add Volt/Terminal/ConPty/NativeMethods.cs
git commit -m "feat(terminal): add ConPTY P/Invoke signatures"
```

---

### Task 25: ConPtyHost.Create

**Files:**
- Create: `Volt/Terminal/ConPty/ConPtyHost.cs`

- [ ] **Step 1: Write ConPtyHost**

```csharp
// Volt/Terminal/ConPty/ConPtyHost.cs
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Volt.NativeMethods;

namespace Volt;

public sealed class TerminalUnavailableException : Exception
{
    public TerminalUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public readonly struct PtyHandles : IDisposable
{
    public IntPtr PseudoConsole { get; init; }
    public SafeFileHandle Input { get; init; }
    public SafeFileHandle Output { get; init; }
    public Process Process { get; init; }

    public void Dispose()
    {
        try { if (PseudoConsole != IntPtr.Zero) ClosePseudoConsole(PseudoConsole); } catch { }
        try { Input?.Dispose(); } catch { }
        try { Output?.Dispose(); } catch { }
        try { if (Process != null && !Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
    }
}

public static class ConPtyHost
{
    public static PtyHandles Create(string shellExe, string? args, string cwd, short rows, short cols)
    {
        SafeFileHandle? inputReadSide = null;
        SafeFileHandle? inputWriteSide = null;
        SafeFileHandle? outputReadSide = null;
        SafeFileHandle? outputWriteSide = null;
        IntPtr hpcon = IntPtr.Zero;
        IntPtr attrList = IntPtr.Zero;
        Process? process = null;

        try
        {
            if (!CreatePipe(out inputReadSide, out inputWriteSide, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed");
            if (!CreatePipe(out outputReadSide, out outputWriteSide, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed");

            var size = new COORD { X = cols, Y = rows };
            int hr = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out hpcon);
            if (hr != 0)
                throw new TerminalUnavailableException($"CreatePseudoConsole failed (HRESULT 0x{hr:X}). Requires Windows 10 1809 or newer.");

            // Once the pseudoconsole owns these ends, we can release our copies
            inputReadSide.Dispose();
            outputWriteSide.Dispose();
            inputReadSide = null;
            outputWriteSide = null;

            // Set up STARTUPINFOEX with the pseudoconsole attribute
            IntPtr listSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            attrList = LocalAlloc(0x0040 /*LPTR*/, listSize);
            if (attrList == IntPtr.Zero) throw new OutOfMemoryException("LocalAlloc for attribute list failed");
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref listSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpcon, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            string cmdLine = string.IsNullOrEmpty(args) ? $"\"{shellExe}\"" : $"\"{shellExe}\" {args}";
            if (!CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero, cwd, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");

            CloseHandle(pi.hThread);
            process = Process.GetProcessById(pi.dwProcessId);

            var result = new PtyHandles
            {
                PseudoConsole = hpcon,
                Input = inputWriteSide!,
                Output = outputReadSide!,
                Process = process
            };

            // Clean up attr list; handles now belong to result
            DeleteProcThreadAttributeList(attrList);
            LocalFree(attrList);
            attrList = IntPtr.Zero;
            inputWriteSide = null;
            outputReadSide = null;
            hpcon = IntPtr.Zero;
            return result;
        }
        catch
        {
            try { inputReadSide?.Dispose(); } catch { }
            try { inputWriteSide?.Dispose(); } catch { }
            try { outputReadSide?.Dispose(); } catch { }
            try { outputWriteSide?.Dispose(); } catch { }
            if (hpcon != IntPtr.Zero) { try { ClosePseudoConsole(hpcon); } catch { } }
            if (attrList != IntPtr.Zero)
            {
                try { DeleteProcThreadAttributeList(attrList); } catch { }
                try { LocalFree(attrList); } catch { }
            }
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            throw;
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add Volt/Terminal/ConPty/ConPtyHost.cs
git commit -m "feat(terminal): add ConPtyHost.Create with handle cleanup on partial failure"
```

---

### Task 26: PtySession — read loop, Write, Resize, Dispose

**Files:**
- Create: `Volt/Terminal/ConPty/PtySession.cs`

- [ ] **Step 1: Write PtySession**

```csharp
// Volt/Terminal/ConPty/PtySession.cs
using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using static Volt.NativeMethods;

namespace Volt;

public sealed class PtySession : IDisposable
{
    public event Action<ReadOnlyMemory<byte>>? Output;
    public event Action<int>? Exited;

    private readonly PtyHandles _handles;
    private readonly Dispatcher _uiDispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readTask;
    private FileStream _input;
    private FileStream _output;
    private bool _disposed;

    public PtySession(string shellExe, string? args, string cwd, short rows, short cols)
    {
        _handles = ConPtyHost.Create(shellExe, args, cwd, rows, cols);
        _input = new FileStream(_handles.Input, FileAccess.Write);
        _output = new FileStream(_handles.Output, FileAccess.Read);
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _readTask = Task.Run(ReadLoop);
        _handles.Process.EnableRaisingEvents = true;
        _handles.Process.Exited += OnProcessExited;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed) return;
        try { _input.Write(data); _input.Flush(); }
        catch { /* broken pipe — treat as session end */ }
    }

    public void Resize(short rows, short cols)
    {
        if (_disposed) return;
        var sz = new COORD { X = cols, Y = rows };
        ResizePseudoConsole(_handles.PseudoConsole, sz);
    }

    private async Task ReadLoop()
    {
        var pool = ArrayPool<byte>.Shared;
        var buf = pool.Rent(4096);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n;
                try { n = await _output.ReadAsync(buf.AsMemory(0, buf.Length), _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { break; }
                if (n <= 0) break;

                var copy = new byte[n];
                Buffer.BlockCopy(buf, 0, copy, 0, n);
                _uiDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    try { Output?.Invoke(copy); }
                    catch (Exception ex)
                    {
#if DEBUG
                        throw;
#else
                        System.Diagnostics.Debug.WriteLine($"[Terminal] Output handler threw: {ex}");
#endif
                    }
                }));
            }
        }
        finally
        {
            pool.Return(buf);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int code = 0;
        try { code = _handles.Process.ExitCode; } catch { }
        _uiDispatcher.BeginInvoke(new Action(() => Exited?.Invoke(code)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _output?.Dispose(); } catch { }
        try { _input?.Dispose(); } catch { }
        try { _readTask.Wait(500); } catch { }
        _handles.Dispose();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add Volt/Terminal/ConPty/PtySession.cs
git commit -m "feat(terminal): add PtySession with async read loop and cleanup"
```

---

### Task 27: ConPTY integration tests (opt-in)

**Files:**
- Create: `Volt.Tests/Terminal/Integration/ConPtySessionTests.cs`

- [ ] **Step 1: Write integration tests**

```csharp
// Volt.Tests/Terminal/Integration/ConPtySessionTests.cs
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Volt;

namespace Volt.Tests.Terminal.Integration;

[Trait("Category", "Integration")]
public class ConPtySessionTests
{
    [Fact]
    public async Task SpawnCmd_EchoHello_ReceivesOutput()
    {
        var sb = new StringBuilder();
        int exitCode = -1;
        var done = new ManualResetEventSlim();

        using var s = new PtySession("cmd.exe", "/c echo hello", ".", 24, 80);
        s.Output += data => sb.Append(Encoding.UTF8.GetString(data.Span));
        s.Exited += c => { exitCode = c; done.Set(); };

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "Process did not exit in time");
        Assert.Contains("hello", sb.ToString());
    }

    [Fact]
    public async Task Dispose_MidRead_TerminatesCleanly()
    {
        var s = new PtySession("cmd.exe", null, ".", 24, 80);
        await Task.Delay(100);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        s.Dispose();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Dispose took {sw.ElapsedMilliseconds}ms");
    }
}
```

- [ ] **Step 2: Run integration tests explicitly**

Run: `dotnet test Volt.Tests --filter Category=Integration`
Expected: both tests pass on a Windows 10 1809+ machine.

- [ ] **Step 3: Verify default run skips them**

Run: `dotnet test Volt.Tests`
Expected: integration tests NOT in the run count.

- [ ] **Step 4: Commit**

```bash
git add Volt.Tests/Terminal/Integration/ConPtySessionTests.cs
git commit -m "test(terminal): add opt-in ConPTY integration tests"
```

---

**Stage 5 done.** ConPTY layer is working — can spawn a shell, read output to a UI thread, write input, resize, and shut down cleanly.

---

## Stage 6 — Input

### Task 28: KeyEncoder

**Files:**
- Create: `Volt/Terminal/Input/KeyEncoder.cs`
- Create: `Volt.Tests/Terminal/KeyEncoderTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Volt.Tests/Terminal/KeyEncoderTests.cs
using System.Text;
using System.Windows.Input;
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class KeyEncoderTests
{
    private static string Enc(Key k, ModifierKeys m = ModifierKeys.None)
    {
        var bytes = KeyEncoder.Encode(k, m);
        return bytes == null ? "<null>" : Encoding.ASCII.GetString(bytes);
    }

    [Fact] public void Enter_IsCarriageReturn() => Assert.Equal("\r", Enc(Key.Enter));
    [Fact] public void Tab_IsHt() => Assert.Equal("\t", Enc(Key.Tab));
    [Fact] public void Backspace_IsDel7F() => Assert.Equal("\x7f", Enc(Key.Back));
    [Fact] public void EscapeKey_IsEsc() => Assert.Equal("\x1b", Enc(Key.Escape));

    [Fact] public void Up_IsCsiA() => Assert.Equal("\x1b[A", Enc(Key.Up));
    [Fact] public void Down_IsCsiB() => Assert.Equal("\x1b[B", Enc(Key.Down));
    [Fact] public void Right_IsCsiC() => Assert.Equal("\x1b[C", Enc(Key.Right));
    [Fact] public void Left_IsCsiD() => Assert.Equal("\x1b[D", Enc(Key.Left));

    [Fact] public void ShiftUp_IsCsi1Sem2A() => Assert.Equal("\x1b[1;2A", Enc(Key.Up, ModifierKeys.Shift));
    [Fact] public void CtrlUp_IsCsi1Sem5A() => Assert.Equal("\x1b[1;5A", Enc(Key.Up, ModifierKeys.Control));

    [Fact] public void Home_IsCsiH() => Assert.Equal("\x1b[H", Enc(Key.Home));
    [Fact] public void End_IsCsiF() => Assert.Equal("\x1b[F", Enc(Key.End));
    [Fact] public void PageUp_IsCsi5Tilde() => Assert.Equal("\x1b[5~", Enc(Key.PageUp));
    [Fact] public void PageDown_IsCsi6Tilde() => Assert.Equal("\x1b[6~", Enc(Key.PageDown));

    [Fact] public void F1_IsSs3P() => Assert.Equal("\x1bOP", Enc(Key.F1));
    [Fact] public void F5_IsCsi15Tilde() => Assert.Equal("\x1b[15~", Enc(Key.F5));

    [Fact] public void CtrlA_IsSoh() => Assert.Equal("\x01", Enc(Key.A, ModifierKeys.Control));
    [Fact] public void CtrlC_IsEtx() => Assert.Equal("\x03", Enc(Key.C, ModifierKeys.Control));
    [Fact] public void CtrlZ_IsSub() => Assert.Equal("\x1a", Enc(Key.Z, ModifierKeys.Control));

    [Fact] public void UnmappedKey_ReturnsNull() => Assert.Null(KeyEncoder.Encode(Key.CapsLock, ModifierKeys.None));
}
```

- [ ] **Step 2: Run, verify failures**

Run: `dotnet test Volt.Tests --filter KeyEncoderTests`
Expected: type not found.

- [ ] **Step 3: Implement KeyEncoder**

```csharp
// Volt/Terminal/Input/KeyEncoder.cs
using System.Windows.Input;

namespace Volt;

public static class KeyEncoder
{
    public static byte[]? Encode(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.Enter: return new byte[] { 0x0D };
            case Key.Tab: return modifiers.HasFlag(ModifierKeys.Shift) ? new byte[] { 0x1B, (byte)'[', (byte)'Z' } : new byte[] { 0x09 };
            case Key.Back: return new byte[] { 0x7F };
            case Key.Escape: return new byte[] { 0x1B };

            case Key.Up:    return Arrow('A', modifiers);
            case Key.Down:  return Arrow('B', modifiers);
            case Key.Right: return Arrow('C', modifiers);
            case Key.Left:  return Arrow('D', modifiers);

            case Key.Home:  return modifiers == ModifierKeys.None ? Ascii("\x1b[H") : Arrow('H', modifiers);
            case Key.End:   return modifiers == ModifierKeys.None ? Ascii("\x1b[F") : Arrow('F', modifiers);
            case Key.PageUp:   return Ascii("\x1b[5~");
            case Key.PageDown: return Ascii("\x1b[6~");
            case Key.Insert:   return Ascii("\x1b[2~");
            case Key.Delete:   return Ascii("\x1b[3~");

            case Key.F1: return Ascii("\x1bOP");
            case Key.F2: return Ascii("\x1bOQ");
            case Key.F3: return Ascii("\x1bOR");
            case Key.F4: return Ascii("\x1bOS");
            case Key.F5: return Ascii("\x1b[15~");
            case Key.F6: return Ascii("\x1b[17~");
            case Key.F7: return Ascii("\x1b[18~");
            case Key.F8: return Ascii("\x1b[19~");
            case Key.F9: return Ascii("\x1b[20~");
            case Key.F10: return Ascii("\x1b[21~");
            case Key.F11: return Ascii("\x1b[23~");
            case Key.F12: return Ascii("\x1b[24~");
        }

        // Ctrl+letter → control byte
        if (modifiers == ModifierKeys.Control && key >= Key.A && key <= Key.Z)
            return new byte[] { (byte)(key - Key.A + 1) };

        return null;
    }

    private static byte[] Arrow(char final, ModifierKeys mods)
    {
        if (mods == ModifierKeys.None) return new byte[] { 0x1B, (byte)'[', (byte)final };
        int modCode = 1;
        if (mods.HasFlag(ModifierKeys.Shift)) modCode += 1;
        if (mods.HasFlag(ModifierKeys.Alt)) modCode += 2;
        if (mods.HasFlag(ModifierKeys.Control)) modCode += 4;
        return Ascii($"\x1b[1;{modCode}{final}");
    }

    private static byte[] Ascii(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Volt.Tests --filter KeyEncoderTests`
Expected: PASS (21 tests).

- [ ] **Step 5: Commit**

```bash
git add Volt/Terminal/Input/KeyEncoder.cs Volt.Tests/Terminal/KeyEncoderTests.cs
git commit -m "feat(terminal): add KeyEncoder for WPF Key → VT sequences"
```

---

**Stage 6 done.** Keyboard input encoding is complete and tested.

---

## Stage 7 — WPF View & Panel

From here on, code runs in WPF land and is not unit-tested — Volt's convention per `EditorControl`.

### Task 29: TerminalView skeleton (FrameworkElement, font metrics, background)

**Files:**
- Create: `Volt/UI/Terminal/TerminalView.cs`

- [ ] **Step 1: Create skeleton**

```csharp
// Volt/UI/Terminal/TerminalView.cs
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public sealed class TerminalView : FrameworkElement, IScrollInfo
{
    private readonly DrawingVisual _textVisual = new();
    private readonly DrawingVisual _cursorVisual = new();
    private TerminalGrid? _grid;
    private double _cellWidth;
    private double _cellHeight;

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _textVisual : _cursorVisual;

    public TerminalView()
    {
        AddVisualChild(_textVisual);
        AddVisualChild(_cursorVisual);
        Focusable = true;
        Background = Brushes.Transparent;
        UpdateCellMetrics();
        App.Current.ThemeManager.ThemeChanged += OnThemeChanged;
    }

    public TerminalGrid? Grid
    {
        get => _grid;
        set
        {
            if (_grid != null) _grid.Changed -= OnGridChanged;
            _grid = value;
            if (_grid != null) _grid.Changed += OnGridChanged;
            InvalidateVisual();
        }
    }

    private void OnGridChanged()
    {
        // Coalesced via WPF layout — InvalidateVisual marks, render ticks at vsync
        InvalidateVisual();
    }

    private void OnThemeChanged()
    {
        InvalidateVisual();
    }

    private void UpdateCellMetrics()
    {
        var fm = App.Current.FontManager;
        _cellWidth = fm.AdvanceWidth;
        _cellHeight = fm.LineHeight;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bg = new SolidColorBrush(AnsiPalette.DefaultBg());
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(RenderSize));

        if (_grid == null) return;
        // Full grid render — Task 30 optimizes to dirty-only + glyph runs
    }

    // --- IScrollInfo stubs (Task 32) ---
    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => ActualWidth;
    public double ExtentHeight => _grid == null ? 0 : (_grid.Rows + _grid.ScrollbackCount) * _cellHeight;
    public double ViewportWidth => ActualWidth;
    public double ViewportHeight => ActualHeight;
    public double HorizontalOffset => 0;
    public double VerticalOffset { get; private set; }
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() { }
    public void LineDown() { }
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() { }
    public void PageDown() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() { }
    public void MouseWheelDown() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset) { VerticalOffset = offset; InvalidateVisual(); }
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
```

**Note:** `App.Current.FontManager` is assumed accessible. If not public, add a public getter on App to expose it for the terminal subsystem. Also `FontManager.AdvanceWidth` and `LineHeight` need to exist; if they aren't exposed publicly, add those accessors (read from existing private fields in `FontManager`).

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS (may need to expose `FontManager` on App or add properties to `FontManager`).

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Terminal/TerminalView.cs
git commit -m "feat(terminal): add TerminalView skeleton"
```

---

### Task 30: TerminalView render grid cells using FontManager.DrawGlyphRun

**Files:**
- Modify: `Volt/UI/Terminal/TerminalView.cs`

- [ ] **Step 1: Replace `OnRender` with a glyph-run batched renderer**

```csharp
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var defaultBgColor = AnsiPalette.DefaultBg();
        var bg = new SolidColorBrush(defaultBgColor);
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(RenderSize));

        if (_grid == null) return;
        var fm = App.Current.FontManager;
        var defaultFg = new SolidColorBrush(AnsiPalette.DefaultFg());
        defaultFg.Freeze();

        double y = 0;
        for (int row = 0; row < _grid.Rows; row++)
        {
            RenderRow(dc, fm, row, y, defaultFg);
            y += _cellHeight;
        }

        DrawCursor(dc);
    }

    private void RenderRow(DrawingContext dc, FontManager fm, int row, double y, Brush defaultFg)
    {
        int col = 0;
        while (col < _grid!.Cols)
        {
            // Batch contiguous cells with identical (fg, bg, attr)
            ref readonly var first = ref _grid.CellAt(row, col);
            int runStart = col;
            int fg = first.FgIndex;
            int bg = first.BgIndex;
            var attr = first.Attr;
            col++;
            while (col < _grid.Cols)
            {
                ref readonly var c = ref _grid.CellAt(row, col);
                if (c.FgIndex != fg || c.BgIndex != bg || c.Attr != attr) break;
                col++;
            }

            // Draw background run if non-default
            if (bg != -1)
            {
                var bgColor = bg < -1 ? AnsiPalette.ResolveTrueColor(_grid.GetTrueColor(bg)) : AnsiPalette.ResolveDefault(bg);
                var bgBrush = new SolidColorBrush(bgColor);
                bgBrush.Freeze();
                dc.DrawRectangle(bgBrush, null, new Rect(runStart * _cellWidth, y, (col - runStart) * _cellWidth, _cellHeight));
            }

            // Build the character run
            var runLen = col - runStart;
            Span<char> chars = stackalloc char[runLen];
            for (int i = 0; i < runLen; i++) chars[i] = _grid.CellAt(row, runStart + i).Glyph;

            // Resolve fg brush
            Brush fgBrush;
            if (fg == -1) fgBrush = defaultFg;
            else
            {
                var fgColor = fg < -1 ? AnsiPalette.ResolveTrueColor(_grid.GetTrueColor(fg)) : AnsiPalette.ResolveDefault(fg);
                var br = new SolidColorBrush(fgColor);
                br.Freeze();
                fgBrush = br;
            }

            fm.DrawGlyphRun(dc, chars.ToString(), runStart * _cellWidth, y, fgBrush);
        }
    }

    private void DrawCursor(DrawingContext dc)
    {
        if (_grid == null || !_grid.CursorVisible || !IsKeyboardFocused) return;
        var (r, c) = _grid.Cursor;
        var rect = new Rect(c * _cellWidth, r * _cellHeight, _cellWidth, _cellHeight);
        var br = new SolidColorBrush(AnsiPalette.DefaultFg());
        br.Freeze();
        dc.DrawRectangle(br, null, rect);
    }
```

**Note:** `FontManager.DrawGlyphRun` signature may differ — match the existing API used by `EditorControl`. If it takes a `string`, use `chars.ToString()`. If it takes a `ReadOnlySpan<char>`, pass `chars` directly.

- [ ] **Step 2: Build + manual smoke test**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Terminal/TerminalView.cs
git commit -m "feat(terminal): render grid cells via FontManager glyph runs"
```

---

### Task 31: TerminalView keyboard input + allowlist

**Files:**
- Modify: `Volt/UI/Terminal/TerminalView.cs`

- [ ] **Step 1: Add input members + handlers**

```csharp
    public event Action<byte[]>? InputBytes; // raised when bytes should go to pty

    private readonly HashSet<(Key key, ModifierKeys mods)> _allowlist = new();
    public void AddAllowlistedShortcut(Key key, ModifierKeys mods) => _allowlist.Add((key, mods));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var mods = Keyboard.Modifiers;

        // Reserved Volt shortcuts — bubble up unhandled
        if (_allowlist.Contains((e.Key, mods))) return;

        // Terminal-managed copy/paste
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C) { DoCopy(); e.Handled = true; return; }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V) { DoPaste(); e.Handled = true; return; }

        // Ctrl+C: copy if selection, else SIGINT
        if (mods == ModifierKeys.Control && e.Key == Key.C)
        {
            // Selection support added in a later task; for v1 send SIGINT
            InputBytes?.Invoke(new byte[] { 0x03 });
            e.Handled = true;
            return;
        }

        var bytes = KeyEncoder.Encode(e.Key, mods);
        if (bytes != null)
        {
            InputBytes?.Invoke(bytes);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;
        // Skip control chars that OnKeyDown already handled
        if (e.Text.Length == 1 && e.Text[0] < 0x20 && e.Text[0] != '\r' && e.Text[0] != '\t') return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(e.Text);
        InputBytes?.Invoke(bytes);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
    }

    private void DoCopy()
    {
        // Selection support is stubbed in v1 — skip unless we add selection later in this task.
    }

    private void DoPaste()
    {
        if (!Clipboard.ContainsText()) return;
        var text = Clipboard.GetText();
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        // Chunk at 4 KB to avoid blocking the UI thread for absurdly large pastes
        const int chunkSize = 4096;
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            int len = Math.Min(chunkSize, bytes.Length - i);
            var slice = new byte[len];
            Buffer.BlockCopy(bytes, i, slice, 0, len);
            InputBytes?.Invoke(slice);
        }
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Terminal/TerminalView.cs
git commit -m "feat(terminal): add keyboard input routing with allowlist and paste"
```

---

### Task 32: TerminalView resize → grid + IScrollInfo for scrollback

**Files:**
- Modify: `Volt/UI/Terminal/TerminalView.cs`

- [ ] **Step 1: Add resize handler + flesh out IScrollInfo**

```csharp
    public event Action<int, int>? SizeRequested; // (rows, cols)
    private DispatcherTimer? _resizeDebounce;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _resizeDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, OnResizeTick, Dispatcher);
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    private void OnResizeTick(object? sender, EventArgs e)
    {
        _resizeDebounce?.Stop();
        if (_grid == null || _cellWidth == 0 || _cellHeight == 0) return;
        int cols = Math.Max(1, (int)(ActualWidth / _cellWidth));
        int rows = Math.Max(1, (int)(ActualHeight / _cellHeight));
        if (cols != _grid.Cols || rows != _grid.Rows)
        {
            _grid.Resize(rows, cols);
            SizeRequested?.Invoke(rows, cols);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_grid == null) return;
        int delta = e.Delta > 0 ? -3 : 3;
        SetVerticalOffset(Math.Clamp(VerticalOffset + delta * _cellHeight, 0, Math.Max(0, ExtentHeight - ViewportHeight)));
        e.Handled = true;
    }
```

**Note:** Full scrollback rendering (rendering negative rows when scrolled back) is deferred — the scrollback is visible in the grid but `OnRender` currently only draws rows 0..Rows-1. To support scrolling back visually, `RenderRow` would need to translate a visual row index to a logical grid row (possibly negative). Add this as a follow-up task **Task 32b** if you want scrollback visible from the start, or leave this to "v1.1".

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Terminal/TerminalView.cs
git commit -m "feat(terminal): add debounced resize and mouse wheel scrolling"
```

---

### Task 33: TerminalSession composition root

**Files:**
- Create: `Volt/UI/Terminal/TerminalSession.cs`

- [ ] **Step 1: Implement TerminalSession**

```csharp
// Volt/UI/Terminal/TerminalSession.cs
using System;

namespace Volt;

public sealed class TerminalSession : IDisposable
{
    public string Title { get; private set; } = "Terminal";
    public event Action? TitleChanged;
    public event Action<int>? Exited;

    public TerminalGrid Grid { get; }
    public PtySession Pty { get; }
    public VtStateMachine Parser { get; }
    public VtDispatcher Dispatcher { get; }
    public TerminalView View { get; }

    public TerminalSession(string shellExe, string? args, string cwd, short rows, short cols, int scrollbackLines)
    {
        Grid = new TerminalGrid(rows, cols, scrollbackLines);
        Dispatcher = new VtDispatcher(Grid);
        Parser = new VtStateMachine(Dispatcher);
        View = new TerminalView { Grid = Grid };
        Pty = new PtySession(shellExe, args, cwd, rows, cols);

        Pty.Output += bytes => Parser.Feed(bytes.Span);
        Pty.Exited += code => Exited?.Invoke(code);
        Dispatcher.TitleChanged += t => { Title = t; TitleChanged?.Invoke(); };
        Dispatcher.ResponseRequested += resp => Pty.Write(resp);
        View.InputBytes += bytes => Pty.Write(bytes);
        View.SizeRequested += (r, c) => Pty.Resize((short)r, (short)c);
    }

    public void Dispose()
    {
        try { Pty.Dispose(); } catch { }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Terminal/TerminalSession.cs
git commit -m "feat(terminal): add TerminalSession composition root"
```

---

### Task 34: TerminalPanel XAML shell

**Files:**
- Create: `Volt/UI/Terminal/TerminalPanel.xaml`
- Create: `Volt/UI/Terminal/TerminalPanel.xaml.cs`

- [ ] **Step 1: Create XAML**

```xml
<!-- Volt/UI/Terminal/TerminalPanel.xaml -->
<UserControl x:Class="Volt.TerminalPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ThemeEditorBgBrush}">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="24"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        <Border Grid.Row="0" Background="{DynamicResource ThemeChromeBrush}">
            <StackPanel Orientation="Horizontal">
                <ItemsControl x:Name="TabsList">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <StackPanel Orientation="Horizontal"/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                </ItemsControl>
                <Button x:Name="AddButton" Content="+" Width="24" Click="OnAddClicked"/>
            </StackPanel>
        </Border>
        <ContentPresenter Grid.Row="1" x:Name="ActiveContent"/>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Create code-behind**

```csharp
// Volt/UI/Terminal/TerminalPanel.xaml.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Volt;

public partial class TerminalPanel : UserControl, IPanel
{
    private readonly List<TerminalSession> _sessions = new();
    private TerminalSession? _active;

    public string PanelId => "terminal";
    public string Title => _active?.Title ?? "Terminal";
    public string? IconGlyph => "\uE756";
    public new UIElement Content => this;
    public event Action? TitleChanged;

    public TerminalPanel()
    {
        InitializeComponent();
    }

    public void NewSession(string? cwd = null)
    {
        var shell = AppSettings.Current.Editor.TerminalShellPath ?? ResolveDefaultShell();
        var args = AppSettings.Current.Editor.TerminalShellArgs;
        var scrollback = AppSettings.Current.Editor.TerminalScrollbackLines;
        var startDir = cwd ?? ResolveStartingDirectory();

        TerminalSession s;
        try
        {
            s = new TerminalSession(shell, args, startDir, 24, 80, scrollback);
        }
        catch (TerminalUnavailableException ex)
        {
            ThemedMessageBox.Show($"Terminal unavailable: {ex.Message}");
            return;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ThemedMessageBox.Show($"Could not start terminal:\n{ex.Message}\n\nShell: {shell}");
            return;
        }

        s.TitleChanged += () => { TitleChanged?.Invoke(); RebuildTabs(); };
        s.Exited += _ => Dispatcher.BeginInvoke(new Action(() => CloseSession(s)));
        _sessions.Add(s);
        SetActive(s);
        RebuildTabs();
    }

    public void CloseActiveSession()
    {
        if (_active != null) CloseSession(_active);
    }

    private void CloseSession(TerminalSession s)
    {
        try { s.Dispose(); } catch { }
        _sessions.Remove(s);
        if (_active == s) _active = _sessions.Count > 0 ? _sessions[^1] : null;
        SetActive(_active);
        RebuildTabs();
    }

    private void SetActive(TerminalSession? s)
    {
        _active = s;
        ActiveContent.Content = s?.View;
        if (s != null) s.View.Focus();
        TitleChanged?.Invoke();
    }

    private void RebuildTabs()
    {
        var items = new List<object>();
        foreach (var s in _sessions)
        {
            var btn = new Button { Content = s.Title, MinWidth = 80, Margin = new Thickness(2, 0, 2, 0) };
            var captured = s;
            btn.Click += (_, _) => SetActive(captured);
            btn.MouseRightButtonUp += (_, _) => CloseSession(captured);
            items.Add(btn);
        }
        TabsList.ItemsSource = items;
    }

    private void OnAddClicked(object sender, RoutedEventArgs e) => NewSession();

    private static string ResolveDefaultShell()
    {
        // Auto-detect order: pwsh.exe → powershell.exe → cmd.exe
        foreach (var name in new[] { "pwsh.exe", "powershell.exe", "cmd.exe" })
        {
            var path = FindInPath(name);
            if (path != null) return path;
        }
        return "cmd.exe";
    }

    private static string? FindInPath(string exe)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
        foreach (var p in paths)
        {
            try
            {
                var candidate = Path.Combine(p, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private string ResolveStartingDirectory()
    {
        // Implemented in Task 37 (needs MainWindow wiring to know active file/folder/workspace)
        return Environment.GetEnvironmentVariable("USERPROFILE") ?? ".";
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (VisualParent == null)
        {
            // Panel removed — dispose all sessions
            foreach (var s in _sessions) try { s.Dispose(); } catch { }
            _sessions.Clear();
            _active = null;
        }
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 4: Commit**

```bash
git add Volt/UI/Terminal/TerminalPanel.xaml Volt/UI/Terminal/TerminalPanel.xaml.cs
git commit -m "feat(terminal): add TerminalPanel IPanel implementation with tab strip"
```

---

**Stage 7 done.** The WPF layer is complete — view, session composition, and panel with multi-session management.

---

## Stage 8 — MainWindow Integration

### Task 35: AppSettings fields

**Files:**
- Modify: `Volt/AppSettings.cs`

- [ ] **Step 1: Find EditorSettings section in AppSettings.cs** and add three properties

```csharp
public string? TerminalShellPath { get; set; }
public string? TerminalShellArgs { get; set; }
public int TerminalScrollbackLines { get; set; } = 10_000;
```

(Match the existing property style — record with-initializers, auto-props, whichever Volt uses.)

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add Volt/AppSettings.cs
git commit -m "feat(settings): add terminal shell path, args, scrollback settings"
```

---

### Task 36: Register TerminalPanel in MainWindow + add command palette commands

**Files:**
- Modify: `Volt/UI/MainWindow.xaml.cs`
- Modify: `Volt/UI/CommandPaletteCommands.cs`

- [ ] **Step 1: Add field + registration in MainWindow constructor**

In `MainWindow.xaml.cs`, add a field:

```csharp
private readonly TerminalPanel _terminalPanel = new();
```

In the constructor, after the existing `Shell.RegisterPanel` calls for explorer, add:

```csharp
Shell.RegisterPanel(_terminalPanel, PanelPlacement.Bottom, defaultSize: 240);
```

- [ ] **Step 2: Register the shortcut allowlist on the active terminal view whenever a session is created**

Since shortcuts must match whatever `MainWindow` has bound, expose a helper:

```csharp
// In MainWindow.xaml.cs
internal void RegisterTerminalAllowlist(TerminalView view)
{
    // Hardcoded list of commands that should bubble past the terminal.
    // Keep in sync with InputBindings / global KeyDown handler.
    view.AddAllowlistedShortcut(Key.P, ModifierKeys.Control | ModifierKeys.Shift); // command palette
    view.AddAllowlistedShortcut(Key.Tab, ModifierKeys.Control);                    // next tab
    view.AddAllowlistedShortcut(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift); // prev tab
    view.AddAllowlistedShortcut(Key.OemComma, ModifierKeys.Control);               // settings
    view.AddAllowlistedShortcut(Key.B, ModifierKeys.Control);                      // toggle explorer
    view.AddAllowlistedShortcut(Key.OemTilde, ModifierKeys.Control);               // toggle terminal
    // Add any other MainWindow-global shortcuts here.
}
```

In `TerminalPanel.NewSession`, call this immediately after creating the session:

```csharp
// after SetActive(s);
((MainWindow)Application.Current.MainWindow).RegisterTerminalAllowlist(s.View);
```

- [ ] **Step 3: Add command palette commands**

In `Volt/UI/CommandPaletteCommands.cs`, add entries for "Terminal: Toggle" and "Terminal: New Session" using the existing command-building pattern:

```csharp
// Inside whatever BuildCommands()/GetCommands() method exists
commands.Add(new PaletteCommand(
    Title: "Terminal: Toggle",
    Action: () => mainWindow.Shell.TogglePanel("terminal")));

commands.Add(new PaletteCommand(
    Title: "Terminal: New Session",
    Action: () =>
    {
        mainWindow.Shell.ShowPanel("terminal");
        // Reach into the panel to spawn a new session
        if (mainWindow.Shell.FindPanel("terminal") is TerminalPanel tp)
            tp.NewSession();
    }));
```

Match the actual constructor signature `PaletteCommand` uses in `CommandPaletteCommands.cs` — the above is illustrative.

- [ ] **Step 4: Bind Ctrl+` globally**

In `MainWindow.xaml` `InputBindings`:

```xml
<KeyBinding Modifiers="Control" Key="Oem3" Command="{Binding ToggleTerminalCommand}"/>
```

Or in code-behind `PreviewKeyDown` handler, add a case for `Ctrl+Oem3` that calls `Shell.TogglePanel("terminal")`.

- [ ] **Step 5: Build + manual smoke test**

Run: `dotnet build Volt.sln && dotnet run --project Volt/Volt.csproj`
Expected: app launches. Toggle terminal via command palette; a session starts; type `echo hi`; see output.

- [ ] **Step 6: Commit**

```bash
git add Volt/UI/MainWindow.xaml Volt/UI/MainWindow.xaml.cs Volt/UI/CommandPaletteCommands.cs Volt/UI/Terminal/TerminalPanel.xaml.cs
git commit -m "feat(terminal): register TerminalPanel and wire command palette + shortcuts"
```

---

### Task 37: Implement ResolveStartingDirectory per spec rules

**Files:**
- Modify: `Volt/UI/Terminal/TerminalPanel.xaml.cs`

- [ ] **Step 1: Expose current workspace/folder/active-tab state accessor on MainWindow**

Add to MainWindow (if not already present):

```csharp
public string? ActiveFilePath { get; } // from active tab
public string? OpenFolderPath { get; } // set when a single folder is open
public Workspace? ActiveWorkspace { get; } // null if no workspace
```

(If these are already accessible via existing fields, skip creation and just reference them.)

- [ ] **Step 2: Replace `ResolveStartingDirectory` stub**

```csharp
    private string ResolveStartingDirectory()
    {
        var mw = (MainWindow)Application.Current.MainWindow;
        var workspace = mw.ActiveWorkspace;
        var activeFile = mw.ActiveFilePath;

        // Rule 1: No workspace, single file open → file's directory
        if (workspace == null && mw.OpenFolderPath == null && activeFile != null)
        {
            var dir = Path.GetDirectoryName(activeFile);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }

        // Rule 2: Single folder open → folder root (always, regardless of active file)
        if (workspace == null && mw.OpenFolderPath != null)
            return mw.OpenFolderPath;

        // Rule 3: Multi-folder workspace with active tab → file's parent dir
        if (workspace != null && activeFile != null)
        {
            var dir = Path.GetDirectoryName(activeFile);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }

        // Rule 4: Multi-folder workspace with no active tab → first folder
        if (workspace != null && workspace.Folders.Count > 0)
            return workspace.Folders[0];

        // Rule 5: Fallback — user home
        return Environment.GetEnvironmentVariable("USERPROFILE") ?? ".";
    }
```

**Note:** adjust `workspace.Folders` to match the actual property name on `Workspace`.

- [ ] **Step 3: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 4: Commit**

```bash
git add Volt/UI/Terminal/TerminalPanel.xaml.cs Volt/UI/MainWindow.xaml.cs
git commit -m "feat(terminal): resolve starting cwd per spec rules"
```

---

### Task 38: Persist detected shell on first use

**Files:**
- Modify: `Volt/UI/Terminal/TerminalPanel.xaml.cs`

- [ ] **Step 1: Update `NewSession` to persist the detected shell**

Replace the shell resolution lines:

```csharp
        var shell = AppSettings.Current.Editor.TerminalShellPath;
        if (string.IsNullOrEmpty(shell))
        {
            shell = ResolveDefaultShell();
            AppSettings.Current.Editor.TerminalShellPath = shell;
            AppSettings.Save();
        }
```

- [ ] **Step 2: Build**

Run: `dotnet build Volt.sln`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Terminal/TerminalPanel.xaml.cs
git commit -m "feat(terminal): persist auto-detected shell on first use"
```

---

**Stage 8 done.** Terminal is wired into the app — command palette opens it, shortcuts work, starting directory respects workspace state, shell is auto-detected and persisted.

---

## Stage 9 — Benchmarks

### Task 39: VtParser benchmarks

**Files:**
- Create: `Volt.Benchmarks/Terminal/VtParserBenchmarks.cs`

- [ ] **Step 1: Create benchmark class**

```csharp
// Volt.Benchmarks/Terminal/VtParserBenchmarks.cs
using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks.Terminal;

[MemoryDiagnoser]
public class VtParserBenchmarks
{
    private byte[] _plainAscii = Array.Empty<byte>();
    private byte[] _sgrHeavy = Array.Empty<byte>();

    private sealed class NullHandler : IVtEventHandler
    {
        public void Print(char ch) { }
        public void Execute(byte ctrl) { }
        public void CsiDispatch(char final, ReadOnlySpan<int> p, ReadOnlySpan<char> i) { }
        public void EscDispatch(char final, ReadOnlySpan<char> i) { }
        public void OscDispatch(int cmd, string data) { }
    }

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 20_000; i++) sb.AppendLine("The quick brown fox jumps over the lazy dog");
        _plainAscii = Encoding.ASCII.GetBytes(sb.ToString());

        sb.Clear();
        for (int i = 0; i < 5_000; i++)
            sb.Append("\u001b[31mred \u001b[1;32mbold green \u001b[0;34mblue \u001b[0m normal ");
        _sgrHeavy = Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Benchmark]
    public void ParsePlainAscii()
    {
        var sm = new VtStateMachine(new NullHandler());
        sm.Feed(_plainAscii);
    }

    [Benchmark]
    public void ParseSgrHeavy()
    {
        var sm = new VtStateMachine(new NullHandler());
        sm.Feed(_sgrHeavy);
    }
}
```

- [ ] **Step 2: Run benchmarks**

Run: `dotnet run -c Release --project Volt.Benchmarks -- --filter *VtParser*`
Expected: two benchmark results with ns/op numbers. Note the numbers as baseline.

- [ ] **Step 3: Commit**

```bash
git add Volt.Benchmarks/Terminal/VtParserBenchmarks.cs
git commit -m "bench(terminal): add VT parser benchmarks"
```

---

### Task 40: TerminalGrid benchmarks

**Files:**
- Create: `Volt.Benchmarks/Terminal/TerminalGridBenchmarks.cs`

- [ ] **Step 1: Create benchmark class**

```csharp
// Volt.Benchmarks/Terminal/TerminalGridBenchmarks.cs
using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks.Terminal;

[MemoryDiagnoser]
public class TerminalGridBenchmarks
{
    private TerminalGrid _grid = null!;

    [GlobalSetup]
    public void Setup() => _grid = new TerminalGrid(24, 80, 10_000);

    [Benchmark]
    public void WriteCellSequential()
    {
        for (int i = 0; i < 1_000_000; i++)
            _grid.WriteCell(i % 24, i % 80, 'x', CellAttr.None);
    }

    [Benchmark]
    public void ScrollUp1Row()
    {
        for (int i = 0; i < 1_000; i++)
            _grid.ScrollUp(1);
    }

    [Benchmark]
    public void ResizeLarge()
    {
        var g = new TerminalGrid(24, 80, 10_000);
        g.Resize(50, 200);
        g.Resize(24, 80);
    }
}
```

- [ ] **Step 2: Run benchmarks**

Run: `dotnet run -c Release --project Volt.Benchmarks -- --filter *TerminalGrid*`
Expected: three benchmark results. Note baselines.

- [ ] **Step 3: Commit**

```bash
git add Volt.Benchmarks/Terminal/TerminalGridBenchmarks.cs
git commit -m "bench(terminal): add TerminalGrid benchmarks"
```

---

**Stage 9 done.** All stages complete. The terminal panel is implemented, tested, benchmarked, and integrated.

---

## Final Smoke Test Checklist

Before calling the feature shipped, manually verify:

- [ ] Open Volt, toggle terminal via command palette. Panel appears at bottom.
- [ ] "+" button spawns a new session; type `echo hello`, see output.
- [ ] Type `cd ..` and `dir`, see directory listing with colors if shell produces them.
- [ ] Resize the panel (drag the splitter); shell output reflows.
- [ ] `vim test.txt` enters alt buffer; `:q` exits; no vim garbage in scrollback.
- [ ] `exit` closes the tab; if last tab, panel shows empty state.
- [ ] Open three tabs in Volt editor; focus terminal; Ctrl+Tab switches editor tabs (allowlist passes through).
- [ ] Ctrl+Shift+P while terminal focused opens command palette.
- [ ] Ctrl+C with no selection sends SIGINT to a running `ping -t google.com`.
- [ ] Ctrl+Shift+V pastes clipboard text into the shell.
- [ ] Switch theme while terminal is running; colors update.
- [ ] Close the panel (Close button or toggle off); all shell processes die.
- [ ] Close Volt with terminal sessions running; no orphan `cmd.exe`/`pwsh.exe` processes in Task Manager.

---

## Known Deferrals (explicitly NOT in v1)

Per the spec's non-goals, these are acknowledged gaps that are acceptable for v1:

- Shell integration / OSC 633
- Link detection / Ctrl+click
- In-terminal find
- Sixel / ReGIS / images
- Font ligatures
- Bracketed paste mode (multiline pastes execute line-by-line)
- Session persistence across app restart
- Scrolled-back rendering (scrollback is captured but not visible when scrolled up — Task 32 noted this as a follow-up)
- Selection + copy inside the terminal view (paste works; copy is stubbed — if you want copy in v1, add selection to `TerminalView` as an extra task before Task 34)
- Text selection via mouse drag

If any of these cross from "acceptable deferral" to "actually needed", add a follow-up task rather than inlining into this plan.
