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
            default: break;
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
