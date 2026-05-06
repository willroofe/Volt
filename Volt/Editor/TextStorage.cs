using System.Buffers;
using System.IO;
using System.Numerics;
using System.Text;

namespace Volt;

internal interface ITextSource
{
    int LineCount { get; }
    long CharCountWithoutLineEndings { get; }
    int MaxLineLength { get; }

    string GetLine(int line);
    int GetLineLength(int line);
    string GetLineSegment(int line, int startColumn, int length);
    IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true);
    int GetMaxLineLength(int startLine, int count);
    long GetCharCountWithoutLineEndings(int startLine, int count);
}

internal interface IFastLiteralMatchCounter
{
    bool TryCountLiteralMatches(FastLiteralMatchRequest request, CancellationToken cancellationToken, out long count);
}

internal readonly record struct FastLiteralMatchRequest(
    string Text,
    bool MatchCase,
    bool WholeWord,
    int StartLine,
    int LineCount,
    Action<FastLiteralMatchProgress>? Progress);

internal readonly record struct FastLiteralMatchProgress(long BytesRead, long TotalBytes, long MatchCount);

internal sealed class MemoryTextSource : ITextSource
{
    private readonly List<string> _lines;
    private readonly long[] _charPrefix;
    private readonly int[] _maxPrefix;

    public MemoryTextSource(IEnumerable<string> lines)
    {
        _lines = lines.ToList();
        if (_lines.Count == 0)
            _lines.Add("");

        _charPrefix = new long[_lines.Count + 1];
        _maxPrefix = new int[_lines.Count + 1];
        for (int i = 0; i < _lines.Count; i++)
        {
            _charPrefix[i + 1] = _charPrefix[i] + _lines[i].Length;
            _maxPrefix[i + 1] = Math.Max(_maxPrefix[i], _lines[i].Length);
        }
    }

    public int LineCount => _lines.Count;
    public long CharCountWithoutLineEndings => _charPrefix[^1];
    public int MaxLineLength => _maxPrefix[^1];

    public string GetLine(int line) => _lines[line];
    public int GetLineLength(int line) => _lines[line].Length;

    public string GetLineSegment(int line, int startColumn, int length)
    {
        string text = _lines[line];
        if (startColumn >= text.Length || length <= 0)
            return "";
        int count = Math.Min(length, text.Length - startColumn);
        return text.Substring(startColumn, count);
    }

    public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
    {
        int end = Math.Min(_lines.Count, startLine + count);
        for (int i = startLine; i < end; i++)
            yield return _lines[i];
    }

    public int GetMaxLineLength(int startLine, int count)
    {
        int end = Math.Min(_lines.Count, startLine + count);
        int max = 0;
        for (int i = startLine; i < end; i++)
            max = Math.Max(max, _lines[i].Length);
        return max;
    }

    public long GetCharCountWithoutLineEndings(int startLine, int count)
    {
        int end = Math.Min(_lines.Count, startLine + count);
        return _charPrefix[end] - _charPrefix[startLine];
    }
}

internal sealed class PrefixedTextSource : ITextSource
{
    public PrefixedTextSource(ITextSource inner, string prefix)
    {
        Inner = inner;
        Prefix = prefix;
    }

    public ITextSource Inner { get; }
    public string Prefix { get; }

    public int LineCount => Inner.LineCount;
    public long CharCountWithoutLineEndings => Inner.CharCountWithoutLineEndings + (long)Prefix.Length * Inner.LineCount;
    public int MaxLineLength => Inner.MaxLineLength + Prefix.Length;

    public string GetLine(int line) => Prefix + Inner.GetLine(line);
    public int GetLineLength(int line) => Prefix.Length + Inner.GetLineLength(line);

    public string GetLineSegment(int line, int startColumn, int length)
    {
        if (length <= 0 || startColumn >= GetLineLength(line))
            return "";

        int count = Math.Min(length, GetLineLength(line) - startColumn);
        if (startColumn >= Prefix.Length)
            return Inner.GetLineSegment(line, startColumn - Prefix.Length, count);

        if (startColumn + count <= Prefix.Length)
            return Prefix.Substring(startColumn, count);

        string prefixPart = Prefix[startColumn..];
        string innerPart = Inner.GetLineSegment(line, 0, count - prefixPart.Length);
        return prefixPart + innerPart;
    }

