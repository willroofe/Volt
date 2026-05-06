using BenchmarkDotNet.Attributes;
using System.IO;
using System.Text;
using Volt;

namespace Volt.Benchmarks;

[MemoryDiagnoser]
public class FindBenchmarks
{
    private const long ExpectedTest1GbMatchCount = 3_146_467;

    private TextBuffer _buffer = null!;
    private TextBuffer? _largeFileBuffer;
    private TextBuffer? _largeFileMixedBuffer;
    private (int startLine, int startCol, int endLine, int endCol) _largeFileSelection;
    private FindManager _find = null!;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new TextBuffer();
        _buffer.SetContent(
            string.Join("\n", Enumerable.Range(0, 50_000).Select(i =>
                $"my $var_{i} = \"hello world {i}\";  # comment {i}")),
            4);
        _find = new FindManager();

        string largeFilePath = ResolveLargeFilePath();
        if (File.Exists(largeFilePath))
        {
            WarmFileCache(largeFilePath);
            TextBuffer.PreparedContent prepared = TextBuffer.PrepareContentFromFile(
                largeFilePath,
                new UTF8Encoding(false),
                tabSize: 4);

            _largeFileBuffer = new TextBuffer();
            _largeFileBuffer.SetPreparedContent(prepared);

            _largeFileMixedBuffer = new TextBuffer();
            _largeFileMixedBuffer.SetPreparedContent(prepared);
            _largeFileMixedBuffer.InsertLine(1, "test TEST edit");

            int startLine = Math.Clamp(_largeFileBuffer.Count / 4, 0, Math.Max(0, _largeFileBuffer.Count - 1));
            int endLine = Math.Clamp(startLine + Math.Max(1, _largeFileBuffer.Count / 2),
                startLine,
                Math.Max(0, _largeFileBuffer.Count - 1));
            _largeFileSelection = (startLine, 0, endLine, _largeFileBuffer.GetLineLength(endLine));
        }
    }

    [Benchmark(Description = "Search common term (50K lines)")]
    public void SearchCommon()
    {
        _find.Search(_buffer, "var", false, 0, 0);
    }

    [Benchmark(Description = "Search rare term (50K lines)")]
    public void SearchRare()
    {
        _find.Search(_buffer, "49999", false, 0, 0);
    }

    [Benchmark(Description = "Search case-sensitive (50K lines)")]
    public void SearchCaseSensitive()
    {
        _find.Search(_buffer, "hello", true, 0, 0);
    }

    [Benchmark(Description = "Large file literal exact count (test-1gb.json, 'test')")]
    public async Task<long> SearchLargeFileLiteralExactCount()
    {
        if (_largeFileBuffer == null)
            return 0;

        long count = await CountLargeFileMatchesAsync(_largeFileBuffer, "test", matchCase: true)
            .ConfigureAwait(false);
        if (count != ExpectedTest1GbMatchCount)
            throw new InvalidOperationException(
                $"Expected {ExpectedTest1GbMatchCount:N0} matches in test-1gb.json, but found {count:N0}.");

        return count;
    }

    [Benchmark(Description = "Large file ASCII case-insensitive count (test-1gb.json, 'TEST')")]
    public async Task<long> SearchLargeFileAsciiCaseInsensitiveExactCount()
    {
        if (_largeFileBuffer == null)
            return 0;

        return await CountLargeFileMatchesAsync(_largeFileBuffer, "TEST", matchCase: false)
            .ConfigureAwait(false);
    }

    [Benchmark(Description = "Large file ASCII whole-word count (test-1gb.json, 'test')")]
    public async Task<long> SearchLargeFileWholeWordExactCount()
    {
        if (_largeFileBuffer == null)
            return 0;

        return await CountLargeFileMatchesAsync(_largeFileBuffer, "test", matchCase: true, wholeWord: true)
            .ConfigureAwait(false);
    }

    [Benchmark(Description = "Large file selected line-range count (test-1gb.json, 'test')")]
    public async Task<long> SearchLargeFileSelectedLineRangeExactCount()
    {
        if (_largeFileBuffer == null)
            return 0;

        return await CountLargeFileMatchesAsync(_largeFileBuffer, "test", matchCase: true,
                selectionBounds: _largeFileSelection)
            .ConfigureAwait(false);
    }

    [Benchmark(Description = "Large file mixed edit overlay count (test-1gb.json + one edit, 'test')")]
    public async Task<long> SearchLargeFileMixedEditOverlayExactCount()
    {
        if (_largeFileMixedBuffer == null)
            return 0;

        return await CountLargeFileMatchesAsync(_largeFileMixedBuffer, "test", matchCase: false)
            .ConfigureAwait(false);
    }

    private static string ResolveLargeFilePath()
    {
        string envPath = Environment.GetEnvironmentVariable("VOLT_FIND_1GB_BENCH_PATH") ?? "";
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        string currentDirectoryPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "test-files", "test-1gb.json"));
        if (File.Exists(currentDirectoryPath))
            return currentDirectoryPath;

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "test-files", "test-1gb.json"));
    }

    private static void WarmFileCache(string path)
    {
        byte[] buffer = new byte[4 * 1024 * 1024];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 20, FileOptions.SequentialScan);
        while (stream.Read(buffer, 0, buffer.Length) > 0)
        {
        }
    }

    private static async Task WaitForExactCountAsync(FindManager find)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!find.HasExactMatchCount)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(1, cts.Token).ConfigureAwait(false);
        }
    }

    private static async Task<long> CountLargeFileMatchesAsync(
        TextBuffer buffer,
        string query,
        bool matchCase,
        bool wholeWord = false,
        (int startLine, int startCol, int endLine, int endCol)? selectionBounds = null)
    {
        var find = new FindManager();
        find.StartSearch(buffer, query, matchCase, caretLine: 0, caretCol: 0, wholeWord: wholeWord,
            selectionBounds: selectionBounds);
        await WaitForExactCountAsync(find).ConfigureAwait(false);
        return find.KnownMatchCount;
    }
}
