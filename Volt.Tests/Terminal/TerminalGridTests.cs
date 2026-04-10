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
}