    public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
    {
        foreach (string line in Inner.EnumerateLines(startLine, count, cache))
            yield return Prefix + line;
    }

    public int GetMaxLineLength(int startLine, int count) =>
        count <= 0 ? 0 : Inner.GetMaxLineLength(startLine, count) + Prefix.Length;

    public long GetCharCountWithoutLineEndings(int startLine, int count)
    {
        if (count <= 0)
            return 0;

        int actualCount = Math.Min(LineCount - startLine, count);
        return Inner.GetCharCountWithoutLineEndings(startLine, count) + (long)Prefix.Length * actualCount;
    }
}

internal sealed class FixedPrefixRemovalTextSource : ITextSource
{
    public FixedPrefixRemovalTextSource(ITextSource inner, string prefix)
    {
        Inner = inner;
        Prefix = prefix;
    }

    public ITextSource Inner { get; }
    public string Prefix { get; }

    public int LineCount => Inner.LineCount;
    public long CharCountWithoutLineEndings =>
        Math.Max(0, Inner.CharCountWithoutLineEndings - (long)Prefix.Length * Inner.LineCount);
    public int MaxLineLength => Math.Max(0, Inner.MaxLineLength - Prefix.Length);

    public string GetLine(int line)
    {
        string value = Inner.GetLine(line);
        return value.StartsWith(Prefix, StringComparison.Ordinal)
            ? value[Prefix.Length..]
            : value;
    }

    public int GetLineLength(int line) => Math.Max(0, Inner.GetLineLength(line) - Prefix.Length);

    public string GetLineSegment(int line, int startColumn, int length)
    {
        string value = GetLine(line);
        if (length <= 0 || startColumn >= value.Length)
            return "";

        return value.Substring(startColumn, Math.Min(length, value.Length - startColumn));
    }

    public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
    {
        foreach (string line in Inner.EnumerateLines(startLine, count, cache))
            yield return line.StartsWith(Prefix, StringComparison.Ordinal)
                ? line[Prefix.Length..]
                : line;
    }

    public int GetMaxLineLength(int startLine, int count) =>
        count <= 0 ? 0 : Math.Max(0, Inner.GetMaxLineLength(startLine, count) - Prefix.Length);

    public long GetCharCountWithoutLineEndings(int startLine, int count)
    {
        if (count <= 0)
            return 0;

        int actualCount = Math.Min(LineCount - startLine, count);
        return Math.Max(0, Inner.GetCharCountWithoutLineEndings(startLine, count) - (long)Prefix.Length * actualCount);
    }
}

internal sealed class LeadingSpaceRemovalTextSource : ITextSource
{
    public LeadingSpaceRemovalTextSource(ITextSource inner, int maxSpaces)
    {
        Inner = inner;
        MaxSpaces = Math.Max(0, maxSpaces);
    }

    public ITextSource Inner { get; }
    public int MaxSpaces { get; }

    public int LineCount => Inner.LineCount;
    public long CharCountWithoutLineEndings => Inner.CharCountWithoutLineEndings;
    public int MaxLineLength => Inner.MaxLineLength;

    public string GetLine(int line)
    {
        string value = Inner.GetLine(line);
        int remove = CountLeadingSpaces(value, MaxSpaces);
        return remove == 0 ? value : value[remove..];
    }

    public int GetLineLength(int line) => Math.Max(0, Inner.GetLineLength(line) - CountRemoved(line));

    public string GetLineSegment(int line, int startColumn, int length)
    {
        if (length <= 0)
            return "";

        int remove = CountRemoved(line);
        int lineLength = Math.Max(0, Inner.GetLineLength(line) - remove);
        if (startColumn >= lineLength)
            return "";

        return Inner.GetLineSegment(line, startColumn + remove, Math.Min(length, lineLength - startColumn));
    }

    public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
    {
        foreach (string line in Inner.EnumerateLines(startLine, count, cache))
        {
            int remove = CountLeadingSpaces(line, MaxSpaces);
            yield return remove == 0 ? line : line[remove..];
        }
    }

    public int GetMaxLineLength(int startLine, int count) =>
        count <= 0 ? 0 : Inner.GetMaxLineLength(startLine, count);

    public long GetCharCountWithoutLineEndings(int startLine, int count) =>
        Inner.GetCharCountWithoutLineEndings(startLine, count);

