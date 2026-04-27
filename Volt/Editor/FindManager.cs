using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Volt;

public readonly record struct FindMatch(int Line, int Col, int Length);

public readonly record struct FindSelectionRange(int StartLine, int StartCol, int EndLine, int EndCol);

public sealed record FindQuery(
    string Text,
    bool MatchCase = false,
    bool UseRegex = false,
    bool WholeWord = false,
    FindSelectionRange? Selection = null);

public sealed class FindSnapshot
{
    public static FindSnapshot Empty { get; } = new(
        generation: 0,
        query: new FindQuery(""),
        retainedMatches: [],
        totalMatches: 0,
        currentRetainedIndex: -1,
        currentOrdinal: null,
        currentMatch: null,
        progress: 1,
        isSearching: false,
        isComplete: true,
        invalidRegex: false,
        retentionLimitExceeded: false);

    public FindSnapshot(
        int generation,
        FindQuery query,
        IReadOnlyList<FindMatch> retainedMatches,
        long totalMatches,
        int currentRetainedIndex,
        long? currentOrdinal,
        FindMatch? currentMatch,
        double progress,
        bool isSearching,
        bool isComplete,
        bool invalidRegex,
        bool retentionLimitExceeded)
    {
        Generation = generation;
        Query = query;
        RetainedMatches = retainedMatches;
        TotalMatches = totalMatches;
        CurrentRetainedIndex = currentRetainedIndex;
        CurrentOrdinal = currentOrdinal;
        CurrentMatch = currentMatch;
        Progress = Math.Clamp(progress, 0, 1);
        IsSearching = isSearching;
        IsComplete = isComplete;
        InvalidRegex = invalidRegex;
        RetentionLimitExceeded = retentionLimitExceeded;
    }

    public int Generation { get; }
    public FindQuery Query { get; }
    public IReadOnlyList<FindMatch> RetainedMatches { get; }
    public long TotalMatches { get; }
    public int RetainedMatchCount => RetainedMatches.Count;
    public int CurrentRetainedIndex { get; }
    public long? CurrentOrdinal { get; }
    public FindMatch? CurrentMatch { get; }
    public double Progress { get; }
    public bool IsSearching { get; }
    public bool IsComplete { get; }
    public bool InvalidRegex { get; }
    public bool RetentionLimitExceeded { get; }
    public bool HasQuery => !string.IsNullOrEmpty(Query.Text);
    public bool CanReplaceAll => IsComplete && !RetentionLimitExceeded && TotalMatches > 0;
}

/// <summary>
/// Progressive find controller. Scans are line-based and run in caller-scheduled
/// batches so large documents do not block editor input/rendering.
/// </summary>
public class FindManager
{
    private const double PublishIntervalMilliseconds = 100;
    private readonly int _retainedMatchLimit;
    private readonly int _batchLineBudget;
    private readonly List<FindMatch> _matchesBeforeCaret = [];
    private readonly List<FindMatch> _matchesFromCaret = [];
    private readonly List<FindMatch> _snapshotMatches = [];
    private Action<Action>? _schedule;
    private ITextDocument? _buffer;
    private Regex? _regex;
    private StringComparison _literalComparison;
    private FindQuery _query = new("");
    private FindSnapshot _snapshot = FindSnapshot.Empty;
    private FindMatch? _currentMatch;
    private int _generation;
    private bool _batchQueued;
    private bool _isSearching;
    private bool _invalidRegex;
    private bool _retentionLimitExceeded;
    private int _scanStartLine;
    private int _scanEndLine;
    private int _caretLine;
    private int _caretCol;
    private int _scanLine;
    private int _scanPhase;
    private int _totalLinesToScan;
    private int _scannedLines;
    private long _totalMatches;
    private long _lastPublishTimestamp;

    public FindManager(int retainedMatchLimit = 100_000, int batchLineBudget = 4096)
    {
        _retainedMatchLimit = Math.Max(1, retainedMatchLimit);
        _batchLineBudget = Math.Max(1, batchLineBudget);
    }

    public event EventHandler<FindSnapshot>? Changed;

