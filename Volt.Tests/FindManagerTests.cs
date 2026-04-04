using Xunit;
using Volt;

namespace Volt.Tests;

public class FindManagerTests
{
    [Fact]
    public void Search_FindsMatchesWithPositions()
    {
        var buf = TestHelpers.MakeBuffer("hello world\nhello again");
        var find = new FindManager();

        find.Search(buf, "hello", matchCase: false, caretLine: 0, caretCol: 0);

        Assert.Equal(2, find.MatchCount);
        Assert.Equal((0, 0, 5), find.Matches[0]);
        Assert.Equal((1, 0, 5), find.Matches[1]);
    }

    [Fact]
    public void Search_CaseSensitive_FiltersCorrectly()
    {
        var buf = TestHelpers.MakeBuffer("Hello hello HELLO");
        var find = new FindManager();

        find.Search(buf, "hello", matchCase: true, caretLine: 0, caretCol: 0);

        Assert.Equal(1, find.MatchCount);
        Assert.Equal((0, 6, 5), find.Matches[0]);
    }

    [Fact]
    public void MoveNext_WrapsAround()
    {
        var buf = TestHelpers.MakeBuffer("aaa\naaa");
        var find = new FindManager();
        find.Search(buf, "aaa", matchCase: false, caretLine: 0, caretCol: 0);

        Assert.Equal(0, find.CurrentIndex);

        find.MoveNext();
        Assert.Equal(1, find.CurrentIndex);

        find.MoveNext();
        Assert.Equal(0, find.CurrentIndex);
    }

    [Fact]
    public void MovePrevious_WrapsAround()
    {
        var buf = TestHelpers.MakeBuffer("aaa\naaa");
        var find = new FindManager();
        find.Search(buf, "aaa", matchCase: false, caretLine: 0, caretCol: 0);

        Assert.Equal(0, find.CurrentIndex);

        find.MovePrevious();
        Assert.Equal(1, find.CurrentIndex);
    }

    [Fact]
    public void Search_NoMatch_ReturnsZero()
    {
        var buf = TestHelpers.MakeBuffer("hello world");
        var find = new FindManager();

        find.Search(buf, "xyz", matchCase: false, caretLine: 0, caretCol: 0);

        Assert.Equal(0, find.MatchCount);
        Assert.Null(find.GetCurrentMatch());
    }
}
