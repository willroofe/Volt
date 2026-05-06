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

    [Fact]
    public void Tokenize_HtmlStyle_UsesCssGrammarInsideTag()
    {
        var mgr = CreateInitialized();
        var grammar = mgr.GetDefinition(".html")!;

        var tokens = mgr.Tokenize("<style>:root { --accent-color: #3b82f6; }</style>", grammar);

        Assert.Contains(tokens, t => t.Scope == "hashkey");
        Assert.Contains(tokens, t => t.Scope == "number");
        Assert.Contains(tokens, t => t.Scope == "operator");
    }

    [Fact]
    public void Tokenize_HtmlScript_CarriesJavaScriptStateAcrossLines()
    {
        var mgr = CreateInitialized();
        var grammar = mgr.GetDefinition(".html")!;

        mgr.Tokenize("<script>", grammar, mgr.DefaultState, out var scriptState);

        Assert.Equal(1, scriptState.EmbeddedRegionIndex);

        var scriptTokens = mgr.Tokenize("const message = `hello ${name}`;", grammar, scriptState, out var nextState);

        Assert.Contains(scriptTokens, t => t.Scope == "keyword");
        Assert.Contains(scriptTokens, t => t.Scope == "variable");
        Assert.Equal(1, nextState.EmbeddedRegionIndex);

        var closingTokens = mgr.Tokenize("</script>", grammar, nextState, out var endState);

        Assert.Contains(closingTokens, t => t.Scope == "keyword");
        Assert.Equal(mgr.DefaultState, endState);
    }
}
