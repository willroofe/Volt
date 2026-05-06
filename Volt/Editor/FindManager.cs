using System.Text.RegularExpressions;
using System.Text;

namespace Volt;

/// <summary>
/// Manages find/replace match state. Searches run against immutable line snapshots
/// so large documents can be scanned without blocking the editor thread.
/// </summary>
public class FindManager
{
    private const int PageLineCount = 512;
    private const int CachedPageLimit = 96;
    private const int ReplaceAllChunkLineCount = 65_536;
    private const int ImmediateFindProgressThresholdLines = 8_192;
    private const int DeferredFindProgressDelayMilliseconds = 160;
    private const long ImmediateFindProgressThresholdChars = 8L * 1024 * 1024;
    private const int SelectionProgressLengthProbeLimit = 512;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly List<(int Line, int Col, int Length)> _matches = [];
    private FindSession? _session;
    private int _currentIndex = -1;
    private int _nextSessionId;
    private SynchronizationContext? _syncContext;

    public event EventHandler? Changed;

    public int MatchCount
    {
        get
        {
            lock (_gate)
                return _session == null
                    ? _matches.Count
                    : ClampCount(_session.KnownMatchCount);
        }
    }

    public long KnownMatchCount
    {
        get
        {
            lock (_gate)
                return _session?.KnownMatchCount ?? _matches.Count;
        }
    }

    public int CurrentIndex
    {
        get
        {
            lock (_gate)
            {
                if (_session == null)
                    return _currentIndex;
                return _session.CurrentOrdinal is { } ordinal && ordinal > 0
                    ? ClampCount(ordinal) - 1
                    : (_session.CurrentMatch == null ? -1 : 0);
            }
        }
    }

    public bool IsSearching
    {
        get
        {
            lock (_gate)
                return _session?.IsSearching ?? false;
        }
    }

    public bool HasExactMatchCount
    {
        get
        {
            lock (_gate)
                return _session?.IsComplete ?? true;
        }
    }

    public bool HasQuery
    {
        get
        {
            lock (_gate)
                return _session?.Query.Text.Length > 0 || LastQuery.Length > 0;
        }
    }

    public IReadOnlyList<(int Line, int Col, int Length)> Matches => _matches;
    public string LastQuery { get; private set; } = "";
    public bool LastMatchCase { get; private set; }
    public bool LastUseRegex { get; private set; }
    public bool LastWholeWord { get; private set; }
    public (int startLine, int startCol, int endLine, int endCol)? LastSelectionBounds { get; private set; }

    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                if (_session == null)
                {
                    if (LastQuery.Length == 0)
                        return "";
                    return _matches.Count == 0
                        ? "No results"
                        : $"{_currentIndex + 1} of {_matches.Count}";
                }

                if (_session.InvalidPattern)
                    return "Invalid regex";

                if (_session.CurrentMatch != null)
                {
                    string ordinal = _session.CurrentOrdinal is { } value && value > 0
                        ? value.ToString("N0")
                        : "...";
                    string total = _session.IsComplete
                        ? _session.KnownMatchCount.ToString("N0")
                        : _session.KnownMatchCount > 0
                            ? $"{_session.KnownMatchCount:N0}+"
                            : "...";
                    string progress = _session.IsComplete || !_session.ProgressTextVisible
                        ? ""
                        : $" ({_session.ProgressPercent:0.0}% searched)";
                    return $"{ordinal} of {total}{progress}";
                }

                if (_session.IsSearching)
                {
                    if (!_session.ProgressTextVisible)
                        return "";

                    string progress = _session.ProgressTextVisible
                        ? $" ({_session.ProgressPercent:0.0}% searched)"
                        : "";
                    return _session.KnownMatchCount > 0
                        ? $"Searching... {_session.KnownMatchCount:N0}+{progress}"
                        : $"Searching...{progress}";
                }

