using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

[MemoryDiagnoser]
public class JsonLanguageBenchmarks
{
    private JsonLanguageService _service = null!;
    private string _smallDocument = null!;
    private string _largeArray = null!;

    [GlobalSetup]
    public void Setup()
    {
        _service = new JsonLanguageService();
        _smallDocument = """
        {
          "name": "Volt",
          "enabled": true,
          "theme": "dark",
          "recent": ["a.cs", "b.json", "c.md"]
        }
        """;
        _largeArray = "[" + string.Join(",", Enumerable.Range(0, 1000)
            .Select(i => $$"""{ "id": {{i}}, "name": "item-{{i}}", "enabled": {{(i % 2 == 0 ? "true" : "false")}} }""")) + "]";
    }

    [Benchmark(Description = "JSON analyze small document")]
    public LanguageSnapshot AnalyzeSmallDocument() => _service.Analyze(_smallDocument, sourceVersion: 1);

    [Benchmark(Description = "JSON analyze large array")]
    public LanguageSnapshot AnalyzeLargeArray() => _service.Analyze(_largeArray, sourceVersion: 1);
}
