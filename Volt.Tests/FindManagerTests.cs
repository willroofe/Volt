using Xunit;
using Volt;
using System.Diagnostics;

namespace Volt.Tests;

public class FindManagerTests
{
    private static void Search(FindManager find, TextBuffer buf, string query, bool matchCase = false)
        => find.Search(buf, query, matchCase, caretLine: 0, caretCol: 0);

    [Fact]
    public void Search_FindsMatchesWithPositions()
    {
        var buf = TestHelpers.MakeBuffer("hello world\nhello again");
        var find = new FindManager();

        Search(find, buf, "hello");

        Assert.Equal(2, find.MatchCount);
        Assert.Equal((0, 0, 5), find.Matches[0]);
        Assert.Equal((1, 0, 5), find.Matches[1]);
    }

    [Fact]
    public void Search_CaseSensitive_FiltersCorrectly()
    {
        var buf = TestHelpers.MakeBuffer("Hello hello HELLO");
        var find = new FindManager();

        Search(find, buf, "hello", matchCase: true);

        Assert.Equal(1, find.MatchCount);
        Assert.Equal((0, 6, 5), find.Matches[0]);
    }

    [Fact]
    public void MoveNext_WrapsAround()
    {
        var buf = TestHelpers.MakeBuffer("aaa\naaa");
        var find = new FindManager();
        Search(find, buf, "aaa");

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
        Search(find, buf, "aaa");

        Assert.Equal(0, find.CurrentIndex);

        find.MovePrevious();
        Assert.Equal(1, find.CurrentIndex);
    }

    [Fact]
    public void Search_NoMatch_ReturnsZero()
    {
        var buf = TestHelpers.MakeBuffer("hello world");
        var find = new FindManager();

        Search(find, buf, "xyz");

        Assert.Equal(0, find.MatchCount);
        Assert.Null(find.GetCurrentMatch());
    }

    [Fact]
    public async Task StartSearch_CompletesWithExactProgressiveCount()
    {
        var buf = TestHelpers.MakeBuffer("alpha beta\nbeta gamma\nalpha beta");
        var find = new FindManager();

        find.StartSearch(buf, "beta", matchCase: false, caretLine: 0, caretCol: 0);
        bool found = await find.FindNearestAsync(0, 0);
        await WaitUntil(() => find.HasExactMatchCount);

        Assert.True(found);
        Assert.Equal(3, find.MatchCount);
        Assert.Equal((0, 6, 4), find.GetCurrentMatch());
        Assert.Equal("1 of 3", find.StatusText);
    }

    [Fact]
    public void GetMatchesInRange_ReturnsOnlyViewportMatches()
    {
        var buf = TestHelpers.MakeBuffer(string.Join('\n', Enumerable.Range(0, 100)
            .Select(i => i % 10 == 0 ? "needle here" : "plain text")));
        var find = new FindManager();

        find.StartSearch(buf, "needle", matchCase: false, caretLine: 0, caretCol: 0);

        var visible = find.GetMatchesInRange(20, 35);

        Assert.Equal([(20, 0, 6), (30, 0, 6)], visible);
        find.Clear();
    }

    [Fact]
    public async Task StartSearch_RestrictsMatchesToSelectionBounds()
    {
        var buf = TestHelpers.MakeBuffer("needle outside\ninside needle\noutside needle");
        var find = new FindManager();

        find.StartSearch(buf, "needle", matchCase: false, caretLine: 0, caretCol: 0,
            selectionBounds: (1, 0, 1, "inside needle".Length));
        bool found = await find.FindNearestAsync(0, 0);
        await WaitUntil(() => find.HasExactMatchCount);

        Assert.True(found);
        Assert.Equal(1, find.KnownMatchCount);
        Assert.Equal((1, 7, 6), find.GetCurrentMatch());
        Assert.Equal([(1, 7, 6)], find.GetMatchesInRange(0, 2));
        find.Clear();
    }