                return _session.KnownMatchCount > 0 ? "" : "No results";
            }
        }
    }

    public sealed record ReplaceAllResult(
        int SessionId,
        int StartLine,
        int LineCount,
        TextBuffer.LineSnapshot Replacement,
        long ReplacementCount);

    public sealed record ReplaceAllProgress(
        int ProcessedLines,
        int TotalLines,
        long ReplacementCount,
        bool IsComplete)
    {
        public double Percent => TotalLines <= 0
            ? 100
            : Math.Clamp(ProcessedLines * 100.0 / TotalLines, 0, 100);
    }

    public void StartSearch(TextBuffer buffer, string query, bool matchCase, int caretLine, int caretCol,
        bool useRegex = false, bool wholeWord = false,
        (int startLine, int startCol, int endLine, int endCol)? selectionBounds = null)
    {
        _syncContext = SynchronizationContext.Current;
        LastQuery = query;
        LastMatchCase = matchCase;
        LastUseRegex = useRegex;
        LastWholeWord = wholeWord;
        LastSelectionBounds = selectionBounds;

        FindSession? previous;
        lock (_gate)
        {
            previous = _session;
            _session = null;
            _matches.Clear();
            _currentIndex = -1;
        }
        previous?.Cancel();

        if (string.IsNullOrEmpty(query))
        {
            RaiseChanged();
            return;
        }

        var normalizedSelection = NormalizeSelection(selectionBounds, buffer.Count);
        var findQuery = new FindQuery(query, matchCase, useRegex, wholeWord, normalizedSelection, Regex: null);
        if (!TryCreateMatcher(findQuery, out Regex? regex, out _))
        {
            var invalidSession = new FindSession(
                Interlocked.Increment(ref _nextSessionId),
                buffer.SnapshotLines(0, buffer.Count),
                findQuery,
                showProgressImmediately: false);
            invalidSession.InvalidPattern = true;
            invalidSession.IsSearching = false;
            invalidSession.IsComplete = true;
            lock (_gate)
                _session = invalidSession;
            RaiseChanged();
            return;
        }
        findQuery = findQuery with { Regex = regex };

        TextBuffer.LineSnapshot snapshot = buffer.SnapshotLines(0, buffer.Count);
        var session = new FindSession(
            Interlocked.Increment(ref _nextSessionId),
            snapshot,
            findQuery,
            ShouldShowFindProgressImmediately(buffer, normalizedSelection));
        lock (_gate)
            _session = session;

        RaiseChanged();
        if (!session.ProgressTextVisible)
            _ = ShowDeferredFindProgressTextAsync(session);

        _ = Task.Run(() => CountMatchesAsync(session));
    }

    /// <summary>
    /// Compatibility synchronous search used by existing unit tests and small-file callers.
    /// EditorControl uses <see cref="StartSearch"/> instead.
    /// </summary>
    public void Search(TextBuffer buffer, string query, bool matchCase, int caretLine, int caretCol,
        bool useRegex = false, bool wholeWord = false,
        (int startLine, int startCol, int endLine, int endCol)? selectionBounds = null)
    {
        LastQuery = query;
        LastMatchCase = matchCase;
        LastUseRegex = useRegex;
        LastWholeWord = wholeWord;
        LastSelectionBounds = selectionBounds;

        FindSession? previous;
        lock (_gate)
        {
            previous = _session;
            _session = null;
            _matches.Clear();
            _currentIndex = -1;
        }
        previous?.Cancel();

        if (string.IsNullOrEmpty(query)) return;

        var normalizedSelection = NormalizeSelection(selectionBounds, buffer.Count);
        var findQuery = new FindQuery(query, matchCase, useRegex, wholeWord, normalizedSelection, Regex: null);
        if (TryCreateMatcher(findQuery, out Regex? regex, out _))
            findQuery = findQuery with { Regex = regex };
        TextBuffer.LineSnapshot snapshot = buffer.SnapshotLines(0, buffer.Count);
        var (searchStartLine, searchLineCount) = GetSearchLineRange(snapshot.Count, findQuery);
        foreach (var match in ScanSnapshotRange(snapshot, findQuery, searchStartLine, searchLineCount, CancellationToken.None))
            _matches.Add(match);

        if (_matches.Count > 0)
            _currentIndex = FindInsertionIndex(_matches, caretLine, caretCol);
    }

    public async Task<bool> FindNearestAsync(int caretLine, int caretCol)
    {
        FindSession? session = CurrentSession();
        if (session == null)
            return false;

        (int Line, int Col, int Length)? match;
        try
        {
            match = await Task.Run(() => FindForward(session, caretLine, caretCol, includeStart: true)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (match == null)
            return false;

        return SetCurrentMatch(session, match.Value);
    }

    public async Task<bool> MoveNextAsync(int caretLine, int caretCol)
    {
        FindSession? session = CurrentSession();
        if (session == null)
            return false;

        (int Line, int Col, int Length)? current;
        long? currentOrdinal;
        long knownMatchCount;
        lock (session.Gate)
        {
            current = session.CurrentMatch;
            currentOrdinal = session.CurrentOrdinal;
            knownMatchCount = session.KnownMatchCount;
        }

        int startLine = current?.Line ?? caretLine;
        int startCol = current == null ? caretCol : current.Value.Col + 1;
        (int Line, int Col, int Length)? match;
        try
        {
            match = await Task.Run(() => FindForward(session, startLine, startCol, includeStart: true)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (match == null)
            return false;

        long? ordinalHint = GetNextOrdinalHint(current, currentOrdinal, knownMatchCount, match.Value);
        return SetCurrentMatch(session, match.Value, ordinalHint);
    }

    public async Task<bool> MovePreviousAsync(int caretLine, int caretCol)
    {
        FindSession? session = CurrentSession();
        if (session == null)
            return false;

        (int Line, int Col, int Length)? current;
        long? currentOrdinal;
        long knownMatchCount;
        lock (session.Gate)
        {
            current = session.CurrentMatch;
            currentOrdinal = session.CurrentOrdinal;
            knownMatchCount = session.KnownMatchCount;
        }

        int startLine = current?.Line ?? caretLine;
        int startCol = current == null ? caretCol : current.Value.Col - 1;
        (int Line, int Col, int Length)? match;
        try
        {
            match = await Task.Run(() => FindBackward(session, startLine, startCol)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (match == null)
            return false;

        long? ordinalHint = GetPreviousOrdinalHint(current, currentOrdinal, knownMatchCount, match.Value);
        return SetCurrentMatch(session, match.Value, ordinalHint);
    }

    public IReadOnlyList<(int Line, int Col, int Length)> GetMatchesInRange(
        int firstLine,
        int lastLine,
        int firstColumn = 0,
        int lastColumn = int.MaxValue)
    {
        FindSession? session = CurrentSession();
        if (session == null)
        {
            if (_matches.Count == 0)
                return Array.Empty<(int Line, int Col, int Length)>();

            int start = FindFirstLineIndex(_matches, firstLine);
            var visible = new List<(int Line, int Col, int Length)>();
            for (int i = start; i < _matches.Count; i++)
            {
                var match = _matches[i];
                if (match.Line > lastLine) break;
                if (IntersectsColumns(match, firstColumn, lastColumn))
                    visible.Add(match);
            }
            return visible;
        }

        if (session.InvalidPattern || firstLine > lastLine)
            return Array.Empty<(int Line, int Col, int Length)>();

        if (session.SearchLineCount <= 0 || lastLine < session.SearchStartLine || firstLine > session.SearchEndLine)
            return Array.Empty<(int Line, int Col, int Length)>();

        firstLine = Math.Clamp(firstLine, 0, Math.Max(0, session.Snapshot.Count - 1));
        lastLine = Math.Clamp(lastLine, firstLine, Math.Max(0, session.Snapshot.Count - 1));
        firstLine = Math.Max(firstLine, session.SearchStartLine);
        lastLine = Math.Min(lastLine, session.SearchEndLine);
        if (firstLine > lastLine)
            return Array.Empty<(int Line, int Col, int Length)>();

        var result = new List<(int Line, int Col, int Length)>();

        int firstPage = firstLine / PageLineCount;
        int lastPage = lastLine / PageLineCount;
        bool fullColumns = firstColumn <= 0 && lastColumn == int.MaxValue;
        for (int page = firstPage; page <= lastPage; page++)
        {
            IReadOnlyList<(int Line, int Col, int Length)> pageMatches;
            try
            {
                pageMatches = fullColumns
                    ? GetOrScanPage(session, page)
                    : ScanPage(session, page, session.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return result;
            }

            foreach (var match in pageMatches)
            {
                if (match.Line < firstLine || match.Line > lastLine)
                    continue;
                if (IntersectsColumns(match, firstColumn, lastColumn))
                    result.Add(match);
            }
        }

        return result;
    }

    public void Clear(bool trimExcess = false)
    {
        FindSession? previous;
        lock (_gate)
        {
            previous = _session;
            _session = null;
            _matches.Clear();
            if (trimExcess) _matches.TrimExcess();
            _currentIndex = -1;
            LastQuery = "";
            LastSelectionBounds = null;
        }
        previous?.Cancel();
        RaiseChanged();
    }

    public void InvalidateForEdit()
    {
        FindSession? previous;
        lock (_gate)
        {
            previous = _session;
            _session = null;
            _matches.Clear();
            _currentIndex = -1;
        }
        previous?.Cancel();
        RaiseChanged();
    }

    public void MoveNext()
    {
        if (_matches.Count == 0) return;
        _currentIndex = (_currentIndex + 1) % _matches.Count;
    }

    public void MovePrevious()
    {
        if (_matches.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _matches.Count) % _matches.Count;
    }

    public (int Line, int Col, int Length)? GetCurrentMatch()
    {
        lock (_gate)
        {
            if (_session != null)
                return _session.CurrentMatch;

            if (_currentIndex < 0 || _currentIndex >= _matches.Count) return null;
            return _matches[_currentIndex];
        }
    }

    public (int FirstLine, int LastLine)? GetMatchLineRange()
    {
        lock (_gate)
        {
            if (_session == null)
            {
                if (_matches.Count == 0) return null;
                return (_matches[0].Line, _matches[^1].Line);
            }

            if (_session.InvalidPattern)
                return null;

            return _session.SearchLineCount <= 0
                ? null
                : (_session.SearchStartLine, _session.SearchEndLine);
        }
    }

    public IEnumerable<(int Line, int Col, int Length)> EnumerateMatchesDescending()
    {
        FindSession? session;
        List<(int Line, int Col, int Length)>? syncMatches = null;
        lock (_gate)
        {
            session = _session;
            if (session == null)
                syncMatches = [.. _matches];
        }

        if (syncMatches != null)
        {
            for (int i = syncMatches.Count - 1; i >= 0; i--)
                yield return syncMatches[i];
            yield break;
        }

        if (session == null || session.InvalidPattern)
            yield break;

        if (session.SearchLineCount <= 0)
            yield break;

        int firstPage = session.SearchStartLine / PageLineCount;
        int lastPage = session.SearchEndLine / PageLineCount;
        for (int page = lastPage; page >= firstPage; page--)
        {
            List<(int Line, int Col, int Length)> matches = ScanPage(session, page, session.Cancellation.Token);
            for (int i = matches.Count - 1; i >= 0; i--)
                yield return matches[i];
        }
    }

    public async Task<ReplaceAllResult?> CreateReplaceAllSnapshotAsync(
        string replacement,
        IProgress<ReplaceAllProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        FindSession? session = CurrentSession();
        if (session == null || session.InvalidPattern || session.Query.Text.Length == 0)
            return null;

        return await Task.Run(
            () => CreateReplaceAllSnapshot(session, replacement, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public bool IsCurrentSession(int sessionId)
    {
        lock (_gate)
            return _session?.Id == sessionId && !_session.Cancellation.IsCancellationRequested;
    }

    private async Task ShowDeferredFindProgressTextAsync(FindSession session)
    {
        try
        {
            await Task.Delay(DeferredFindProgressDelayMilliseconds, session.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        bool changed = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session) || session.Cancellation.IsCancellationRequested)
                return;

            lock (session.Gate)
            {
                if (!session.ProgressTextVisible && session.IsSearching && !session.IsComplete)
                {
                    session.ProgressTextVisible = true;
                    changed = true;
                }
            }
        }

        if (changed)
            RaiseChanged();
    }

    private async Task CountMatchesAsync(FindSession session)
    {
        try
        {
            if (!TryCreateMatcher(session.Query, out _, out _))
            {
                lock (session.Gate)
                {
                    session.InvalidPattern = true;
                    session.IsSearching = false;
                    session.IsComplete = true;
                }
                RaiseChanged();
                return;
            }

            int currentPage = -1;
            long currentPageMatches = 0;
            long totalMatches = 0;
            long lastRaisedTicks = 0;
            int processedLines = 0;

            if (session.SearchLineCount <= 0)
            {
                lock (session.Gate)
                {
                    session.IsSearching = false;
                    session.IsComplete = true;
                    session.SearchedLineCount = 0;
                    UpdateCurrentOrdinalNoLock(session);
                }
                RaiseChanged();
                return;
            }

            if (TryCountFastLiteralMatches(session, progress =>
                {
                    long nowTicks = Environment.TickCount64;
                    if (progress.SearchedLineCount < session.SearchLineCount && nowTicks - lastRaisedTicks < 80)
                        return;

                    lastRaisedTicks = nowTicks;
                    lock (session.Gate)
                    {
                        session.KnownMatchCount = progress.MatchCount;
                        session.SearchedLineCount = Math.Clamp(progress.SearchedLineCount, 0, session.SearchLineCount);
                        UpdateCurrentOrdinalNoLock(session);
                    }
                    RaiseChanged();
                },
                out long fastCount))
            {
                lock (session.Gate)
                {
                    session.KnownMatchCount = fastCount;
                    session.SearchedLineCount = session.SearchLineCount;
                    session.IsSearching = false;
                    session.IsComplete = true;
                    UpdateCurrentOrdinalNoLock(session);
                }
                RaiseChanged();
                return;
            }

            foreach (var (lineNumber, text) in EnumerateSnapshotLines(
                         session.Snapshot,
                         session.SearchStartLine,
                         session.SearchLineCount,
                         session.Cancellation.Token,
                         cacheLines: false))
            {
                session.Cancellation.Token.ThrowIfCancellationRequested();
                int page = lineNumber / PageLineCount;
                if (page != currentPage)
                {
                    if (currentPage >= 0)
                    {
                        totalMatches += currentPageMatches;
                        lock (session.Gate)
                        {
                            session.PageCounts[currentPage] = currentPageMatches;
                            session.HighestCountedPage = currentPage;
                            session.KnownMatchCount = totalMatches;
                            session.SearchedLineCount = processedLines;
                            UpdateCurrentOrdinalNoLock(session);
                        }
                    }

                    currentPage = page;
                    currentPageMatches = 0;
                }

                currentPageMatches += CountLineMatches(text, lineNumber, session.Query, session.Cancellation.Token);
                processedLines++;

                long nowTicks = Environment.TickCount64;
                if (nowTicks - lastRaisedTicks > 80)
                {
                    lastRaisedTicks = nowTicks;
                    lock (session.Gate)
                    {
                        session.KnownMatchCount = totalMatches + currentPageMatches;
                        session.SearchedLineCount = Math.Min(processedLines, session.SearchLineCount);
                    }
                    RaiseChanged();
                    await Task.Yield();
                }
            }

            if (currentPage >= 0)
            {
                totalMatches += currentPageMatches;
                lock (session.Gate)
                {
                    session.PageCounts[currentPage] = currentPageMatches;
                    session.HighestCountedPage = currentPage;
                    session.KnownMatchCount = totalMatches;
                    session.SearchedLineCount = session.SearchLineCount;
                    UpdateCurrentOrdinalNoLock(session);
                }
            }

            lock (session.Gate)
            {
                session.IsSearching = false;
                session.IsComplete = true;
                session.SearchedLineCount = session.SearchLineCount;
                UpdateCurrentOrdinalNoLock(session);
            }
            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (RegexMatchTimeoutException ex)
        {
            lock (session.Gate)
            {
                session.IsSearching = false;
                session.IsComplete = true;
                session.Error = ex.Message;
            }
            RaiseChanged();
        }
    }

    private static ReplaceAllResult CreateReplaceAllSnapshot(
        FindSession session,
        string replacement,
        IProgress<ReplaceAllProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            session.Cancellation.Token, cancellationToken);
        CancellationToken token = linkedCancellation.Token;

        TextBuffer.LineSnapshot snapshot = session.Snapshot;
        if (snapshot.Count == 0)
        {
            progress?.Report(new ReplaceAllProgress(0, 0, 0, IsComplete: true));
            return new ReplaceAllResult(session.Id, 0, 0, new TextBuffer.LineSnapshot([], 0), 0);
        }

        if (session.SearchLineCount <= 0)
        {
            progress?.Report(new ReplaceAllProgress(0, 0, 0, IsComplete: true));
            return new ReplaceAllResult(session.Id, 0, 0, new TextBuffer.LineSnapshot([], 0), 0);
        }

        int firstLine = session.SearchStartLine;
        int lastLine = session.SearchEndLine;

        var replacementPieces = new List<TextBuffer.LinePiece>();
        long replacementCount = 0;
        int globalLine = 0;
        int lineCount = session.SearchLineCount;
        int processedLines = 0;
        progress?.Report(new ReplaceAllProgress(0, lineCount, 0, IsComplete: false));

        foreach (TextBuffer.LinePiece piece in snapshot.Pieces)
        {
            token.ThrowIfCancellationRequested();

            int pieceStart = globalLine;
            int pieceEnd = globalLine + piece.LineCount;
            if (pieceEnd <= firstLine)
            {
                globalLine = pieceEnd;
                continue;
            }
            if (pieceStart > lastLine)
                break;

            int overlapStart = Math.Max(firstLine, pieceStart);
            int overlapEnd = Math.Min(lastLine + 1, pieceEnd);
            int sourceStart = piece.StartLine + overlapStart - pieceStart;
            int remaining = overlapEnd - overlapStart;
            int chunkSourceStart = sourceStart;
            int chunkGlobalStart = overlapStart;

            while (remaining > 0)
            {
                int chunkLineCount = Math.Min(remaining, ReplaceAllChunkLineCount);
                var analysis = AnalyzeReplacementChunk(
                    piece.Source,
                    chunkSourceStart,
                    chunkLineCount,
                    chunkGlobalStart,
                    session.Query,
                    replacement,
                    token);

                replacementCount += analysis.MatchCount;
                if (analysis.MatchCount == 0)
                {
                    replacementPieces.Add(piece with
                    {
                        StartLine = chunkSourceStart,
                        LineCount = chunkLineCount
                    });
                }
                else
                {
                    var source = new ReplacementTextSource(
                        piece.Source,
                        chunkSourceStart,
                        chunkLineCount,
                        chunkGlobalStart,
                        session.Query,
                        replacement,
                        analysis.LineDeltas,
                        analysis.CharDelta,
                        analysis.MaxLineLength);
                    replacementPieces.Add(new TextBuffer.LinePiece(source, 0, chunkLineCount));
                }

                chunkSourceStart += chunkLineCount;
                chunkGlobalStart += chunkLineCount;
                remaining -= chunkLineCount;
                processedLines += chunkLineCount;
                progress?.Report(new ReplaceAllProgress(
                    Math.Min(processedLines, lineCount),
                    lineCount,
                    replacementCount,
                    IsComplete: false));
            }

            globalLine = pieceEnd;
        }

        progress?.Report(new ReplaceAllProgress(lineCount, lineCount, replacementCount, IsComplete: true));
        return new ReplaceAllResult(
            session.Id,
            firstLine,
            lineCount,
            new TextBuffer.LineSnapshot(replacementPieces, lineCount),
            replacementCount);
    }

    private static ReplacementChunkAnalysis AnalyzeReplacementChunk(
        ITextSource source,
        int sourceStartLine,
        int lineCount,
        int globalStartLine,
        FindQuery query,
        string replacement,
        CancellationToken token)
    {
        int[]? lineDeltas = null;
        long charDelta = 0;
        long matchCount = 0;
        int maxLineLength = 0;
        int localLine = 0;

        foreach (string text in source.EnumerateLines(sourceStartLine, lineCount, cache: false))
        {
            token.ThrowIfCancellationRequested();
            int globalLine = globalStartLine + localLine;
            List<(int Line, int Col, int Length)> matches = FindLineMatches(text, globalLine, query, token).ToList();
            if (matches.Count == 0)
            {
                maxLineLength = Math.Max(maxLineLength, text.Length);
                localLine++;
                continue;
            }

            lineDeltas ??= new int[lineCount];
            string replaced = ReplaceLineMatches(text, matches, replacement);
            int delta = replaced.Length - text.Length;
            lineDeltas[localLine] = delta;
            charDelta += delta;
            matchCount += matches.Count;
            maxLineLength = Math.Max(maxLineLength, replaced.Length);
            localLine++;
        }

        return new ReplacementChunkAnalysis(
            lineDeltas ?? Array.Empty<int>(),
            charDelta,
            maxLineLength,
            matchCount);
    }

    private static string ReplaceLineMatches(
        string text,
        IReadOnlyList<(int Line, int Col, int Length)> matches,
        string replacement)
    {
        if (matches.Count == 0)
            return text;

        var builder = new StringBuilder(text);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            if (match.Col < 0 || match.Col > builder.Length)
                continue;

            int length = Math.Min(match.Length, builder.Length - match.Col);
            builder.Remove(match.Col, length);
            builder.Insert(match.Col, replacement);
        }

        return builder.ToString();
    }

    private FindSession? CurrentSession()
    {
        lock (_gate)
            return _session;
    }

    private bool SetCurrentMatch(FindSession session, (int Line, int Col, int Length) match, long? ordinalHint = null)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session) || session.Cancellation.IsCancellationRequested)
                return false;
        }

        bool resolveOrdinal;
        int ordinalVersion;
        lock (session.Gate)
        {
            session.CurrentMatch = match;
            session.CurrentOrdinal = ordinalHint;
            if (session.CurrentOrdinal == null)
                UpdateCurrentOrdinalNoLock(session);
            resolveOrdinal = session.CurrentOrdinal == null;
            ordinalVersion = resolveOrdinal ? ++session.OrdinalResolutionVersion : session.OrdinalResolutionVersion;
        }

        if (resolveOrdinal)
            ResolveCurrentOrdinalInBackground(session, match, ordinalVersion);

        RaiseChanged();
        return true;
    }

    private void ResolveCurrentOrdinalInBackground(
        FindSession session,
        (int Line, int Col, int Length) match,
        int ordinalVersion)
    {
        _ = Task.Run(() =>
        {
            try
            {
                long ordinal = CountOrdinalForMatch(session, match, session.Cancellation.Token);
                lock (_gate)
                {
                    if (!ReferenceEquals(_session, session) || session.Cancellation.IsCancellationRequested)
                        return;
                }

                lock (session.Gate)
                {
                    if (session.OrdinalResolutionVersion != ordinalVersion || session.CurrentMatch != match)
                        return;

                    session.CurrentOrdinal = ordinal;
                }

                RaiseChanged();
            }
            catch (OperationCanceledException)
            {
            }
            catch (RegexMatchTimeoutException)
            {
            }
            catch
            {
            }
        });
    }

    private static long CountOrdinalForMatch(
        FindSession session,
        (int Line, int Col, int Length) match,
        CancellationToken token)
    {
        if (session.SearchLineCount <= 0 || match.Line < session.SearchStartLine || match.Line > session.SearchEndLine)
            return 0;

        long beforeLine = CountSnapshotRangeFastOrFallback(
            session,
            session.SearchStartLine,
            match.Line - session.SearchStartLine,
            token);
        return beforeLine + CountLineMatchesThrough(session, match, token);
    }

    private static long CountLineMatchesThrough(
        FindSession session,
        (int Line, int Col, int Length) target,
        CancellationToken token)
    {
        string text = GetSnapshotLine(session.Snapshot, target.Line);
        long count = 0;
        foreach (var match in FindLineMatches(text, target.Line, session.Query, token))
        {
            count++;
            if (match.Line == target.Line && match.Col == target.Col && match.Length == target.Length)
                return count;
        }

        return count;
    }

    private static long? GetNextOrdinalHint(
        (int Line, int Col, int Length)? current,
        long? currentOrdinal,
        long knownMatchCount,
        (int Line, int Col, int Length) next)
    {
        if (knownMatchCount <= 0)
            return null;

        if (current == null || currentOrdinal is not { } ordinal)
            return null;

        return CompareMatches(next, current.Value) <= 0
            ? 1
            : Math.Min(knownMatchCount, ordinal + 1);
    }

    private static long? GetPreviousOrdinalHint(
        (int Line, int Col, int Length)? current,
        long? currentOrdinal,
        long knownMatchCount,
        (int Line, int Col, int Length) previous)
    {
        if (knownMatchCount <= 0)
            return null;

        if (current == null || currentOrdinal is not { } ordinal)
            return null;

        return CompareMatches(previous, current.Value) >= 0
            ? knownMatchCount
            : Math.Max(1, ordinal - 1);
    }

    private static int CompareMatches(
        (int Line, int Col, int Length) left,
        (int Line, int Col, int Length) right)
    {
        int line = left.Line.CompareTo(right.Line);
        if (line != 0)
            return line;

        int col = left.Col.CompareTo(right.Col);
        return col != 0 ? col : left.Length.CompareTo(right.Length);
    }

    private void UpdateCurrentOrdinalNoLock(FindSession session)
    {
        if (session.CurrentMatch == null)
            return;

        var current = session.CurrentMatch.Value;
        if (session.SearchLineCount <= 0 || current.Line < session.SearchStartLine || current.Line > session.SearchEndLine)
            return;

        int currentPage = current.Line / PageLineCount;
        if (session.HighestCountedPage < currentPage)
            return;

        long ordinal = 0;
        int firstPage = session.SearchStartLine / PageLineCount;
        for (int page = firstPage; page < currentPage; page++)
        {
            if (!session.PageCounts.TryGetValue(page, out long pageCount))
                return;
            ordinal += pageCount;
        }

        foreach (var match in GetOrScanPage(session, currentPage))
        {
            if (match.Line > current.Line || (match.Line == current.Line && match.Col > current.Col))
                break;
            ordinal++;
            if (match.Line == current.Line && match.Col == current.Col && match.Length == current.Length)
            {
                session.CurrentOrdinal = ordinal;
                return;
            }
        }
    }

    private IReadOnlyList<(int Line, int Col, int Length)> GetOrScanPage(FindSession session, int page)
    {
        lock (session.Gate)
        {
            if (session.PageMatchCache.TryGetValue(page, out var cached))
                return cached;
        }

        var matches = ScanPage(session, page, session.Cancellation.Token);
        lock (session.Gate)
        {
            if (session.PageMatchCache.TryGetValue(page, out var cached))
                return cached;

            session.PageMatchCache[page] = matches;
            session.PageCacheOrder.Enqueue(page);
            while (session.PageMatchCache.Count > CachedPageLimit && session.PageCacheOrder.Count > 0)
            {
                int evict = session.PageCacheOrder.Dequeue();
                session.PageMatchCache.Remove(evict);
            }
        }
        return matches;
    }

    private static List<(int Line, int Col, int Length)> ScanPage(FindSession session, int page, CancellationToken token)
    {
        int pageStartLine = page * PageLineCount;
        if (pageStartLine >= session.Snapshot.Count || session.SearchLineCount <= 0)
            return [];

        int pageEndLine = Math.Min(session.Snapshot.Count - 1, pageStartLine + PageLineCount - 1);
        int startLine = Math.Max(pageStartLine, session.SearchStartLine);
        int endLine = Math.Min(pageEndLine, session.SearchEndLine);
        if (startLine > endLine)
            return [];

        int count = endLine - startLine + 1;
        return ScanSnapshotRange(session.Snapshot, session.Query, startLine, count, token);
    }

    private static (int Line, int Col, int Length)? FindForward(
        FindSession session,
        int startLine,
        int startCol,
        bool includeStart)
    {
        if (session.Snapshot.Count == 0 || session.SearchLineCount <= 0 || !TryCreateMatcher(session.Query, out _, out _))
            return null;

        startCol = Math.Max(0, startCol);
        var token = session.Cancellation.Token;
        int firstLine = session.SearchStartLine;
        int lastLine = session.SearchEndLine;

        if (startLine < firstLine || startLine > lastLine)
            return FindForwardInRange(session.Snapshot, session.Query, firstLine, lastLine,
                boundaryLine: -1, boundaryCol: 0, includeStart: true, token);

        var match = FindForwardInRange(session.Snapshot, session.Query, startLine, lastLine,
            startLine, startCol, includeStart, token);
        if (match != null)
            return match;

        if (startLine > firstLine || startCol > 0)
            match = FindForwardBeforeBoundary(session.Snapshot, session.Query, firstLine, startLine, startCol, token);

        return match;
    }

    private static (int Line, int Col, int Length)? FindBackward(
        FindSession session,
        int startLine,
        int startCol)
    {
        if (session.Snapshot.Count == 0 || session.SearchLineCount <= 0 || !TryCreateMatcher(session.Query, out _, out _))
            return null;

        startCol = Math.Max(0, startCol);
        var token = session.Cancellation.Token;
        int firstLine = session.SearchStartLine;
        int lastLine = session.SearchEndLine;

        if (startLine < firstLine || startLine > lastLine)
            return FindBackwardInRange(session.Snapshot, session.Query, lastLine, firstLine,
                boundaryLine: -1, boundaryCol: 0, token);

        var match = FindBackwardInRange(session.Snapshot, session.Query, startLine, firstLine,
            startLine, startCol, token);
        if (match != null)
            return match;

        match = FindBackwardAfterBoundary(session.Snapshot, session.Query, startLine, startCol, lastLine, token);

        return match;
    }

    private static (int Line, int Col, int Length)? FindForwardInRange(
        TextBuffer.LineSnapshot snapshot,
        FindQuery query,
        int firstLine,
        int lastLine,
        int boundaryLine,
        int boundaryCol,
        bool includeStart,
        CancellationToken token)
    {
        if (firstLine > lastLine)
            return null;

        foreach (var (lineNumber, text) in EnumerateSnapshotLines(snapshot, firstLine, lastLine - firstLine + 1, token))
        {
            token.ThrowIfCancellationRequested();
            int minCol = lineNumber == boundaryLine ? boundaryCol : 0;
            foreach (var match in FindLineMatches(text, lineNumber, query, token))
            {
                if (lineNumber == boundaryLine)
                {
                    bool afterBoundary = includeStart ? match.Col >= minCol : match.Col > minCol;
                    if (!afterBoundary)
                        continue;
                }

                return match;
            }
        }

        return null;
    }

    private static (int Line, int Col, int Length)? FindBackwardInRange(
        TextBuffer.LineSnapshot snapshot,
        FindQuery query,
        int firstLine,
        int lastLine,
        int boundaryLine,
        int boundaryCol,
        CancellationToken token)
    {
        if (firstLine < lastLine)
            return null;

        for (int line = firstLine; line >= lastLine; line--)
        {
            token.ThrowIfCancellationRequested();
            string text = GetSnapshotLine(snapshot, line);
            (int Line, int Col, int Length)? last = null;
            foreach (var match in FindLineMatches(text, line, query, token))
            {
                if (line == boundaryLine && match.Col >= boundaryCol)
                    break;
                last = match;
            }

            if (last != null)
                return last;
        }

        return null;
    }

    private static (int Line, int Col, int Length)? FindForwardBeforeBoundary(
        TextBuffer.LineSnapshot snapshot,
        FindQuery query,
        int firstLine,
        int boundaryLine,
        int boundaryCol,
        CancellationToken token)
    {
        if (boundaryLine > firstLine)
        {
            var match = FindForwardInRange(snapshot, query, firstLine, boundaryLine - 1,
                boundaryLine: -1, boundaryCol: 0, includeStart: true, token);
            if (match != null)
                return match;
        }

        if (boundaryCol <= 0)
            return null;

        string text = GetSnapshotLine(snapshot, boundaryLine);
        foreach (var match in FindLineMatches(text, boundaryLine, query, token))
        {
            if (match.Col >= boundaryCol)
                break;
            return match;
        }

        return null;
    }

    private static (int Line, int Col, int Length)? FindBackwardAfterBoundary(
        TextBuffer.LineSnapshot snapshot,
        FindQuery query,
        int boundaryLine,
        int boundaryCol,
        int lastLine,
        CancellationToken token)
    {
        if (boundaryLine < lastLine)
        {
            var match = FindBackwardInRange(snapshot, query, lastLine, boundaryLine + 1,
                boundaryLine: -1, boundaryCol: 0, token);
            if (match != null)
                return match;
        }

        string text = GetSnapshotLine(snapshot, boundaryLine);
        (int Line, int Col, int Length)? last = null;
        foreach (var match in FindLineMatches(text, boundaryLine, query, token))
        {
            if (match.Col > boundaryCol)
                last = match;
        }

        return last;
    }

    private static bool TryCountFastLiteralMatches(
        FindSession session,
        Action<FastCountProgress>? progress,
        out long count)
    {
        return TryCountFastLiteralMatches(
            session,
            session.SearchStartLine,
            session.SearchLineCount,
            progress,
            out count);
    }

    private static bool TryCountFastLiteralMatches(
        FindSession session,
        int startLine,
        int lineCount,
        Action<FastCountProgress>? progress,
        out long count)
    {
        count = 0;
        if (lineCount <= 0)
            return true;

        FindQuery query = session.Query;
        if (query.UseRegex || query.Text.Length == 0 || !CanFastCountSelection(session))
        {
            return false;
        }

        if (!SearchRangeContainsFastCounter(session, startLine, lineCount))
            return false;

        int firstLine = startLine;
        int endLine = startLine + lineCount;
        int globalLine = 0;
        int processedLines = 0;
        long total = 0;

        foreach (TextBuffer.LinePiece piece in session.Snapshot.Pieces)
        {
            session.Cancellation.Token.ThrowIfCancellationRequested();

            int pieceStart = globalLine;
            int pieceEnd = globalLine + piece.LineCount;
            if (pieceEnd <= firstLine)
            {
                globalLine = pieceEnd;
                continue;
            }
            if (pieceStart >= endLine)
                break;

            int overlapStart = Math.Max(firstLine, pieceStart);
            int overlapEnd = Math.Min(endLine, pieceEnd);
            int sourceStart = piece.StartLine + overlapStart - pieceStart;
            int take = overlapEnd - overlapStart;
            long totalBeforePiece = total;
            int processedBeforePiece = processedLines;

            if (piece.Source is IFastLiteralMatchCounter counter)
            {
                var request = new FastLiteralMatchRequest(
                    query.Text,
                    query.MatchCase,
                    query.WholeWord,
                    sourceStart,
                    take,
                    pieceProgress =>
                    {
                        int pieceLines = EstimateSearchedLines(take, pieceProgress.BytesRead, pieceProgress.TotalBytes);
                        progress?.Invoke(new FastCountProgress(
                            processedBeforePiece + pieceLines,
                            totalBeforePiece + pieceProgress.MatchCount));
                    });

                if (counter.TryCountLiteralMatches(request, session.Cancellation.Token, out long pieceCount))
                {
                    total += pieceCount;
                    processedLines += take;
                    progress?.Invoke(new FastCountProgress(processedLines, total));
                    globalLine = pieceEnd;
                    continue;
                }
            }

            total += CountSourceRange(
                piece.Source,
                sourceStart,
                take,
                overlapStart,
                query,
                session.Cancellation.Token);
            processedLines += take;
            progress?.Invoke(new FastCountProgress(processedLines, total));
            globalLine = pieceEnd;
        }

        count = total;
        return true;
    }

    private static bool SearchRangeContainsFastCounter(FindSession session, int startLine, int lineCount)
    {
        int firstLine = startLine;
        int endLine = startLine + lineCount;
        int globalLine = 0;
        foreach (TextBuffer.LinePiece piece in session.Snapshot.Pieces)
        {
            int pieceStart = globalLine;
            int pieceEnd = globalLine + piece.LineCount;
            if (pieceEnd <= firstLine)
            {
                globalLine = pieceEnd;
                continue;
            }
            if (pieceStart >= endLine)
                break;

            if (piece.Source is IFastLiteralMatchCounter)
                return true;

            globalLine = pieceEnd;
        }

        return false;
    }

    private static bool CanFastCountSelection(FindSession session)
    {
        if (session.Query.SelectionBounds is not { } selection)
            return true;

        if (selection.startCol != 0)
            return false;

        string endLine = GetSnapshotLine(session.Snapshot, selection.endLine);
        return selection.endCol >= endLine.Length;
    }

    private static int EstimateSearchedLines(int searchLineCount, long bytesRead, long totalBytes)
    {
        if (searchLineCount <= 0)
            return 0;

        if (totalBytes <= 0)
            return searchLineCount;

        double ratio = Math.Clamp(bytesRead / (double)totalBytes, 0, 1);
        return Math.Clamp((int)Math.Round(searchLineCount * ratio), 0, searchLineCount);
    }

    private static long CountSnapshotRange(
        TextBuffer.LineSnapshot snapshot,
        FindQuery query,
        int startLine,
        int count,
        CancellationToken token)
    {
        long total = 0;
        foreach (var (lineNumber, text) in EnumerateSnapshotLines(snapshot, startLine, count, token))
            total += CountLineMatches(text, lineNumber, query, token);
        return total;
    }

    private static long CountSnapshotRangeFastOrFallback(
        FindSession session,
        int startLine,
        int count,
        CancellationToken token)
    {
        if (count <= 0)
            return 0;

        if (TryCountFastLiteralMatches(session, startLine, count, progress: null, out long fastCount))
            return fastCount;

        return CountSnapshotRange(session.Snapshot, session.Query, startLine, count, token);
    }

    private static long CountSourceRange(
        ITextSource source,
        int sourceStartLine,
        int count,
        int globalStartLine,
        FindQuery query,
        CancellationToken token)
    {
        long total = 0;
        int localLine = 0;
        foreach (string text in source.EnumerateLines(sourceStartLine, count, cache: false))
        {
            token.ThrowIfCancellationRequested();
            total += CountLineMatches(text, globalStartLine + localLine, query, token);
            localLine++;
        }

        return total;
    }

    private static List<(int Line, int Col, int Length)> ScanSnapshotRange(
        TextBuffer.LineSnapshot snapshot,
        FindQuery query,
        int startLine,
        int count,
        CancellationToken token)
    {
        if (!TryCreateMatcher(query, out _, out _))
            return [];

        var matches = new List<(int Line, int Col, int Length)>();
        foreach (var (lineNumber, text) in EnumerateSnapshotLines(snapshot, startLine, count, token))
            matches.AddRange(FindLineMatches(text, lineNumber, query, token));
        return matches;
    }

    private static IEnumerable<(int Line, string Text)> EnumerateSnapshotLines(
        TextBuffer.LineSnapshot snapshot,
        int startLine,
        int count,
        CancellationToken token,
        bool cacheLines = true)
    {
        if (count <= 0)
            yield break;

        int endLine = Math.Min(snapshot.Count, startLine + count);
        int globalLine = 0;
        foreach (TextBuffer.LinePiece piece in snapshot.Pieces)
        {
            int pieceStart = globalLine;
            int pieceEnd = globalLine + piece.LineCount;
            if (pieceEnd <= startLine)
            {
                globalLine = pieceEnd;
                continue;
            }
            if (pieceStart >= endLine)
                yield break;

            int overlapStart = Math.Max(startLine, pieceStart);
            int overlapEnd = Math.Min(endLine, pieceEnd);
            int sourceStart = piece.StartLine + overlapStart - pieceStart;
            int take = overlapEnd - overlapStart;
            int lineNumber = overlapStart;
            foreach (string line in piece.Source.EnumerateLines(sourceStart, take, cacheLines))
            {
                token.ThrowIfCancellationRequested();
                yield return (lineNumber, line);
                lineNumber++;
            }

            globalLine = pieceEnd;
        }
    }

    private static string GetSnapshotLine(TextBuffer.LineSnapshot snapshot, int line)
    {
        int globalLine = 0;
        foreach (TextBuffer.LinePiece piece in snapshot.Pieces)
        {
            int pieceEnd = globalLine + piece.LineCount;
            if (line < pieceEnd)
                return piece.Source.GetLine(piece.StartLine + line - globalLine);
            globalLine = pieceEnd;
        }

        throw new ArgumentOutOfRangeException(nameof(line));
    }

    private static long CountLineMatches(string text, int lineNumber, FindQuery query, CancellationToken token)
    {
        if (!LineIntersectsSelection(lineNumber, query, text.Length, out int minCol, out int maxCol))
            return 0;

        if (query.UseRegex || query.WholeWord)
        {
            if (!TryCreateMatcher(query, out Regex? regex, out _) || regex == null)
                return 0;

            return CountRegexLineMatches(text, minCol, maxCol, regex, token);
        }

        int pos = Math.Clamp(minCol, 0, text.Length);
        if (pos >= maxCol)
            return 0;

        long count = 0;
        int cancellationCheck = 0;
        if (query.MatchCase && query.Text.Length == 1)
        {
            char needle = query.Text[0];
            while (pos < text.Length)
            {
                if ((cancellationCheck++ & 0x3F) == 0)
                    token.ThrowIfCancellationRequested();

                int idx = text.IndexOf(needle, pos);
                if (idx < 0 || idx >= maxCol)
                    break;
                count++;
                pos = idx + 1;
            }

            return count;
        }

        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        while (pos < text.Length)
        {
            if ((cancellationCheck++ & 0x3F) == 0)
                token.ThrowIfCancellationRequested();

            int idx = text.IndexOf(query.Text, pos, comparison);
            if (idx < 0 || idx >= maxCol)
                break;
            if (idx + query.Text.Length <= maxCol)
                count++;
            pos = idx + 1;
        }

        return count;
    }

    private static long CountRegexLineMatches(
        string text,
        int minCol,
        int maxCol,
        Regex regex,
        CancellationToken token)
    {
        int searchStart = Math.Clamp(minCol, 0, text.Length);
        if (searchStart >= text.Length || searchStart >= maxCol)
            return 0;

        if (searchStart > 0)
            return CountRegexLineMatchesFromStart(text, searchStart, minCol, maxCol, regex, token);

        long count = 0;
        int cancellationCheck = 0;
        foreach (ValueMatch match in regex.EnumerateMatches(text.AsSpan()))
        {
            if ((cancellationCheck++ & 0x3F) == 0)
                token.ThrowIfCancellationRequested();

            if (match.Index >= maxCol)
                break;

            if (match.Length > 0 && match.Index + match.Length <= maxCol)
                count++;
        }

        return count;
    }

    private static long CountRegexLineMatchesFromStart(
        string text,
        int searchStart,
        int minCol,
        int maxCol,
        Regex regex,
        CancellationToken token)
    {
        long count = 0;
        int cancellationCheck = 0;
        Match match = regex.Match(text, searchStart);
        while (match.Success)
        {
            if ((cancellationCheck++ & 0x3F) == 0)
                token.ThrowIfCancellationRequested();

            if (match.Index >= maxCol)
                break;

            if (match.Length > 0 && match.Index >= minCol && match.Index + match.Length <= maxCol)
                count++;

            int nextIndex = match.Length == 0 ? match.Index + 1 : match.Index + Math.Max(1, match.Length);
            if (nextIndex > text.Length)
                break;
            match = regex.Match(text, nextIndex);
        }

        return count;
    }

    private static IEnumerable<(int Line, int Col, int Length)> FindLineMatches(
        string text,
        int lineNumber,
        FindQuery query,
        CancellationToken token)
    {
        if (!LineIntersectsSelection(lineNumber, query, text.Length, out int minCol, out int maxCol))
            yield break;

        if (query.UseRegex || query.WholeWord)
        {
            if (!TryCreateMatcher(query, out Regex? regex, out _) || regex == null)
                yield break;

            int searchStart = Math.Clamp(minCol, 0, text.Length);
            Match match = regex.Match(text, searchStart);
            while (match.Success)
            {
                token.ThrowIfCancellationRequested();
                if (match.Index >= maxCol)
                    yield break;

                if (match.Length > 0 && match.Index >= minCol && match.Index + match.Length <= maxCol)
                    yield return (lineNumber, match.Index, match.Length);

                int nextIndex = match.Length == 0 ? match.Index + 1 : match.Index + Math.Max(1, match.Length);
                if (nextIndex > text.Length)
                    yield break;
                match = regex.Match(text, nextIndex);
            }
            yield break;
        }

        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int pos = Math.Clamp(minCol, 0, text.Length);
        while (pos < text.Length)
        {
            token.ThrowIfCancellationRequested();
            int idx = text.IndexOf(query.Text, pos, comparison);
            if (idx < 0 || idx >= maxCol)
                break;
            if (idx + query.Text.Length <= maxCol)
                yield return (lineNumber, idx, query.Text.Length);
            pos = idx + 1;
        }
    }

    private static bool LineIntersectsSelection(
        int lineNumber,
        FindQuery query,
        int lineLength,
        out int minCol,
        out int maxCol)
    {
        minCol = 0;
        maxCol = lineLength;
        if (query.SelectionBounds == null)
            return true;

        var (startLine, startCol, endLine, endCol) = query.SelectionBounds.Value;
        if (lineNumber < startLine || lineNumber > endLine)
            return false;

        if (lineNumber == startLine)
            minCol = Math.Clamp(startCol, 0, lineLength);
        if (lineNumber == endLine)
            maxCol = Math.Clamp(endCol, 0, lineLength);

        return minCol <= maxCol;
    }

    private static bool TryCreateMatcher(FindQuery query, out Regex? regex, out string? error)
    {
        regex = query.Regex;
        error = null;
        if (regex != null)
            return true;

        if (!query.UseRegex && !query.WholeWord)
        {
            regex = null;
            return true;
        }

        try
        {
            string pattern = query.UseRegex ? query.Text : Regex.Escape(query.Text);
            if (query.WholeWord)
                pattern = @"\b" + pattern + @"\b";

            var options = RegexOptions.CultureInvariant;
            if (!query.MatchCase)
                options |= RegexOptions.IgnoreCase;
            regex = new Regex(pattern, options, RegexTimeout);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static (int startLine, int startCol, int endLine, int endCol)? NormalizeSelection(
        (int startLine, int startCol, int endLine, int endCol)? selectionBounds,
        int lineCount)
    {
        if (selectionBounds == null || lineCount <= 0)
            return null;

        var (startLine, startCol, endLine, endCol) = selectionBounds.Value;
        if (endLine < startLine || (startLine == endLine && endCol < startCol))
            (startLine, startCol, endLine, endCol) = (endLine, endCol, startLine, startCol);

        startLine = Math.Clamp(startLine, 0, lineCount - 1);
        endLine = Math.Clamp(endLine, startLine, lineCount - 1);
        startCol = Math.Max(0, startCol);
        endCol = Math.Max(0, endCol);
        return (startLine, startCol, endLine, endCol);
    }

    private static bool ShouldShowFindProgressImmediately(
        TextBuffer buffer,
        (int startLine, int startCol, int endLine, int endCol)? selectionBounds)
    {
        if (selectionBounds is not { } selection)
        {
            return buffer.Count >= ImmediateFindProgressThresholdLines ||
                   buffer.CharCount >= ImmediateFindProgressThresholdChars;
        }

        int selectedLineCount = selection.endLine - selection.startLine + 1;
        if (selectedLineCount >= ImmediateFindProgressThresholdLines)
            return true;

        if (selectedLineCount > SelectionProgressLengthProbeLimit)
            return false;

        long selectedChars = 0;
        for (int line = selection.startLine; line <= selection.endLine; line++)
        {
            int lineLength = buffer.GetLineLength(line);
            int startCol = line == selection.startLine ? Math.Min(selection.startCol, lineLength) : 0;
            int endCol = line == selection.endLine ? Math.Min(selection.endCol, lineLength) : lineLength;
            selectedChars += Math.Max(0, endCol - startCol);
            if (line < selection.endLine)
                selectedChars++;

            if (selectedChars >= ImmediateFindProgressThresholdChars)
                return true;
        }

        return false;
    }

    private static (int StartLine, int LineCount) GetSearchLineRange(int lineCount, FindQuery query)
    {
        if (lineCount <= 0)
            return (0, 0);

        if (query.SelectionBounds is not { } selection)
            return (0, lineCount);

        int startLine = Math.Clamp(selection.startLine, 0, lineCount - 1);
        int endLine = Math.Clamp(selection.endLine, startLine, lineCount - 1);
        return (startLine, endLine - startLine + 1);
    }

    private static int FindInsertionIndex(IReadOnlyList<(int Line, int Col, int Length)> matches, int caretLine, int caretCol)
    {
        int lo = 0, hi = matches.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var (line, col, _) = matches[mid];
            if (line < caretLine || (line == caretLine && col < caretCol))
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return lo < matches.Count ? lo : 0;
    }

    private static int FindFirstLineIndex(IReadOnlyList<(int Line, int Col, int Length)> matches, int firstLine)
    {
        int lo = 0, hi = matches.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (matches[mid].Line < firstLine)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return lo;
    }

    private static bool IntersectsColumns((int Line, int Col, int Length) match, int firstColumn, int lastColumn)
    {
        if (lastColumn == int.MaxValue)
            return true;
        int end = match.Col + match.Length;
        return end >= firstColumn && match.Col <= lastColumn;
    }

    private static int ClampCount(long count) =>
        count > int.MaxValue ? int.MaxValue : (int)Math.Max(0, count);

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler == null)
            return;

        if (_syncContext == null || SynchronizationContext.Current == _syncContext)
        {
            handler(this, EventArgs.Empty);
            return;
        }

        _syncContext.Post(_ => handler(this, EventArgs.Empty), null);
    }

    private sealed record FindQuery(
        string Text,
        bool MatchCase,
        bool UseRegex,
        bool WholeWord,
        (int startLine, int startCol, int endLine, int endCol)? SelectionBounds,
        Regex? Regex);

    private readonly record struct FastCountProgress(int SearchedLineCount, long MatchCount);

    private sealed record ReplacementChunkAnalysis(
        int[] LineDeltas,
        long CharDelta,
        int MaxLineLength,
        long MatchCount);

    private sealed class ReplacementTextSource : ITextSource
    {
        private readonly ITextSource _inner;
        private readonly int _sourceStartLine;
        private readonly int _globalStartLine;
        private readonly FindQuery _query;
        private readonly string _replacement;
        private readonly int[] _lineDeltas;
        private readonly long _charDelta;

        public ReplacementTextSource(
            ITextSource inner,
            int sourceStartLine,
            int lineCount,
            int globalStartLine,
            FindQuery query,
            string replacement,
            int[] lineDeltas,
            long charDelta,
            int maxLineLength)
        {
            _inner = inner;
            _sourceStartLine = sourceStartLine;
            _globalStartLine = globalStartLine;
            _query = query;
            _replacement = replacement;
            _lineDeltas = lineDeltas;
            _charDelta = charDelta;
            LineCount = lineCount;
            MaxLineLength = maxLineLength;
        }

        public int LineCount { get; }
        public long CharCountWithoutLineEndings =>
            _inner.GetCharCountWithoutLineEndings(_sourceStartLine, LineCount) + _charDelta;
        public int MaxLineLength { get; }

        public string GetLine(int line) =>
            ReplaceLine(line, _inner.GetLine(_sourceStartLine + line));

        public int GetLineLength(int line) =>
            _inner.GetLineLength(_sourceStartLine + line) + GetLineDelta(line);

        public string GetLineSegment(int line, int startColumn, int length)
        {
            if (length <= 0)
                return "";

            string value = GetLine(line);
            if (startColumn >= value.Length)
                return "";

            return value.Substring(startColumn, Math.Min(length, value.Length - startColumn));
        }

        public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
        {
            int end = Math.Min(LineCount, startLine + count);
            int sourceLine = _sourceStartLine + startLine;
            int localLine = startLine;
            foreach (string line in _inner.EnumerateLines(sourceLine, end - startLine, cache))
            {
                yield return ReplaceLine(localLine, line);
                localLine++;
            }
        }

        public int GetMaxLineLength(int startLine, int count) =>
            count <= 0 ? 0 : MaxLineLength;

        public long GetCharCountWithoutLineEndings(int startLine, int count)
        {
            if (count <= 0)
                return 0;

            int actualCount = Math.Min(LineCount - startLine, count);
            long total = _inner.GetCharCountWithoutLineEndings(_sourceStartLine + startLine, actualCount);
            for (int i = startLine; i < startLine + actualCount; i++)
                total += GetLineDelta(i);
            return total;
        }

        private int GetLineDelta(int line) =>
            (uint)line < (uint)_lineDeltas.Length ? _lineDeltas[line] : 0;

        private string ReplaceLine(int localLine, string text)
        {
            int globalLine = _globalStartLine + localLine;
            List<(int Line, int Col, int Length)> matches = FindLineMatches(
                text,
                globalLine,
                _query,
                CancellationToken.None).ToList();
            return ReplaceLineMatches(text, matches, _replacement);
        }
    }

    private sealed class FindSession
    {
        public FindSession(
            int id,
            TextBuffer.LineSnapshot snapshot,
            FindQuery query,
            bool showProgressImmediately)
        {
            Id = id;
            Snapshot = snapshot;
            Query = query;
            ProgressTextVisible = showProgressImmediately;
            (SearchStartLine, SearchLineCount) = GetSearchLineRange(snapshot.Count, query);
        }

        public int Id { get; }
        public TextBuffer.LineSnapshot Snapshot { get; }
        public FindQuery Query { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public object Gate { get; } = new();
        public Dictionary<int, long> PageCounts { get; } = new();
        public Dictionary<int, List<(int Line, int Col, int Length)>> PageMatchCache { get; } = new();
        public Queue<int> PageCacheOrder { get; } = new();
        public int HighestCountedPage { get; set; } = -1;
        public long KnownMatchCount { get; set; }
        public int SearchedLineCount { get; set; }
        public int SearchStartLine { get; }
        public int SearchLineCount { get; }
        public int SearchEndLine => SearchLineCount <= 0 ? SearchStartLine - 1 : SearchStartLine + SearchLineCount - 1;
        public double ProgressPercent => SearchLineCount <= 0
            ? 100
            : Math.Clamp(SearchedLineCount * 100.0 / SearchLineCount, 0, 100);
        public bool ProgressTextVisible { get; set; }
        public bool IsSearching { get; set; } = true;
        public bool IsComplete { get; set; }
        public bool InvalidPattern { get; set; }
        public string? Error { get; set; }
        public (int Line, int Col, int Length)? CurrentMatch { get; set; }
        public long? CurrentOrdinal { get; set; }
        public int OrdinalResolutionVersion { get; set; }

        public void Cancel()
        {
            try { Cancellation.Cancel(); } catch { }
        }
    }
}
