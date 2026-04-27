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
        SearchToCompletion("var", matchCase: false);
    }

    [Benchmark(Description = "Search rare term (50K lines)")]
    public void SearchRare()
    {
        SearchToCompletion("49999", matchCase: false);
    }

    [Benchmark(Description = "Search case-sensitive (50K lines)")]
    public void SearchCaseSensitive()
    {
        SearchToCompletion("hello", matchCase: true);
    }

    private void SearchToCompletion(string query, bool matchCase)
    {
        _find.StartSearch(_buffer, new FindQuery(query, matchCase), 0, 0);
        while (_find.RunNextBatch()) { }
    }
}
