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
            _largeFileBuffer = new TextBuffer();
            _largeFileBuffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(
                largeFilePath,
                new UTF8Encoding(false),
                tabSize: 4));
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

        var find = new FindManager();
        find.StartSearch(_largeFileBuffer, "test", matchCase: true, caretLine: 0, caretCol: 0);
        await WaitForExactCountAsync(find).ConfigureAwait(false);

        long count = find.KnownMatchCount;
        if (count != ExpectedTest1GbMatchCount)
            throw new InvalidOperationException(
                $"Expected {ExpectedTest1GbMatchCount:N0} matches in test-1gb.json, but found {count:N0}.");

        return count;
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
}
