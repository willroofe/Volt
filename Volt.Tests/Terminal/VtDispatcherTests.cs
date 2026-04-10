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
        var s = System.Text.Encoding.ASCII.GetString(sent!);
        Assert.Equal("\u001b[5;10R", s);
    }
}
