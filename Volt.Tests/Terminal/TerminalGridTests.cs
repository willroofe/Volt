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
}