    private int CountRemoved(int line)
    {
        if (MaxSpaces <= 0)
            return 0;

        int inspect = Math.Min(MaxSpaces, Inner.GetLineLength(line));
        return CountLeadingSpaces(Inner.GetLineSegment(line, 0, inspect), inspect);
    }

    internal static int CountLeadingSpaces(string text, int maxSpaces)
    {
        int count = 0;
        int limit = Math.Min(maxSpaces, text.Length);
        while (count < limit && text[count] == ' ')
            count++;
        return count;
    }
}

internal sealed class FileTextSource : ITextSource, IFastLiteralMatchCounter
{
    private const int CachePageLineCount = 512;
    private const int LiteralSearchChunkByteCount = 16 * 1024 * 1024;

    private readonly string _path;
    private readonly Encoding _encoding;
    private readonly int _tabSize;
    private readonly LargeFileLineIndex _index;
    private readonly TextPageCache _cache = new(capacity: 16_384);

    public FileTextSource(string path, Encoding encoding, int tabSize, LargeFileLineIndex index)
    {
        _path = path;
        _encoding = encoding;
        _tabSize = tabSize;
        _index = index;
    }

    public int LineCount => _index.LineCount;
    public long CharCountWithoutLineEndings => _index.CharCountWithoutLineEndings;
    public int MaxLineLength => _index.MaxLineLength;
    public string LineEnding => _index.LineEnding;

    public static bool SupportsEncoding(Encoding encoding) =>
        encoding.CodePage == Encoding.UTF8.CodePage;

    public bool TryCountLiteralMatches(FastLiteralMatchRequest request, CancellationToken cancellationToken,
        out long count)
    {
        count = 0;
        if (request.LineCount <= 0)
            return true;

        int endLine = request.StartLine + request.LineCount;
        if (request.StartLine < 0 || endLine < request.StartLine || endLine > _index.LineCount)
            return false;

        if (_index.HasTabs || ContainsLineBreak(request.Text) || ContainsSurrogate(request.Text))
            return false;

        bool requiresAscii = !request.MatchCase || request.WholeWord;
        if (requiresAscii && (_index.HasNonAscii || !IsAscii(request.Text)))
            return false;

        byte[] needle = _encoding.GetBytes(request.Text);
        if (needle.Length == 0)
            return false;

        long startOffset = GetByteOffsetForLine(request.StartLine, cancellationToken);
        long endOffset = GetByteOffsetForLine(endLine, cancellationToken);
        count = CountLiteralMatchesInFile(_path, startOffset, endOffset, needle, request.MatchCase,
            request.WholeWord, cancellationToken, progress: request.Progress);
        return true;
    }

    public string GetLine(int line)
    {
        if (_cache.TryGet(line, out string? cached) && cached != null)
            return cached;

        return LoadPageContaining(line);
    }

    public int GetLineLength(int line)
    {
        if (_index.LineCount == 1)
            return _index.MaxLineLength;
        return GetLine(line).Length;
    }

