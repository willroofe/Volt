using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class CellTests
{
    [Fact]
    public void DefaultCell_HasBlankGlyphAndDefaultColors()
    {
        var cell = new Cell();
        Assert.Equal('\0', cell.Glyph);
        Assert.Equal(-1, cell.FgIndex);
        Assert.Equal(-1, cell.BgIndex);
        Assert.Equal(CellAttr.None, cell.Attr);
    }

    [Fact]
    public void CellAttr_SupportsFlagCombination()
    {
        var combined = CellAttr.Bold | CellAttr.Italic | CellAttr.Underline;
        Assert.True(combined.HasFlag(CellAttr.Bold));
        Assert.True(combined.HasFlag(CellAttr.Italic));
        Assert.True(combined.HasFlag(CellAttr.Underline));
        Assert.False(combined.HasFlag(CellAttr.Inverse));
    }
}
