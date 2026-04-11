using System;
using System.Collections.Generic;

namespace Volt;

public enum EraseMode { ToEnd, ToStart, All }

public sealed partial class TerminalGrid
{
    private Cell[,] _main;
    private Cell[,]? _alt;
    private readonly int _scrollbackLines;
    private GridRegion _dirty;
    public Cell Pen = new Cell { FgIndex = -1, BgIndex = -1, Attr = CellAttr.None, Glyph = ' ' };
    private bool _pendingWrap;

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

    private Cell[][] _scrollback = Array.Empty<Cell[]>();
    private int _scrollbackHead; // points at oldest
    private int _scrollbackCount;

    public int ScrollbackCount => _scrollbackCount;

    public int Rows { get; private set; }
    public int Cols { get; private set; }
    public (int row, int col) Cursor { get; private set; }
    public bool CursorVisible { get; set; } = true;
    public bool UsingAltBuffer { get; private set; }
    public GridRegion Dirty => _dirty;
    public int ScrollTop { get; private set; }
    public int ScrollBottom { get; private set; }

    public event Action? Changed;
    public event Action? BellRang;

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
        if (col < 0 || col >= Cols) return ref _sink;
        if (row >= 0 && row < Rows)
            return ref ActiveBuffer[row, col];
        // Negative row = scrollback; -1 = newest, -N = oldest still live
        int scrollbackIndex = _scrollbackCount + row; // row is negative → positive offset from head
        if (scrollbackIndex < 0 || scrollbackIndex >= _scrollbackCount) return ref _sink;
        int ringIndex = (_scrollbackHead + scrollbackIndex) % _scrollbackLines;
        var line = _scrollback[ringIndex];
        if (line == null || col >= line.Length) return ref _sink;
        return ref line[col];
    }

    private static Cell _sink = Cell.Blank;
    private Cell[,] ActiveBuffer => UsingAltBuffer ? _alt! : _main;

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

    public void Bell() => BellRang?.Invoke();

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

    private void PushToScrollback(int n)
    {
        for (int i = 0; i < n; i++)
        {
            var row = new Cell[Cols];
            for (int c = 0; c < Cols; c++) row[c] = ActiveBuffer[i, c];
            PushRowToScrollback(row);
        }
    }

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

    public void Resize(int rows, int cols)
    {
        rows = Math.Max(1, rows);
        cols = Math.Max(1, cols);
        if (rows == Rows && cols == Cols) return;

        int oldRows = Rows;
        int oldCols = Cols;
        var (oldCr, oldCc) = Cursor;

        var newMain = AllocBlank(rows, cols);
        int copyRows = Math.Min(oldRows, rows);
        int copyCols = Math.Min(oldCols, cols);
        // Shorter grid: keep bottom rows (prompt); taller: keep top.
        int srcRow0 = rows < oldRows ? oldRows - copyRows : 0;
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                newMain[r, c] = _main[srcRow0 + r, c];
        _main = newMain;

        if (_alt != null)
            _alt = AllocBlank(rows, cols);

        Rows = rows;
        Cols = cols;
        ReallocateScrollbackRowsToWidth(cols);
        ScrollTop = 0;
        ScrollBottom = Rows - 1;

        int newCr = rows < oldRows ? oldCr - srcRow0 : oldCr;
        SetCursor(newCr, oldCc);
        _pendingWrap = false;
        _dirty.MarkDirtyRange(0, Rows - 1);
        Changed?.Invoke();
    }

    /// <summary>
    /// Clears the main screen to blanks and moves the cursor to the origin. Scrollback is unchanged.
    /// Called after the host viewport changes cell row/column count so a shell SIGWINCH redraw does not
    /// leave stale text plus a large blank middle (new rows) plus duplicate banner/prompt at the bottom.
    /// </summary>
    public void ClearMainScreenHome()
    {
        if (UsingAltBuffer) return;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                _main[r, c] = Cell.Blank;
        SetCursor(0, 0);
        Pen = new Cell { FgIndex = -1, BgIndex = -1, Attr = CellAttr.None, Glyph = ' ' };
        _dirty.MarkDirtyRange(0, Rows - 1);
        Changed?.Invoke();
    }

    /// <summary>Scrollback lines are stored as <see cref="Cell"/>[] per row — width must stay in sync with <see cref="Cols"/> after resize.</summary>
    private void ReallocateScrollbackRowsToWidth(int newCols)
    {
        if (_scrollback.Length == 0) return;
        for (int i = 0; i < _scrollback.Length; i++)
        {
            var row = _scrollback[i];
            if (row == null || row.Length == newCols) continue;
            var newRow = new Cell[newCols];
            int copy = Math.Min(row.Length, newCols);
            for (int c = 0; c < copy; c++)
                newRow[c] = row[c];
            for (int c = copy; c < newCols; c++)
                newRow[c] = Cell.Blank;
            _scrollback[i] = newRow;
        }
    }
}
