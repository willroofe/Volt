using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

[MemoryDiagnoser]
public class TerminalGridBenchmarks
{
    private TerminalGrid _grid = null!;

    [GlobalSetup]
    public void Setup() => _grid = new TerminalGrid(24, 80, 10_000);

    [Benchmark(Description = "WriteCell 1M sequential")]
    public void WriteCellSequential()
    {
        for (int i = 0; i < 1_000_000; i++)
            _grid.WriteCell(i % 24, i % 80, 'x', CellAttr.None);
    }

    [Benchmark(Description = "ScrollUp 1 row x1000 (80x24)")]
    public void ScrollUp1Row()
    {
        for (int i = 0; i < 1_000; i++)
            _grid.ScrollUp(1);
    }

    [Benchmark(Description = "Resize 24x80 → 50x200 → 24x80")]
    public void ResizeLarge()
    {
        var g = new TerminalGrid(24, 80, 10_000);
        g.Resize(50, 200);
        g.Resize(24, 80);
    }
}