    [Fact]
    public async Task StartSearch_SelectionCountEnumeratesOnlySelectedLines()
    {
        var source = new CountingTextSource(lineCount: 1_000_000, "needle");
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = source,
            LineEnding = "\n"
        });
        var find = new FindManager();

        find.StartSearch(buffer, "needle", matchCase: false, caretLine: 0, caretCol: 0,
            selectionBounds: (500_000, 0, 500_003, "needle".Length));
        await WaitUntil(() => find.HasExactMatchCount);

        Assert.Equal(4, find.KnownMatchCount);
        Assert.Equal(4, source.EnumeratedLineReads);
        find.Clear();
    }

    [Fact]
    public async Task FindNearestAsync_SelectionStartsAtSelectionInsteadOfDocumentStart()
    {
        var source = new GuardedTextSource(lineCount: 1_000_000, allowedStartLine: 500_000, allowedLineCount: 4, "needle");
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = source,
            LineEnding = "\n"
        });
        var find = new FindManager();

        find.StartSearch(buffer, "needle", matchCase: false, caretLine: 0, caretCol: 0,
            selectionBounds: (500_000, 0, 500_003, "needle".Length));
        bool found = await find.FindNearestAsync(0, 0);

        Assert.True(found);
        Assert.Equal((500_000, 0, 6), find.GetCurrentMatch());
        find.Clear();
    }

    [Fact]
    public async Task MoveNextAsync_FindsDirectionallyWithoutSynchronousFullList()
    {
        var buf = TestHelpers.MakeBuffer("zero\none needle\ntwo\nthree needle");
        var find = new FindManager();

        find.StartSearch(buf, "needle", matchCase: false, caretLine: 0, caretCol: 0);
        bool found = await find.MoveNextAsync(2, 0);

        Assert.True(found);
        Assert.Equal((3, 6, 6), find.GetCurrentMatch());
        Assert.Empty(find.Matches);
        find.Clear();
    }

    [Fact]
    public async Task MoveNextAsync_WrapsToEarlierMatchOnSameLine()
    {
        var buf = TestHelpers.MakeBuffer("needle middle needle");
        var find = new FindManager();

        find.StartSearch(buf, "needle", matchCase: false, caretLine: 0, caretCol: 15);
        bool found = await find.MoveNextAsync(0, 15);

        Assert.True(found);
        Assert.Equal((0, 0, 6), find.GetCurrentMatch());
        find.Clear();
    }

    [Fact]
    public async Task MovePreviousAsync_WrapsToLaterMatchOnSameLine()
    {
        var buf = TestHelpers.MakeBuffer("needle middle needle");
        var find = new FindManager();

        find.StartSearch(buf, "needle", matchCase: false, caretLine: 0, caretCol: 0);
        bool found = await find.MovePreviousAsync(0, 0);

        Assert.True(found);
        Assert.Equal((0, 14, 6), find.GetCurrentMatch());
        find.Clear();
    }

    [Fact]
    public async Task CreateReplaceAllSnapshot_ReplacesMatchesWithoutGlobalMatchList()
    {
        var buf = TestHelpers.MakeBuffer("test one\nplain\ntest test");
        var find = new FindManager();

        find.StartSearch(buf, "test", matchCase: false, caretLine: 0, caretCol: 0);
        FindManager.ReplaceAllResult? result = await find.CreateReplaceAllSnapshotAsync("tester");

        Assert.NotNull(result);
        Assert.Equal(3, result.ReplacementCount);

        buf.ReplaceLines(result.StartLine, result.LineCount, result.Replacement);

        Assert.Equal("tester one", buf[0]);
        Assert.Equal("plain", buf[1]);
        Assert.Equal("tester tester", buf[2]);
        find.Clear();
    }

    [Fact]
    public async Task CreateReplaceAllSnapshot_RespectsSelectionBounds()
    {
        var buf = TestHelpers.MakeBuffer("test test\ntest test");
        var find = new FindManager();

        find.StartSearch(buf, "test", matchCase: false, caretLine: 0, caretCol: 0,
            selectionBounds: (0, 5, 1, 4));
        FindManager.ReplaceAllResult? result = await find.CreateReplaceAllSnapshotAsync("tester");

        Assert.NotNull(result);
        Assert.Equal(2, result.ReplacementCount);

        buf.ReplaceLines(result.StartLine, result.LineCount, result.Replacement);

        Assert.Equal("test tester", buf[0]);
        Assert.Equal("tester test", buf[1]);
        find.Clear();
    }

    [Fact]
    public async Task CreateReplaceAllSnapshot_ReportsProgress()
    {
        var buf = TestHelpers.MakeBuffer("test one\nplain\ntest test");
        var find = new FindManager();
        var progress = new RecordingProgress<FindManager.ReplaceAllProgress>();

        find.StartSearch(buf, "test", matchCase: false, caretLine: 0, caretCol: 0);
        FindManager.ReplaceAllResult? result = await find.CreateReplaceAllSnapshotAsync("tester", progress);

        Assert.NotNull(result);
        Assert.NotEmpty(progress.Values);
        FindManager.ReplaceAllProgress last = progress.Values[^1];
        Assert.True(last.IsComplete);
        Assert.Equal(3, last.TotalLines);
        Assert.Equal(3, last.ProcessedLines);
        Assert.Equal(3, last.ReplacementCount);
        Assert.Equal(100, last.Percent);
        find.Clear();
    }

    [Fact]
    public async Task AsyncNavigation_ReturnsFalseWhenSearchIsCleared()
    {
        using var release = new ManualResetEventSlim();
        var source = new BlockingTextSource(lineCount: 10_000, release);
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = source,
            LineEnding = "\n"
        });
        var find = new FindManager();

        find.StartSearch(buffer, "missing", matchCase: false, caretLine: 0, caretCol: 0);
        Task<bool> nearest = find.FindNearestAsync(0, 0);
        Task<bool> next = find.MoveNextAsync(0, 0);

        find.Clear();
        release.Set();

        Assert.False(await nearest);
        Assert.False(await next);
    }

    [Fact]
    public void StartSearch_ReturnsBeforeSlowFullScanCompletes()
    {
        using var release = new ManualResetEventSlim();
        var source = new BlockingTextSource(lineCount: 1_000, release);
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = source,
            LineEnding = "\n"
        });
        var find = new FindManager();
        var sw = Stopwatch.StartNew();

        try
        {
            find.StartSearch(buffer, "needle", matchCase: false, caretLine: 0, caretCol: 0);

            Assert.True(sw.ElapsedMilliseconds < 250);
            Assert.True(find.IsSearching);
            Assert.Contains("% searched", find.StatusText);
        }
        finally
        {
            release.Set();
            find.Clear();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class BlockingTextSource : ITextSource
    {
        private readonly ManualResetEventSlim _release;
        private readonly string _line = "needle";

        public BlockingTextSource(int lineCount, ManualResetEventSlim release)
        {
            LineCount = lineCount;
            _release = release;
        }

        public int LineCount { get; }
        public long CharCountWithoutLineEndings => LineCount * _line.Length;
        public int MaxLineLength => _line.Length;

        public string GetLine(int line) => _line;
        public int GetLineLength(int line) => _line.Length;
        public string GetLineSegment(int line, int startColumn, int length) =>
            startColumn >= _line.Length ? "" : _line.Substring(startColumn, Math.Min(length, _line.Length - startColumn));

        public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
        {
            _release.Wait();
            for (int i = 0; i < count && startLine + i < LineCount; i++)
                yield return _line;
        }

        public int GetMaxLineLength(int startLine, int count) => _line.Length;
        public long GetCharCountWithoutLineEndings(int startLine, int count) => (long)count * _line.Length;
    }

    private sealed class CountingTextSource : ITextSource
    {
        private readonly string _line;
        private int _enumeratedLineReads;

        public CountingTextSource(int lineCount, string line)
        {
            LineCount = lineCount;
            _line = line;
        }

        public int LineCount { get; }
        public int EnumeratedLineReads => Volatile.Read(ref _enumeratedLineReads);
        public long CharCountWithoutLineEndings => (long)LineCount * _line.Length;
        public int MaxLineLength => _line.Length;

        public string GetLine(int line) => _line;
        public int GetLineLength(int line) => _line.Length;
        public string GetLineSegment(int line, int startColumn, int length) =>
            startColumn >= _line.Length ? "" : _line.Substring(startColumn, Math.Min(length, _line.Length - startColumn));

        public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
        {
            for (int i = 0; i < count && startLine + i < LineCount; i++)
            {
                Interlocked.Increment(ref _enumeratedLineReads);
                yield return _line;
            }
        }

        public int GetMaxLineLength(int startLine, int count) => _line.Length;
        public long GetCharCountWithoutLineEndings(int startLine, int count) => (long)count * _line.Length;
    }

    private sealed class GuardedTextSource : ITextSource
    {
        private readonly int _allowedStartLine;
        private readonly int _allowedEndLine;
        private readonly string _line;

        public GuardedTextSource(int lineCount, int allowedStartLine, int allowedLineCount, string line)
        {
            LineCount = lineCount;
            _allowedStartLine = allowedStartLine;
            _allowedEndLine = allowedStartLine + allowedLineCount - 1;
            _line = line;
        }

        public int LineCount { get; }
        public long CharCountWithoutLineEndings => (long)LineCount * _line.Length;
        public int MaxLineLength => _line.Length;

        public string GetLine(int line)
        {
            EnsureAllowed(line, 1);
            return _line;
        }

        public int GetLineLength(int line) => _line.Length;
        public string GetLineSegment(int line, int startColumn, int length) =>
            startColumn >= _line.Length ? "" : _line.Substring(startColumn, Math.Min(length, _line.Length - startColumn));

        public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
        {
            EnsureAllowed(startLine, count);
            for (int i = 0; i < count && startLine + i < LineCount; i++)
                yield return _line;
        }

        public int GetMaxLineLength(int startLine, int count) => _line.Length;
        public long GetCharCountWithoutLineEndings(int startLine, int count) => (long)count * _line.Length;

        private void EnsureAllowed(int startLine, int count)
        {
            int endLine = startLine + Math.Max(0, count) - 1;
            if (startLine < _allowedStartLine || endLine > _allowedEndLine)
                throw new InvalidOperationException($"Unexpected read outside selected range: {startLine}-{endLine}.");
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
