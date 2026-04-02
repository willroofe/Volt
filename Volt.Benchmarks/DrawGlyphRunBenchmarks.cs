using System.Windows;
using System.Windows.Media;
using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

/// <summary>
/// Measures the allocation cost of DrawGlyphRun's array creation.
/// Uses a mock DrawingContext since we can't render in a benchmark,
/// but isolates the glyph index lookup + array allocation which is the target.
/// </summary>
[MemoryDiagnoser]
public class DrawGlyphRunBenchmarks
{
    private FontManager _font = null!;
    private string _shortText = null!;
    private string _mediumText = null!;
    private string _longText = null!;
    private DrawingVisual _visual = null!;

    [GlobalSetup]
    public void Setup()
    {
        _font = new FontManager();
        _shortText = "my $foo = 42;";        // 13 chars — typical token
        _mediumText = new string('X', 80);     // 80 chars — full line
        _longText = new string('X', 500);      // 500 chars — long line in one call

        _visual = new DrawingVisual();
    }

    [Benchmark(Description = "DrawGlyphRun 13 chars (token)")]
    public void DrawShort()
    {
        using var dc = _visual.RenderOpen();
        _font.DrawGlyphRun(dc, _shortText, 0, _shortText.Length, 0, 0, Brushes.White);
    }

    [Benchmark(Description = "DrawGlyphRun 80 chars (line)")]
    public void DrawMedium()
    {
        using var dc = _visual.RenderOpen();
        _font.DrawGlyphRun(dc, _mediumText, 0, _mediumText.Length, 0, 0, Brushes.White);
    }

    [Benchmark(Description = "DrawGlyphRun 500 chars (long line)")]
    public void DrawLong()
    {
        using var dc = _visual.RenderOpen();
        _font.DrawGlyphRun(dc, _longText, 0, _longText.Length, 0, 0, Brushes.White);
    }

    [Benchmark(Description = "Simulate render frame: 80 lines x 5 tokens")]
    public void SimulateRenderFrame()
    {
        using var dc = _visual.RenderOpen();
        for (int line = 0; line < 80; line++)
        {
            // Simulate 5 tokens per line, avg 16 chars each
            for (int t = 0; t < 5; t++)
            {
                _font.DrawGlyphRun(dc, _mediumText, 0, 16, t * 16.0 * _font.CharWidth, line * _font.LineHeight, Brushes.White);
            }
        }
    }
}
