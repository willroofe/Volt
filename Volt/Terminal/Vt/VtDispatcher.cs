using System;

namespace Volt;

public sealed class VtDispatcher : IVtEventHandler
{
    private readonly TerminalGrid _grid;
    public event Action<string>? TitleChanged;
    public event Action<byte[]>? ResponseRequested;

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
            case '@': _grid.InsertChars(p0default1); break;
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
            case 'P': _grid.DeleteChars(p0default1); break;
            case 'S': _grid.ScrollUp(p0default1); break;
            case 'T': _grid.ScrollDown(p0default1); break;
            case 'n': HandleDsr(p0raw); break;
            case 'r':
                if (p.Length == 0) _grid.SetScrollRegion(0, _grid.Rows - 1);
                else _grid.SetScrollRegion(p0default1 - 1, p1default1 - 1);
                break;
            case 'm': HandleSgr(p); break;
            case 'h': HandleMode(p, i, set: true); break;
            case 'l': HandleMode(p, i, set: false); break;
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
                    // Mouse/bracketed-paste modes — deferred to post-v1, silently acknowledged
                    break;
                default: break;
            }
        }
    }

    private void HandleDsr(int mode)
    {
        if (mode != 6) return;
        var (r, c) = _grid.Cursor;
        var response = $"\u001b[{r + 1};{c + 1}R";
        ResponseRequested?.Invoke(System.Text.Encoding.ASCII.GetBytes(response));
    }
}
