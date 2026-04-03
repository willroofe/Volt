using Xunit;
using Volt;

namespace Volt.Tests;

public class SyntaxManagerTests
{
    private static SyntaxManager CreateInitialized()
    {
        var mgr = new SyntaxManager();
        mgr.Initialize();
        return mgr;
    }

    [Fact]
    public void GetDefinition_KnownExtension_ReturnsGrammar()
    {
        var mgr = CreateInitialized();

        var def = mgr.GetDefinition(".pl");

        Assert.NotNull(def);
        Assert.Equal("Perl", def!.Name);
    }

    [Fact]
    public void GetDefinition_UnknownExtension_ReturnsNull()
    {
        var mgr = CreateInitialized();

        Assert.Null(mgr.GetDefinition(".xyz"));
    }

    [Fact]
    public void GetDefinition_NullOrEmpty_ReturnsNull()
    {
        var mgr = CreateInitialized();

        Assert.Null(mgr.GetDefinition(null));
        Assert.Null(mgr.GetDefinition(""));
    }

    [Fact]
    public void Tokenize_NullGrammar_ReturnsEmpty()
    {
        var mgr = CreateInitialized();

        var tokens = mgr.Tokenize("my $foo = 42;", grammar: null);

        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_SimplePerlLine_ProducesTokens()
    {
        var mgr = CreateInitialized();
        var grammar = mgr.GetDefinition(".pl")!;

        var tokens = mgr.Tokenize("my $foo = 42;  # comment", grammar);

        Assert.NotEmpty(tokens);
        Assert.Contains(tokens, t => t.Scope == "keyword");
        Assert.Contains(tokens, t => t.Scope == "variable");
        Assert.Contains(tokens, t => t.Scope == "number");
        Assert.Contains(tokens, t => t.Scope == "comment");
    }

    [Fact]
    public void Tokenize_UnclosedString_CarriesStateAcrossLines()
    {
        var mgr = CreateInitialized();
        var grammar = mgr.GetDefinition(".pl")!;

        mgr.Tokenize("my $x = \"hello", grammar, mgr.DefaultState, out var midState);

        Assert.NotNull(midState.OpenQuote);
        Assert.Equal('"', midState.OpenQuote);

        mgr.Tokenize("world\";", grammar, midState, out var endState);

        Assert.Null(endState.OpenQuote);
    }
}
