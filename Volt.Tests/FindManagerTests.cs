using Xunit;
using Volt;

namespace Volt.Tests;

public class FindManagerTests
{
    private static FindManager Search(
        TextBuffer buffer,
        string query,
        bool matchCase = false,
        bool useRegex = false,
        bool wholeWord = false,
        int caretLine = 0,
        int caretCol = 0,
        FindSelectionRange? selection = null,
        int retainedMatchLimit = 100_000,
        int batchLineBudget = 512)
    {
        var find = new FindManager(retainedMatchLimit, batchLineBudget);
        find.StartSearch(buffer, new FindQuery(query, matchCase, useRegex, wholeWord, selection), caretLine, caretCol);
        Drain(find);
        return find;
    }

    private static void Drain(FindManager find)
    {
        while (find.RunNextBatch()) { }
    }

    [Fact]
    public void Search_FindsMatchesWithPositions()
    {
        var buf = TestHelpers.MakeBuffer("hello world\nhello again");

        var find = Search(buf, "hello");

        Assert.Equal(2, find.Snapshot.TotalMatches);
        Assert.Equal(new FindMatch(0, 0, 5), find.Snapshot.RetainedMatches[0]);
        Assert.Equal(new FindMatch(1, 0, 5), find.Snapshot.RetainedMatches[1]);
    }

    [Fact]
    public void Search_CaseSensitive_FiltersCorrectly()
    {
        var buf = TestHelpers.MakeBuffer("Hello hello HELLO");

        var find = Search(buf, "hello", matchCase: true);

        Assert.Equal(1, find.Snapshot.TotalMatches);
        Assert.Equal(new FindMatch(0, 6, 5), find.Snapshot.RetainedMatches[0]);
    }

    [Fact]
    public void Search_RegexAndWholeWord_FindsExpectedMatches()
    {
        var buf = TestHelpers.MakeBuffer("cat scatter cat\ncat1 cat");

        var find = Search(buf, "cat", wholeWord: true);

        Assert.Equal(3, find.Snapshot.TotalMatches);
        Assert.All(find.Snapshot.RetainedMatches, m => Assert.Equal(3, m.Length));

        var regexFind = Search(buf, "c.t", useRegex: true);
        Assert.Equal(5, regexFind.Snapshot.TotalMatches);
    }

    [Fact]
    public void Search_InvalidRegex_PublishesInvalidSnapshot()
    {
        var buf = TestHelpers.MakeBuffer("hello");
        var find = new FindManager();

        find.StartSearch(buf, new FindQuery("(", MatchCase: false, UseRegex: true), 0, 0);

        Assert.True(find.Snapshot.InvalidRegex);
        Assert.True(find.Snapshot.IsComplete);
        Assert.Equal(0, find.Snapshot.TotalMatches);
    }

    [Fact]
    public void Search_SelectionBounds_FiltersMatches()
    {
        var buf = TestHelpers.MakeBuffer("aaa aaa", "aaa aaa");
        var selection = new FindSelectionRange(0, 4, 1, 3);

        var find = Search(buf, "aaa", selection: selection);

        Assert.Equal(2, find.Snapshot.TotalMatches);
        Assert.Equal(new FindMatch(0, 4, 3), find.Snapshot.RetainedMatches[0]);
        Assert.Equal(new FindMatch(1, 0, 3), find.Snapshot.RetainedMatches[1]);
    }

    [Fact]
    public void Search_Cancellation_ReplacesPreviousGeneration()
    {
        var buf = TestHelpers.MakeBuffer("old", "new", "new");
        var find = new FindManager(batchLineBudget: 1);

        find.StartSearch(buf, new FindQuery("old"), 0, 0);
        find.RunNextBatch();
        int oldGeneration = find.Snapshot.Generation;

        find.StartSearch(buf, new FindQuery("new"), 0, 0);
        Drain(find);

        Assert.NotEqual(oldGeneration, find.Snapshot.Generation);
        Assert.Equal(2, find.Snapshot.TotalMatches);
        Assert.All(find.Snapshot.RetainedMatches, m => Assert.Equal("new", buf[m.Line].Substring(m.Col, m.Length)));
    }