    public FindSnapshot Snapshot => _snapshot;
    public int MatchCount => _snapshot.TotalMatches > int.MaxValue ? int.MaxValue : (int)_snapshot.TotalMatches;
    public int CurrentIndex => _snapshot.CurrentRetainedIndex;
    public IReadOnlyList<FindMatch> RetainedMatches => _snapshot.RetainedMatches;
    public string LastQuery => _query.Text;
    public bool LastMatchCase => _query.MatchCase;
    public bool LastUseRegex => _query.UseRegex;
    public bool LastWholeWord => _query.WholeWord;
    public FindSelectionRange? LastSelection => _query.Selection;

    public int StartSearch(
        ITextDocument buffer,
        FindQuery query,
        int caretLine,
        int caretCol,
        Action<Action>? schedule = null)
    {
        CancelPendingBatch();
        _generation++;
        _buffer = buffer;
        _query = query;
        _schedule = schedule;
        _regex = null;
        _invalidRegex = false;
        _retentionLimitExceeded = false;
        _matchesBeforeCaret.Clear();
        _matchesFromCaret.Clear();
        _snapshotMatches.Clear();
        _currentMatch = null;
        _totalMatches = 0;
        _scannedLines = 0;
        _scanPhase = 0;
        _lastPublishTimestamp = 0;

        if (string.IsNullOrEmpty(query.Text) || buffer.Count == 0)
        {
            _isSearching = false;
            PublishSnapshot();
            return _generation;
        }

        if (!PrepareMatcher(query))
        {
            _invalidRegex = true;
            _isSearching = false;
            PublishSnapshot();
            return _generation;
        }

        (_scanStartLine, _scanEndLine) = GetScanBounds(buffer, query);
        if (_scanStartLine > _scanEndLine)
        {
            _isSearching = false;
            PublishSnapshot();
            return _generation;
        }

        _caretLine = Math.Clamp(caretLine, _scanStartLine, _scanEndLine);
        _caretCol = Math.Max(0, caretCol);
        _scanLine = _caretLine;
        _totalLinesToScan = _scanEndLine - _scanStartLine + 1;
        _isSearching = true;
        PublishSnapshot();
        ScheduleNextBatch(_generation);
        return _generation;
    }

    public void CancelSearch()
    {
        CancelPendingBatch();
        _generation++;
        _isSearching = false;
        PublishSnapshot();
    }

    public void Clear(bool trimExcess = false)
    {
        CancelPendingBatch();
        _generation++;
        _buffer = null;
        _regex = null;
        _query = new FindQuery("");
        _currentMatch = null;
        _matchesBeforeCaret.Clear();
        _matchesFromCaret.Clear();
        _snapshotMatches.Clear();
        if (trimExcess)
        {
            _matchesBeforeCaret.TrimExcess();
            _matchesFromCaret.TrimExcess();
            _snapshotMatches.TrimExcess();
        }
        _isSearching = false;
        _invalidRegex = false;
        _retentionLimitExceeded = false;
        _totalMatches = 0;
        _scannedLines = 0;
        _totalLinesToScan = 0;
        PublishSnapshot();
    }

    public bool RunNextBatch()
    {
        if (!_isSearching || _buffer == null)
            return false;

        bool hadCurrentMatch = _currentMatch != null;
        int processed = 0;
        while (_isSearching && processed < _batchLineBudget)
        {
            if (!TryGetNextLine(out int line))
            {
                _isSearching = false;
                break;
            }

            ScanLine(line);
            _scannedLines++;
            processed++;
        }

        bool foundFirstMatch = !hadCurrentMatch && _currentMatch != null;
        PublishSnapshotIfNeeded(foundFirstMatch || !_isSearching || _invalidRegex);

        if (_isSearching)
            ScheduleNextBatch(_generation);
        return _isSearching;
    }

    public Task MoveNextAsync()
    {
        EnsureSomeRetainedMatch();
        MoveCurrent(1);
        return Task.CompletedTask;
    }

    public Task MovePreviousAsync()
    {
        EnsureSomeRetainedMatch();
        MoveCurrent(-1);
        return Task.CompletedTask;
    }

    public FindMatch? GetCurrentMatch() => _snapshot.CurrentMatch;

