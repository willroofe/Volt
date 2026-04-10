using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class GridRegionTests
{
    [Fact]
    public void Empty_ReturnsSentinel()
    {
        var r = new GridRegion();
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void MarkDirty_ExpandsRange()
    {
        var r = new GridRegion();
        r.MarkDirty(5);
        r.MarkDirty(3);
        r.MarkDirty(10);
        Assert.False(r.IsEmpty);
        Assert.Equal(3, r.MinRow);
        Assert.Equal(10, r.MaxRow);
    }

    [Fact]
    public void Clear_ResetsToEmpty()
    {
        var r = new GridRegion();
        r.MarkDirty(5);
        r.Clear();
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void MarkRange_ExpandsOverMultipleRows()
    {
        var r = new GridRegion();
        r.MarkDirtyRange(4, 8);
        Assert.Equal(4, r.MinRow);
        Assert.Equal(8, r.MaxRow);
    }
}
