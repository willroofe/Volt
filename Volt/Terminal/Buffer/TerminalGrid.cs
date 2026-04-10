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