    public string GetLineSegment(int line, int startColumn, int length)
    {
        if (length <= 0)
            return "";

        if (_cache.TryGet(line, out string? cached) && cached != null)
            return startColumn >= cached.Length
                ? ""
                : cached.Substring(startColumn, Math.Min(length, cached.Length - startColumn));

        if (_index.LineCount != 1)
        {
            string value = GetLine(line);
            return startColumn >= value.Length
                ? ""
                : value.Substring(startColumn, Math.Min(length, value.Length - startColumn));
        }

        var (checkpointLine, offset) = _index.GetCheckpointForLine(line);
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 20, FileOptions.RandomAccess);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, _encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 20);

        int currentLine = checkpointLine;
        while (currentLine < line)
        {
            if (reader.ReadLine() == null)
                return "";
            currentLine++;
        }

        Span<char> one = stackalloc char[1];
        int skipped = 0;
        while (skipped < startColumn)
        {
            int n = reader.Read(one);
            if (n == 0 || one[0] == '\r' || one[0] == '\n')
                return "";
            skipped++;
        }

        var chars = new char[length];
        int read = 0;
        while (read < length)
        {
            int ch = reader.Read();
            if (ch < 0 || ch == '\r' || ch == '\n')
                break;
            chars[read++] = (char)ch;
        }

        return new string(chars, 0, read);
    }

    private string LoadPageContaining(int line)
    {
        if ((uint)line >= (uint)_index.LineCount)
            throw new ArgumentOutOfRangeException(nameof(line));

        int pageStart = line - line % CachePageLineCount;
        int pageCount = Math.Min(CachePageLineCount, _index.LineCount - pageStart);
        string requested = "";
        int currentLine = pageStart;

        foreach (string value in ReadLinesFromFile(pageStart, pageCount))
        {
            _cache.Add(currentLine, value);
            if (currentLine == line)
                requested = value;
            currentLine++;
        }

        return requested;
    }

    public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
    {
        if (count <= 0 || startLine >= _index.LineCount)
            yield break;

        if (startLine < 0)
            throw new ArgumentOutOfRangeException(nameof(startLine));

        int endLine = Math.Min(_index.LineCount, startLine + count);
        int currentLine = startLine;
        foreach (string value in ReadLinesFromFile(startLine, endLine - startLine))
        {
            if (cache)
                _cache.Add(currentLine, value);
            yield return value;
            currentLine++;
        }
    }

    private IEnumerable<string> ReadLinesFromFile(int startLine, int count)
    {
        if (count <= 0)
            yield break;

        int endLine = Math.Min(_index.LineCount, startLine + count);
        var (checkpointLine, offset) = _index.GetCheckpointForLine(startLine);

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 20, FileOptions.SequentialScan);
        stream.Seek(offset, SeekOrigin.Begin);

        // Checkpoints are always at UTF-8 line starts, so BOM detection is disabled here.
        using var reader = new StreamReader(stream, _encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 20);
        int currentLine = checkpointLine;
        while (currentLine < startLine)
        {
            if (reader.ReadLine() == null)
                break;
            currentLine++;
        }

        while (currentLine < endLine)
        {
            string? line = reader.ReadLine();
            if (line == null)
                line = "";

            yield return TextBuffer.ExpandTabs(line, _tabSize);
            currentLine++;
        }
    }

    public int GetMaxLineLength(int startLine, int count)
    {
        // The index tracks a file-wide maximum without per-line materialization.
        // This is an upper bound for partial ranges, which keeps scroll extent
        // calculation cheap for file-backed pieces.
        return count <= 0 ? 0 : _index.MaxLineLength;
    }

    public long GetCharCountWithoutLineEndings(int startLine, int count)
    {
        if (count <= 0)
            return 0;

        if (startLine == 0 && count >= _index.LineCount)
            return _index.CharCountWithoutLineEndings;

        int endLine = Math.Min(_index.LineCount, startLine + count);
        return GetCharCountBeforeLine(endLine) - GetCharCountBeforeLine(startLine);
    }

    private long GetCharCountBeforeLine(int line)
    {
        if (line <= 0)
            return 0;
        if (line >= _index.LineCount)
            return _index.CharCountWithoutLineEndings;

        var (checkpointLine, _, checkpointCharCount) = _index.GetCheckpointSummaryForLine(line);
        long total = checkpointCharCount;
        int scanCount = line - checkpointLine;
        foreach (string value in ReadLinesFromFile(checkpointLine, scanCount))
            total += value.Length;
        return total;
    }

    private long GetByteOffsetForLine(int line, CancellationToken cancellationToken)
    {
        if (line <= 0)
            return _index.ContentStartOffset;

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 20, FileOptions.SequentialScan);
        if (line >= _index.LineCount)
            return stream.Length;

        var (checkpointLine, offset) = _index.GetCheckpointForLine(line);
        if (checkpointLine == line)
            return offset;

        stream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[1 << 20];
        int currentLine = checkpointLine;
        long absoluteOffset = offset;

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;

                currentLine++;
                if (currentLine == line)
                    return absoluteOffset + i + 1;
            }

            absoluteOffset += read;
        }

        return stream.Length;
    }

    internal static long CountLiteralMatchesInFile(
        string path,
        long startOffset,
        byte[] needle,
        CancellationToken cancellationToken,
        int chunkByteCount = LiteralSearchChunkByteCount,
        Action<FastLiteralMatchProgress>? progress = null)
    {
        long endOffset = new FileInfo(path).Length;
        return CountLiteralMatchesInFile(path, startOffset, endOffset, needle, matchCase: true, wholeWord: false,
            cancellationToken, chunkByteCount, progress);
    }

    internal static long CountLiteralMatchesInFile(
        string path,
        long startOffset,
        long endOffset,
        byte[] needle,
        bool matchCase,
        bool wholeWord,
        CancellationToken cancellationToken,
        int chunkByteCount = LiteralSearchChunkByteCount,
        Action<FastLiteralMatchProgress>? progress = null)
    {
        if (needle.Length == 0)
            return 0;

        int overlapByteCount = wholeWord ? needle.Length + 1 : needle.Length - 1;
        chunkByteCount = Math.Max(chunkByteCount, needle.Length + (wholeWord ? 1 : 0));
        byte[] buffer = new byte[chunkByteCount + overlapByteCount];
        int carry = 0;
        long count = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 20, FileOptions.SequentialScan);
        long rangeStart = Math.Clamp(startOffset, 0, stream.Length);
        long rangeEnd = Math.Clamp(endOffset, rangeStart, stream.Length);
        stream.Seek(rangeStart, SeekOrigin.Begin);
        long totalBytes = rangeEnd - rangeStart;
        long bytesRead = 0;

        while (bytesRead < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int targetRead = (int)Math.Min(chunkByteCount, totalBytes - bytesRead);
            int read = stream.Read(buffer, carry, targetRead);
            if (read == 0)
                break;

            int length = carry + read;
            int minStart = wholeWord && carry > 0 ? 1 : 0;
            bytesRead += read;
            bool finalChunk = bytesRead >= totalBytes;
            count += CountLiteralMatches(buffer.AsSpan(0, length), needle, matchCase, wholeWord, finalChunk, minStart);
            progress?.Invoke(new FastLiteralMatchProgress(bytesRead, totalBytes, count));

            carry = finalChunk ? 0 : Math.Min(overlapByteCount, length);
            if (carry > 0)
                Buffer.BlockCopy(buffer, length - carry, buffer, 0, carry);
        }

        return count;
    }

    private static long CountLiteralMatches(ReadOnlySpan<byte> text, ReadOnlySpan<byte> needle, bool matchCase,
        bool wholeWord, bool finalChunk, int minStart)
    {
        if (text.Length < needle.Length)
            return 0;

        int maxStart = text.Length - needle.Length;
        if (wholeWord && !finalChunk)
            maxStart--;
        if (maxStart < minStart)
            return 0;

        return matchCase
            ? CountCaseSensitiveLiteralMatches(text, needle, wholeWord, minStart, maxStart)
            : CountAsciiIgnoreCaseLiteralMatches(text, needle, wholeWord, minStart, maxStart);
    }

    private static long CountCaseSensitiveLiteralMatches(ReadOnlySpan<byte> text, ReadOnlySpan<byte> needle,
        bool wholeWord, int minStart, int maxStart)
    {
        ReadOnlySpan<byte> searchable = text[..(maxStart + needle.Length)];
        long count = 0;
        int offset = minStart;
        while (offset <= maxStart)
        {
            int found = searchable[offset..].IndexOf(needle);
            if (found < 0)
                break;

            int matchStart = offset + found;
            if (!wholeWord || HasAsciiWordBoundaries(text, matchStart, needle.Length))
                count++;

            offset = matchStart + 1;
        }

        return count;
    }

    private static long CountAsciiIgnoreCaseLiteralMatches(ReadOnlySpan<byte> text, ReadOnlySpan<byte> needle,
        bool wholeWord, int minStart, int maxStart)
    {
        long count = 0;
        int offset = minStart;
        byte first = ToLowerAscii(needle[0]);
        while (offset <= maxStart)
        {
            int found = IndexOfAsciiByteIgnoreCase(text.Slice(offset, maxStart - offset + 1), first);
            if (found < 0)
                break;

            int matchStart = offset + found;
            if (AsciiEqualsIgnoreCase(text.Slice(matchStart, needle.Length), needle) &&
                (!wholeWord || HasAsciiWordBoundaries(text, matchStart, needle.Length)))
            {
                count++;
            }

            offset = matchStart + 1;
        }

        return count;
    }

    private static int IndexOfAsciiByteIgnoreCase(ReadOnlySpan<byte> text, byte lower)
    {
        byte upper = ToUpperAscii(lower);
        return upper == lower ? text.IndexOf(lower) : text.IndexOfAny(lower, upper);
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i < needle.Length; i++)
        {
            if (ToLowerAscii(text[i]) != ToLowerAscii(needle[i]))
                return false;
        }

        return true;
    }

    private static bool HasAsciiWordBoundaries(ReadOnlySpan<byte> text, int start, int length)
    {
        bool beforeIsWord = start > 0 && IsAsciiWordByte(text[start - 1]);
        int after = start + length;
        bool afterIsWord = after < text.Length && IsAsciiWordByte(text[after]);
        return !beforeIsWord && !afterIsWord;
    }

    private static bool IsAsciiWordByte(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ||
        value is >= (byte)'a' and <= (byte)'z' ||
        value is >= (byte)'0' and <= (byte)'9' ||
        value == (byte)'_';

    private static byte ToLowerAscii(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private static byte ToUpperAscii(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

    private static bool IsAscii(string text)
    {
        foreach (char ch in text)
        {
            if (ch > 0x7F)
                return false;
        }

        return true;
    }

    private static bool ContainsLineBreak(string text) =>
        text.AsSpan().IndexOfAny('\r', '\n') >= 0;

    private static bool ContainsSurrogate(string text)
    {
        foreach (char ch in text)
        {
            if (char.IsSurrogate(ch))
                return true;
        }

        return false;
    }
}

internal sealed class LargeFileLineIndex
{
    public const int CheckpointInterval = 4096;
    internal const int ReadBufferSize = 8 * 1024 * 1024;

    private readonly List<long> _checkpoints;
    private readonly List<long> _checkpointCharCounts;
    private readonly int _bomLength;

    private LargeFileLineIndex(
        List<long> checkpoints,
        List<long> checkpointCharCounts,
        int bomLength,
        int lineCount,
        long charCountWithoutLineEndings,
        int maxLineLength,
        string lineEnding,
        bool hasTabs,
        bool hasNonAscii)
    {
        _checkpoints = checkpoints;
        _checkpointCharCounts = checkpointCharCounts;
        _bomLength = bomLength;
        LineCount = lineCount;
        CharCountWithoutLineEndings = charCountWithoutLineEndings;
        MaxLineLength = maxLineLength;
        LineEnding = lineEnding;
        HasTabs = hasTabs;
        HasNonAscii = hasNonAscii;
    }

    public int LineCount { get; }
    public long CharCountWithoutLineEndings { get; }
    public int MaxLineLength { get; }
    public string LineEnding { get; }
    public bool HasTabs { get; }
    public bool HasNonAscii { get; }
    public long ContentStartOffset => _bomLength;

    public static LargeFileLineIndex Build(string path, Encoding encoding,
        CancellationToken cancellationToken = default) =>
        Build(path, encoding, progress: null, cancellationToken);

    internal static LargeFileLineIndex Build(
        string path,
        Encoding encoding,
        IProgress<FileLoadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!FileTextSource.SupportsEncoding(encoding))
            throw new NotSupportedException("File-backed indexing currently supports UTF-8 text.");

        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: ReadBufferSize, FileOptions.SequentialScan);
        long totalBytes = stream.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            int read = stream.Read(buffer, 0, ReadBufferSize);
            int bomLength = DetectUtf8BomLength(buffer.AsSpan(0, read));
            var checkpoints = new List<long> { bomLength };
            var checkpointCharCounts = new List<long> { 0 };

            long lineCount = 1;
            long currentLineLength = 0;
            long charCountWithoutLineEndings = 0;
            int maxLineLength = 0;
            int lfCount = 0;
            int crlfCount = 0;
            bool hasTabs = false;
            bool hasNonAscii = false;
            bool previousWasCr = false;
            long absoluteOffset = 0;

            progress?.Report(FileLoadProgress.ForBytes("Indexing file", bomLength, totalBytes));
            while (read > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int contentStart = absoluteOffset == 0 ? bomLength : 0;
                var span = buffer.AsSpan(contentStart, read - contentStart);
                if (!hasNonAscii && ContainsNonAscii(span))
                    hasNonAscii = true;

                int position = 0;
                while (position < span.Length)
                {
                    int relativeIndex = span[position..].IndexOfAny((byte)'\n', (byte)'\t');
                    if (relativeIndex < 0)
                        break;

                    int index = position + relativeIndex;
                    byte value = span[index];
                    if (value == (byte)'\t')
                    {
                        hasTabs = true;
                        currentLineLength += index - position + 1L;
                        previousWasCr = false;
                        position = index + 1;
                        continue;
                    }

                    long lineLength = currentLineLength + index - position;
                    bool isCrLf = index > position
                        ? span[index - 1] == (byte)'\r'
                        : previousWasCr;
                    long contentLength = isCrLf ? Math.Max(0, lineLength - 1) : lineLength;
                    charCountWithoutLineEndings += contentLength;
                    maxLineLength = (int)Math.Max(maxLineLength, Math.Min(contentLength, int.MaxValue));
                    if (isCrLf)
                        crlfCount++;
                    else
                        lfCount++;

                    long nextLineIndex = lineCount;
                    lineCount++;
                    if (nextLineIndex % CheckpointInterval == 0)
                    {
                        checkpoints.Add(absoluteOffset + contentStart + index + 1);
                        checkpointCharCounts.Add(charCountWithoutLineEndings);
                    }

                    currentLineLength = 0;
                    previousWasCr = false;
                    position = index + 1;
                }

                int remaining = span.Length - position;
                if (remaining > 0)
                {
                    currentLineLength += remaining;
                    previousWasCr = span[^1] == (byte)'\r';
                }

                absoluteOffset += read;
                progress?.Report(FileLoadProgress.ForBytes("Indexing file", absoluteOffset, totalBytes));
                read = stream.Read(buffer, 0, ReadBufferSize);
            }

            cancellationToken.ThrowIfCancellationRequested();
            charCountWithoutLineEndings += currentLineLength;
            maxLineLength = (int)Math.Max(maxLineLength, Math.Min(currentLineLength, int.MaxValue));

            if (lineCount > int.MaxValue)
                throw new NotSupportedException($"Volt indexed {lineCount:N0} lines, which exceeds the current editor line-addressing limit.");

            string lineEnding = lfCount > crlfCount ? "\n" : "\r\n";
            return new LargeFileLineIndex(
                checkpoints,
                checkpointCharCounts,
                bomLength,
                (int)lineCount,
                charCountWithoutLineEndings,
                maxLineLength,
                lineEnding,
                hasTabs,
                hasNonAscii);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public (int line, long offset) GetCheckpointForLine(int line)
    {
        int checkpointIndex = Math.Clamp(line / CheckpointInterval, 0, _checkpoints.Count - 1);
        return (checkpointIndex * CheckpointInterval, _checkpoints[checkpointIndex]);
    }

    public (int line, long offset, long charCount) GetCheckpointSummaryForLine(int line)
    {
        int checkpointIndex = Math.Clamp(line / CheckpointInterval, 0, _checkpoints.Count - 1);
        return (checkpointIndex * CheckpointInterval, _checkpoints[checkpointIndex],
            _checkpointCharCounts[checkpointIndex]);
    }

    private static bool ContainsNonAscii(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return false;

        if (Vector.IsHardwareAccelerated && span.Length >= Vector<byte>.Count)
        {
            var highBit = new Vector<byte>(0x80);
            int vectorWidth = Vector<byte>.Count;
            int vectorEnd = span.Length - vectorWidth;
            int i = 0;
            for (; i <= vectorEnd; i += vectorWidth)
            {
                var value = new Vector<byte>(span.Slice(i, vectorWidth));
                if (!Vector.EqualsAll(Vector.BitwiseAnd(value, highBit), Vector<byte>.Zero))
                    return true;
            }

            span = span[i..];
        }

        foreach (byte value in span)
        {
            if (value >= 0x80)
                return true;
        }

        return false;
    }

    private static int DetectUtf8BomLength(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
    }
}

internal sealed class TextPageCache
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<int, string> _lines = new();
    private readonly Queue<int> _order = new();

    public TextPageCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public bool TryGet(int line, out string? text)
    {
        lock (_gate)
        {
            return _lines.TryGetValue(line, out text);
        }
    }

    public void Add(int line, string text)
    {
        lock (_gate)
        {
            if (_lines.ContainsKey(line))
            {
                _lines[line] = text;
                return;
            }

            _lines.Add(line, text);
            _order.Enqueue(line);

            while (_lines.Count > _capacity && _order.Count > 0)
            {
                int evict = _order.Dequeue();
                _lines.Remove(evict);
            }
        }
    }
}