    public IReadOnlyList<FindMatch> GetMatchesInRange(int firstLine, int lastLine)
    {
        if (_snapshot.RetainedMatches.Count == 0 || firstLine > lastLine)
        {
            return _retentionLimitExceeded
                ? GetOnDemandMatchesInRange(firstLine, lastLine)
                : [];
        }

        var result = new List<FindMatch>();
        int index = LowerBoundLine(_snapshot.RetainedMatches, firstLine);
        for (int i = index; i < _snapshot.RetainedMatches.Count; i++)
        {
            var match = _snapshot.RetainedMatches[i];
            if (match.Line > lastLine) break;
            result.Add(match);
        }

        if (_retentionLimitExceeded)
            AddOnDemandMatchesInRange(firstLine, lastLine, result);
        return result;
    }

    private IReadOnlyList<FindMatch> GetOnDemandMatchesInRange(int firstLine, int lastLine)
    {
        var matches = new List<FindMatch>();
        AddOnDemandMatchesInRange(firstLine, lastLine, matches);
        return matches;
    }

    private void AddOnDemandMatchesInRange(int firstLine, int lastLine, List<FindMatch> matches)
    {
        if (_buffer == null || string.IsNullOrEmpty(_query.Text) || _invalidRegex)
            return;

        int start = Math.Max(firstLine, _scanStartLine);
        int end = Math.Min(lastLine, _scanEndLine);
        if (start > end)
            return;

        var existing = matches.Count == 0 ? null : matches.ToHashSet();
        for (int line = start; line <= end; line++)
            AppendLineMatches(line, _buffer[line], matches, existing);

        matches.Sort(CompareMatches);
    }

