using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

[MemoryDiagnoser]
public class VtParserBenchmarks
{
    private byte[] _plainAscii = Array.Empty<byte>();
    private byte[] _sgrHeavy = Array.Empty<byte>();

    private sealed class NullHandler : IVtEventHandler
    {
        public void Print(char ch) { }
        public void Execute(byte ctrl) { }
        public void CsiDispatch(char final, ReadOnlySpan<int> p, ReadOnlySpan<char> i) { }
        public void EscDispatch(char final, ReadOnlySpan<char> i) { }
        public void OscDispatch(int cmd, string data) { }
    }

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 20_000; i++) sb.AppendLine("The quick brown fox jumps over the lazy dog");
        _plainAscii = Encoding.ASCII.GetBytes(sb.ToString());

        sb.Clear();
        for (int i = 0; i < 5_000; i++)
            sb.Append("\u001b[31mred \u001b[1;32mbold green \u001b[0;34mblue \u001b[0m normal ");
        _sgrHeavy = Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Benchmark(Description = "Parse plain ASCII (~1 MB)")]
    public void ParsePlainAscii()
    {
        var sm = new VtStateMachine(new NullHandler());
        sm.Feed(_plainAscii);
    }

    [Benchmark(Description = "Parse SGR-heavy stream")]
    public void ParseSgrHeavy()
    {
        var sm = new VtStateMachine(new NullHandler());
        sm.Feed(_sgrHeavy);
    }
}
