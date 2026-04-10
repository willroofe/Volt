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
        int p0default1 = P(p, 0, 1);
        int p1default1 = P(p, 1, 1);
        int p0raw = p.Length > 0 ? p[0] : 0;
        switch (final)
        {
            case 'A': CursorUp(p0default1); break;
            case 'B': CursorDown(p0default1); break;
            case 'C': CursorForward(p0default1); break;
            case 'D': CursorBack(p0default1); break;
            case 'E': CarriageReturn(); CursorDown(p0default1); break;
            case 'F': CarriageReturn(); CursorUp(p0default1); break;
            case 'G': CursorHorizontalAbsolute(p0default1); break;
            case 'H': case 'f': CursorPosition(p0default1, p1default1); break;
            case 'J': EraseDisplay(p0raw); break;
            case 'K': EraseLine(p0raw); break;
            case 'L': _grid.InsertLines(p0default1); break;
            case 'M': _grid.DeleteLines(p0default1); break;
            case 'S': _grid.ScrollUp(p0default1); break;
            case 'T': _grid.ScrollDown(p0default1); break;
            case 'r':
                if (p.Length == 0) _grid.SetScrollRegion(0, _grid.Rows - 1);
                else _grid.SetScrollRegion(p0default1 - 1, p1default1 - 1);
                break;
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

    private void EraseDisplay(int mode)
    {
        _grid.EraseInDisplay(mode switch { 1 => EraseMode.ToStart, 2 => EraseMode.All, _ => EraseMode.ToEnd });
    }

    private void EraseLine(int mode)
    {
        _grid.EraseInLine(mode switch { 1 => EraseMode.ToStart, 2 => EraseMode.All, _ => EraseMode.ToEnd });
    }
}