    private static int LowerBoundLine(IReadOnlyList<FindMatch> matches, int line)
    {
        int lo = 0;
        int hi = matches.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (matches[mid].Line < line)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    public (int FirstLine, int LastLine)? GetMatchLineRange()
    {
        if (!_snapshot.CanReplaceAll || _snapshot.RetainedMatches.Count == 0)
            return null;
        return (_snapshot.RetainedMatches[0].Line, _snapshot.RetainedMatches[^1].Line);
    }

    private bool PrepareMatcher(FindQuery query)
    {
        _literalComparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!query.UseRegex && !query.WholeWord)
            return true;

        try
        {
            var pattern = query.UseRegex ? query.Text : Regex.Escape(query.Text);
            if (query.WholeWord)
                pattern = @"\b" + pattern + @"\b";
            var options = query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            _regex = new Regex(pattern, options, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static (int startLine, int endLine) GetScanBounds(ITextDocument buffer, FindQuery query)
    {
        if (query.Selection is not { } selection)
            return (0, buffer.Count - 1);

        int start = Math.Clamp(selection.StartLine, 0, Math.Max(0, buffer.Count - 1));
        int end = Math.Clamp(selection.EndLine, 0, Math.Max(0, buffer.Count - 1));
        return start <= end ? (start, end) : (end, start);
    }

    private bool TryGetNextLine(out int line)
    {
        if (_scanPhase == 0)
        {
            if (_scanLine <= _scanEndLine)
            {
                line = _scanLine++;
                return true;
            }

            _scanPhase = 1;
            _scanLine = _scanStartLine;
        }

        if (_scanPhase == 1)
        {
            if (_scanLine < _caretLine)
            {
                line = _scanLine++;
                return true;
            }

            _scanPhase = 2;
        }

        line = -1;
        return false;
    }

    private void ScanLine(int lineIndex)
    {
        if (_buffer == null) return;
        string line = _buffer[lineIndex];
        if (_regex != null)
            ScanRegexLine(lineIndex, line, AddMatch);
        else
            ScanLiteralLine(lineIndex, line, AddMatch);
    }

    private void AppendLineMatches(
        int lineIndex,
        string line,
        List<FindMatch> matches,
        HashSet<FindMatch>? existing)
    {
        void AddDisplayMatch(FindMatch match)
        {
            if (existing != null && existing.Contains(match))
                return;
            matches.Add(match);
        }

        if (_regex != null)
            ScanRegexLine(lineIndex, line, AddDisplayMatch);
        else
            ScanLiteralLine(lineIndex, line, AddDisplayMatch);
    }

    private void ScanLiteralLine(int lineIndex, string line, Action<FindMatch> add)
    {
        int pos = 0;
        while (pos < line.Length)
        {
            int idx = line.IndexOf(_query.Text, pos, _literalComparison);
            if (idx < 0) break;
            var match = new FindMatch(lineIndex, idx, _query.Text.Length);
            if (IsWithinSelection(match))
                add(match);
            pos = idx + 1;
        }
    }

    private void ScanRegexLine(int lineIndex, string line, Action<FindMatch> add)
    {
        if (_regex == null) return;
        try
        {
            var match = _regex.Match(line);
            while (match.Success)
            {
                if (match.Length > 0)
                {
                    var findMatch = new FindMatch(lineIndex, match.Index, match.Length);
                    if (IsWithinSelection(findMatch))
                        add(findMatch);
                }
                match = match.NextMatch();
            }
        }
        catch (RegexMatchTimeoutException)
        {
            _invalidRegex = true;
            _isSearching = false;
        }
    }

    private void AddMatch(FindMatch match)
    {
        _totalMatches++;
        if (_matchesBeforeCaret.Count + _matchesFromCaret.Count >= _retainedMatchLimit)
        {
            _retentionLimitExceeded = true;
            return;
        }

        if (match.Line < _caretLine)
            _matchesBeforeCaret.Add(match);
        else
            _matchesFromCaret.Add(match);

        if (_currentMatch == null && IsAtOrAfterCaret(match))
            _currentMatch = match;
    }

    private bool IsWithinSelection(FindMatch match)
    {
        if (_query.Selection is not { } selection)
            return true;

        int matchEnd = match.Col + match.Length;
        if (match.Line < selection.StartLine || (match.Line == selection.StartLine && match.Col < selection.StartCol))
            return false;
        if (match.Line > selection.EndLine || (match.Line == selection.EndLine && matchEnd > selection.EndCol))
            return false;
        return true;
    }

    private bool IsAtOrAfterCaret(FindMatch match) =>
        match.Line > _caretLine || (match.Line == _caretLine && match.Col >= _caretCol);

    private void EnsureSomeRetainedMatch()
    {
        for (int i = 0; i < 4 && _currentMatch == null && _isSearching; i++)
            RunNextBatch();
    }

    private void MoveCurrent(int delta)
    {
        var matches = _snapshot.RetainedMatches;
        if (matches.Count == 0)
            return;

        int index = _currentMatch is { } current ? IndexOf(matches, current) : -1;
        if (index < 0)
            index = FindNearestRetainedIndex(matches);

        var anchor = _currentMatch
            ?? (index >= 0 ? matches[index] : matches[delta >= 0 ? 0 : matches.Count - 1]);
        if (_retentionLimitExceeded && TryFindDirectionalMatch(anchor, delta, out var directionalMatch))
        {
            _currentMatch = directionalMatch;
            PublishSnapshot();
            return;
        }

        if (index < 0)
            index = delta >= 0 ? 0 : matches.Count - 1;
        else
            index = (index + delta + matches.Count) % matches.Count;

        _currentMatch = matches[index];
        PublishSnapshot();
    }

    private bool TryFindDirectionalMatch(FindMatch anchor, int delta, out FindMatch match)
    {
        match = default;
        if (_buffer == null || string.IsNullOrEmpty(_query.Text) || _invalidRegex)
            return false;

        return delta >= 0
            ? TryFindNextMatch(anchor, out match)
            : TryFindPreviousMatch(anchor, out match);
    }

    private bool TryFindNextMatch(FindMatch anchor, out FindMatch match)
    {
        if (TryFindInLine(anchor.Line, m => CompareMatches(m, anchor) > 0, first: true, out match))
            return true;

        if (TryFindForward(anchor.Line + 1, _scanEndLine, out match))
            return true;
        if (TryFindForward(_scanStartLine, anchor.Line - 1, out match))
            return true;
        return TryFindInLine(anchor.Line, m => CompareMatches(m, anchor) <= 0, first: true, out match);
    }

    private bool TryFindPreviousMatch(FindMatch anchor, out FindMatch match)
    {
        if (TryFindInLine(anchor.Line, m => CompareMatches(m, anchor) < 0, first: false, out match))
            return true;

        if (TryFindBackward(anchor.Line - 1, _scanStartLine, out match))
            return true;
        if (TryFindBackward(_scanEndLine, anchor.Line + 1, out match))
            return true;
        return TryFindInLine(anchor.Line, m => CompareMatches(m, anchor) >= 0, first: false, out match);
    }

    private bool TryFindForward(int startLine, int endLine, out FindMatch match)
    {
        match = default;
        if (_buffer == null || startLine > endLine)
            return false;

        startLine = Math.Max(startLine, _scanStartLine);
        endLine = Math.Min(endLine, _scanEndLine);
        for (int line = startLine; line <= endLine; line++)
        {
            if (TryFindInLine(line, _ => true, first: true, out match))
                return true;
        }
        return false;
    }

    private bool TryFindBackward(int startLine, int endLine, out FindMatch match)
    {
        match = default;
        if (_buffer == null || startLine < endLine)
            return false;

        startLine = Math.Min(startLine, _scanEndLine);
        endLine = Math.Max(endLine, _scanStartLine);
        for (int line = startLine; line >= endLine; line--)
        {
            if (TryFindInLine(line, _ => true, first: false, out match))
                return true;
        }
        return false;
    }

    private bool TryFindInLine(int line, Func<FindMatch, bool> predicate, bool first, out FindMatch match)
    {
        match = default;
        if (_buffer == null || line < _scanStartLine || line > _scanEndLine)
            return false;

        var lineMatches = new List<FindMatch>();
        AppendLineMatches(line, _buffer[line], lineMatches, existing: null);
        if (first)
        {
            foreach (var candidate in lineMatches)
            {
                if (predicate(candidate))
                {
                    match = candidate;
                    return true;
                }
            }
            return false;
        }

        for (int i = lineMatches.Count - 1; i >= 0; i--)
        {
            if (predicate(lineMatches[i]))
            {
                match = lineMatches[i];
                return true;
            }
        }
        return false;
    }

    private int FindNearestRetainedIndex(IReadOnlyList<FindMatch> matches)
    {
        for (int i = 0; i < matches.Count; i++)
        {
            if (IsAtOrAfterCaret(matches[i]))
                return i;
        }
        return matches.Count > 0 ? 0 : -1;
    }

    private static int IndexOf(IReadOnlyList<FindMatch> matches, FindMatch current)
    {
        for (int i = 0; i < matches.Count; i++)
            if (matches[i].Equals(current))
                return i;
        return -1;
    }

    private void PublishSnapshotIfNeeded(bool force = false)
    {
        if (_schedule == null || force || ShouldPublishProgress())
            PublishSnapshot();
    }

    private bool ShouldPublishProgress()
    {
        if (_lastPublishTimestamp == 0)
            return true;

        long elapsedTicks = Stopwatch.GetTimestamp() - _lastPublishTimestamp;
        double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        return elapsedMs >= PublishIntervalMilliseconds;
    }

    private void PublishSnapshot()
    {
        _snapshotMatches.Clear();
        _snapshotMatches.AddRange(_matchesBeforeCaret);
        _snapshotMatches.AddRange(_matchesFromCaret);
        if (_currentMatch == null && !_isSearching && _snapshotMatches.Count > 0)
            _currentMatch = _snapshotMatches[0];

        int currentIndex = _currentMatch is { } current
            ? IndexOf(_snapshotMatches, current)
            : -1;
        long? currentOrdinal = !_isSearching && currentIndex >= 0
            ? currentIndex + 1
            : null;

        double progress = _totalLinesToScan <= 0
            ? 1
            : (double)_scannedLines / _totalLinesToScan;

        var snapshotCurrentMatch = currentIndex >= 0
            ? _snapshotMatches[currentIndex]
            : _currentMatch;

        _snapshot = new FindSnapshot(
            _generation,
            _query,
            _snapshotMatches.ToArray(),
            _totalMatches,
            currentIndex,
            currentOrdinal,
            snapshotCurrentMatch,
            progress,
            _isSearching,
            !_isSearching,
            _invalidRegex,
            _retentionLimitExceeded);
        _lastPublishTimestamp = Stopwatch.GetTimestamp();
        Changed?.Invoke(this, _snapshot);
    }

    private static int CompareMatches(FindMatch left, FindMatch right)
    {
        int line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Col.CompareTo(right.Col);
    }

    private void ScheduleNextBatch(int generation)
    {
        if (!_isSearching || _schedule == null || _batchQueued)
            return;

        _batchQueued = true;
        _schedule(() =>
        {
            if (generation != _generation)
                return;
            _batchQueued = false;
            RunNextBatch();
        });
    }

    private void CancelPendingBatch()
    {
        _batchQueued = false;
        _isSearching = false;
    }
}
