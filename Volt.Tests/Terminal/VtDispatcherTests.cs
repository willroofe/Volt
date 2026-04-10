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
