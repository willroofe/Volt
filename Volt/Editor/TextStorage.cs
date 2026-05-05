using System.IO;
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
    IEnumerable<string> EnumerateLines(int startLine, int count);
    int GetMaxLineLength(int startLine, int count);
    long GetCharCountWithoutLineEndings(int startLine, int count);
}

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

    public IEnumerable<string> EnumerateLines(int startLine, int count)
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

    public IEnumerable<string> EnumerateLines(int startLine, int count)
    {
        foreach (string line in Inner.EnumerateLines(startLine, count))
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

    public IEnumerable<string> EnumerateLines(int startLine, int count)
    {
        foreach (string line in Inner.EnumerateLines(startLine, count))
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

    public IEnumerable<string> EnumerateLines(int startLine, int count)
    {
        foreach (string line in Inner.EnumerateLines(startLine, count))
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

internal sealed class FileTextSource : ITextSource
{
    private const int CachePageLineCount = 512;

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

    public IEnumerable<string> EnumerateLines(int startLine, int count)
    {
        if (count <= 0 || startLine >= _index.LineCount)
            yield break;

        if (startLine < 0)
            throw new ArgumentOutOfRangeException(nameof(startLine));

        int endLine = Math.Min(_index.LineCount, startLine + count);
        int currentLine = startLine;
        foreach (string value in ReadLinesFromFile(startLine, endLine - startLine))
        {
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
}

internal sealed class LargeFileLineIndex
{
    public const int CheckpointInterval = 4096;

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
        string lineEnding)
    {
        _checkpoints = checkpoints;
        _checkpointCharCounts = checkpointCharCounts;
        _bomLength = bomLength;
        LineCount = lineCount;
        CharCountWithoutLineEndings = charCountWithoutLineEndings;
        MaxLineLength = maxLineLength;
        LineEnding = lineEnding;
    }

    public int LineCount { get; }
    public long CharCountWithoutLineEndings { get; }
    public int MaxLineLength { get; }
    public string LineEnding { get; }

    public static LargeFileLineIndex Build(string path, Encoding encoding)
    {
        if (!FileTextSource.SupportsEncoding(encoding))
            throw new NotSupportedException("File-backed indexing currently supports UTF-8 text.");

        int bomLength = DetectUtf8BomLength(path);
        var checkpoints = new List<long> { bomLength };
        var checkpointCharCounts = new List<long> { 0 };

        long lineCount = 1;
        long currentLineLength = 0;
        long charCountWithoutLineEndings = 0;
        int maxLineLength = 0;
        int lfCount = 0;
        int crlfCount = 0;
        bool previousWasCr = false;
        long absoluteOffset = bomLength;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 20, FileOptions.SequentialScan);
        stream.Seek(bomLength, SeekOrigin.Begin);

        byte[] buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte b = buffer[i];
                if (b == (byte)'\n')
                {
                    long contentLength = previousWasCr ? Math.Max(0, currentLineLength - 1) : currentLineLength;
                    charCountWithoutLineEndings += contentLength;
                    maxLineLength = (int)Math.Max(maxLineLength, Math.Min(contentLength, int.MaxValue));
                    if (previousWasCr)
                        crlfCount++;
                    else
                        lfCount++;

                    long nextLineIndex = lineCount;
                    lineCount++;
                    if (nextLineIndex % CheckpointInterval == 0)
                    {
                        checkpoints.Add(absoluteOffset + i + 1);
                        checkpointCharCounts.Add(charCountWithoutLineEndings);
                    }

                    currentLineLength = 0;
                    previousWasCr = false;
                    continue;
                }

                currentLineLength++;
                previousWasCr = b == (byte)'\r';
            }

            absoluteOffset += read;
        }

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
            lineEnding);
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

    private static int DetectUtf8BomLength(string path)
    {
        Span<byte> bom = stackalloc byte[3];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        int read = stream.Read(bom);
        return read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF ? 3 : 0;
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