    [Fact]
    public void Search_CaretFirst_FindsNearbyMatchBeforeEarlierLines()
    {
        var buf = TestHelpers.MakeBuffer("target first", "noise", "target near");
        var find = new FindManager(batchLineBudget: 1);

        find.StartSearch(buf, new FindQuery("target"), caretLine: 2, caretCol: 0);
        find.RunNextBatch();

        Assert.Equal(new FindMatch(2, 0, 6), find.Snapshot.CurrentMatch);
        Assert.True(find.Snapshot.IsSearching);
    }

    [Fact]
    public void Search_PublishesPartialProgress()
    {
        var buf = TestHelpers.MakeBuffer("one", "two", "three");
        var find = new FindManager(batchLineBudget: 1);

        find.StartSearch(buf, new FindQuery("z"), 0, 0);
        find.RunNextBatch();

        Assert.True(find.Snapshot.IsSearching);
        Assert.InRange(find.Snapshot.Progress, 0.01, 0.99);
    }

    [Fact]
    public void Search_RetentionLimit_KeepsCountingAndDisablesReplaceAll()
    {
        var buf = TestHelpers.MakeBuffer("a a a a");

        var find = Search(buf, "a", retainedMatchLimit: 2);

        Assert.Equal(4, find.Snapshot.TotalMatches);
        Assert.Equal(2, find.Snapshot.RetainedMatchCount);
        Assert.True(find.Snapshot.RetentionLimitExceeded);
        Assert.False(find.Snapshot.CanReplaceAll);
    }

    [Fact]
    public void GetMatchesInRange_ComputesVisibleMatchesAfterRetentionLimit()
    {
        var buf = TestHelpers.MakeBuffer("hit", "hit", "hit");

        var find = Search(buf, "hit", retainedMatchLimit: 1);
        var visible = find.GetMatchesInRange(2, 2);

        Assert.True(find.Snapshot.RetentionLimitExceeded);
        Assert.Contains(new FindMatch(2, 0, 3), visible);
    }

    [Fact]
    public async Task MoveNext_WrapsAroundRetainedMatches()
    {
        var buf = TestHelpers.MakeBuffer("aaa\naaa");
        var find = Search(buf, "aaa");

        Assert.Equal(0, find.Snapshot.CurrentRetainedIndex);

        await find.MoveNextAsync();
        Assert.Equal(1, find.Snapshot.CurrentRetainedIndex);

        await find.MoveNextAsync();
        Assert.Equal(0, find.Snapshot.CurrentRetainedIndex);
    }

    [Fact]
    public async Task MovePrevious_WrapsAroundRetainedMatches()
    {
        var buf = TestHelpers.MakeBuffer("aaa\naaa");
        var find = Search(buf, "aaa");

        Assert.Equal(0, find.Snapshot.CurrentRetainedIndex);

        await find.MovePreviousAsync();
        Assert.Equal(1, find.Snapshot.CurrentRetainedIndex);
    }

    [Fact]
    public async Task MovePrevious_WithRetentionLimit_WrapsToLastDocumentMatch()
    {
        var buf = TestHelpers.MakeBuffer("hit", "hit", "hit", "hit", "hit");
        var find = Search(buf, "hit", retainedMatchLimit: 2);

        await find.MovePreviousAsync();

        Assert.Equal(new FindMatch(4, 0, 3), find.Snapshot.CurrentMatch);
        Assert.Equal(-1, find.Snapshot.CurrentRetainedIndex);
    }

    [Fact]
    public async Task MoveNext_WithRetentionLimit_CanNavigatePastRetainedWindow()
    {
        var buf = TestHelpers.MakeBuffer("hit", "hit", "hit", "hit");
        var find = Search(buf, "hit", retainedMatchLimit: 2);

        await find.MoveNextAsync();
        await find.MoveNextAsync();

        Assert.Equal(new FindMatch(2, 0, 3), find.Snapshot.CurrentMatch);
        Assert.Equal(-1, find.Snapshot.CurrentRetainedIndex);
    }

    [Fact]
    public void Search_NoMatch_ReturnsZero()
    {
        var buf = TestHelpers.MakeBuffer("hello world");

        var find = Search(buf, "xyz");

        Assert.Equal(0, find.Snapshot.TotalMatches);
        Assert.Null(find.GetCurrentMatch());
    }
}
