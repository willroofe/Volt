using BenchmarkDotNet.Attributes;
using TextEdit;

namespace TextEdit.Benchmarks;

[MemoryDiagnoser]
public class TextBufferBenchmarks
{
    private TextBuffer _smallBuffer = null!;
    private TextBuffer _largeBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallBuffer = new TextBuffer();
        _smallBuffer.SetContent(
            string.Join("\n", Enumerable.Range(0, 1_000).Select(i => $"line {i} with some content here")),
            4);

        _largeBuffer = new TextBuffer();
        _largeBuffer.SetContent(
            string.Join("\n", Enumerable.Range(0, 100_000).Select(i => $"line {i} with some content here")),
            4);
    }

    [Benchmark(Description = "MaxLineLength 1K lines")]
    public int MaxLineLength1K()
    {
        _smallBuffer.InvalidateMaxLineLength();
        return _smallBuffer.MaxLineLength;
    }

    [Benchmark(Description = "MaxLineLength 100K lines")]
    public int MaxLineLength100K()
    {
        _largeBuffer.InvalidateMaxLineLength();
        return _largeBuffer.MaxLineLength;
    }

    [Benchmark(Description = "InsertAt mid-line (80 char line)")]
    public void InsertAtMidLine()
    {
        // Reset the line before each insert to keep it stable
        _smallBuffer[500] = "0123456789012345678901234567890123456789012345678901234567890123456789012345678X";
        _smallBuffer.InsertAt(500, 40, "a");
    }

    [Benchmark(Description = "InsertAt mid-line (10K char line)")]
    public void InsertAtLongLine()
    {
        _smallBuffer[500] = new string('X', 10_000);
        _smallBuffer.InsertAt(500, 5000, "a");
    }
}
