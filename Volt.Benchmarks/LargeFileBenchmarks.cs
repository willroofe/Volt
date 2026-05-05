using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

[MemoryDiagnoser]
public class LargeFileBenchmarks
{
    private string? _path;
    private TextBuffer _buffer = null!;
    private Encoding _encoding = null!;
    private readonly Random _random = new(1234);
    private int[] _sampleLines = [];

    [GlobalSetup]
    public void Setup()
    {
        _path = Environment.GetEnvironmentVariable("VOLT_LARGE_FILE_BENCH_PATH");
        _encoding = new UTF8Encoding(false);

        string? path = _path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _buffer = new TextBuffer();
            _buffer.SetContent("set VOLT_LARGE_FILE_BENCH_PATH to run large-file benchmarks", tabSize: 4);
            _sampleLines = [0];
            return;
        }

        _buffer = new TextBuffer();
        _buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, _encoding, tabSize: 4));
        _sampleLines = Enumerable.Range(0, Math.Min(128, _buffer.Count))
            .Select(_ => _random.Next(0, _buffer.Count))
            .ToArray();
    }

    [Benchmark(Description = "Large file exact index build")]
    public int IndexBuild()
    {
        string? path = _path;
        if (path == null || !File.Exists(path))
            return 0;

        var buffer = new TextBuffer();
        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, _encoding, tabSize: 4));
        return buffer.Count;
    }

    [Benchmark(Description = "Large file random line reads")]
    public long RandomLineReads()
    {
        long total = 0;
        foreach (int line in _sampleLines)
            total += _buffer[line].Length;
        return total;
    }

    [Benchmark(Description = "Large file small edit overlay")]
    public int SmallEditOverlay()
    {
        var copy = new TextBuffer();
        string? path = _path;
        if (path != null && File.Exists(path))
            copy.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, _encoding, tabSize: 4));
        else
            copy.SetContent("alpha\nbeta\ngamma", tabSize: 4);

        int line = Math.Min(copy.Count - 1, Math.Max(0, copy.Count / 2));
        copy.InsertAt(line, 0, "x");
        return copy[line].Length;
    }

    [Benchmark(Description = "Large file safe save rewrite")]
    public long SafeSaveRewrite()
    {
        string? path = _path;
        if (path == null || !File.Exists(path))
            return 0;

        string tempPath = Path.Combine(Path.GetTempPath(), "Volt.Benchmarks." + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var copy = new TextBuffer();
            copy.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, _encoding, tabSize: 4));
            copy.InsertAt(0, 0, "x");
            copy.SaveToFile(tempPath, _encoding, tabSize: 4);
            return new FileInfo(tempPath).Length;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }
}
