using System;
using System.Collections.Generic;

namespace Volt;

public sealed partial class TerminalGrid
{
    private Cell[,] _main;
    private Cell[,]? _alt;
    private readonly int _scrollbackLines;
    private GridRegion _dirty;
    public Cell Pen = new Cell { FgIndex = -1, BgIndex = -1, Attr = CellAttr.None, Glyph = ' ' };
    private bool _pendingWrap;

    public int Rows { get; private set; }
    public int Cols { get; private set; }
    public (int row, int col) Cursor { get; private set; }
    public bool CursorVisible { get; set; } = true;
    public bool UsingAltBuffer { get; private set; }
    public GridRegion Dirty => _dirty;
    public int ScrollTop { get; private set; }
    public int ScrollBottom { get; private set; }

    public event Action? Changed;

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
}
