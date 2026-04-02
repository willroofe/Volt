using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

[MemoryDiagnoser]
public class FindBenchmarks
{
    private TextBuffer _buffer = null!;
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
}
