using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

[MemoryDiagnoser]
public class TokenizeBenchmarks
{
    private SyntaxManager _mgr = null!;
    private SyntaxDefinition _grammar = null!;
    private string _simpleLine = null!;
    private string _complexLine = null!;
    private string _longLine = null!;
    private LineState _defaultState = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mgr = new SyntaxManager();
        _mgr.Initialize();
        _grammar = _mgr.GetDefinition(".pl")!;
        _defaultState = _mgr.DefaultState;

        _simpleLine = "    my $foo = 42;  # comment";
        _complexLine = "    my $foo = Bar::Baz->new({ key => $val, other => \"hello $world\" });  # comment";
        _longLine = string.Concat(Enumerable.Range(0, 50).Select(i => $"my $v{i} = {i}; "));
    }

    [Benchmark(Description = "Tokenize simple line")]
    public List<SyntaxToken> TokenizeSimple()
        => _mgr.Tokenize(_simpleLine, _grammar, _defaultState, out _);

    [Benchmark(Description = "Tokenize complex line")]
    public List<SyntaxToken> TokenizeComplex()
        => _mgr.Tokenize(_complexLine, _grammar, _defaultState, out _);

    [Benchmark(Description = "Tokenize long line (~500 chars)")]
    public List<SyntaxToken> TokenizeLong()
        => _mgr.Tokenize(_longLine, _grammar, _defaultState, out _);
}
