using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

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

    private static readonly string Line80 = "0123456789012345678901234567890123456789012345678901234567890123456789012345678X";
    private static readonly string Line10K = new('X', 10_000);

    [IterationSetup(Target = nameof(InsertAtMidLine))]
    public void ResetMidLine() => _smallBuffer[500] = Line80;

    [IterationSetup(Target = nameof(InsertAtLongLine))]
    public void ResetLongLine() => _smallBuffer[500] = Line10K;

    [Benchmark(Description = "InsertAt mid-line (80 char line)")]
    public void InsertAtMidLine()
    {
        _smallBuffer.InsertAt(500, 40, "a");
    }

    [Benchmark(Description = "InsertAt mid-line (10K char line)")]
    public void InsertAtLongLine()
    {
        _smallBuffer.InsertAt(500, 5000, "a");
    }
}
