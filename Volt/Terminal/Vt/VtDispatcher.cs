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
