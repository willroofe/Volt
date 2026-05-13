using Volt;
using Xunit;

namespace Volt.Tests;

public class JsonLanguageServiceTests
{
    [Fact]
    public void Analyze_ValidObject_BuildsTreeAndClassifiesTokens()
    {
        var service = new JsonLanguageService();

        LanguageSnapshot snapshot = service.Analyze("""
        {
          "name": "Volt",
          "enabled": true,
          "count": 3,
          "items": [null, false]
        }
        """, sourceVersion: 42);

        Assert.Equal("JSON", snapshot.LanguageName);
        Assert.Equal(42, snapshot.SourceVersion);
        Assert.Equal(JsonSyntaxKinds.Document, snapshot.Root.Kind);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Contains(snapshot.Tokens, token => token.Kind == LanguageTokenKind.PropertyName && token.Text == "\"name\"");
        Assert.Contains(snapshot.Tokens, token => token.Kind == LanguageTokenKind.String && token.Text == "\"Volt\"");
        Assert.Contains(snapshot.Tokens, token => token.Kind == LanguageTokenKind.Boolean && token.Text == "true");
        Assert.Contains(snapshot.Tokens, token => token.Kind == LanguageTokenKind.Number && token.Text == "3");
        Assert.Contains(snapshot.Tokens, token => token.Kind == LanguageTokenKind.Null && token.Text == "null");
    }

    [Fact]
    public void Analyze_MalformedJson_ReportsDiagnosticsAndKeepsTree()
    {
        var service = new JsonLanguageService();

        LanguageSnapshot snapshot = service.Analyze("{ \"name\" \"Volt\", }", sourceVersion: 1);

        Assert.NotEmpty(snapshot.Diagnostics);
        Assert.Equal(JsonSyntaxKinds.Document, snapshot.Root.Kind);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Expected ':'"));
    }

    [Fact]
    public void LanguageManager_DetectsJsonByExtension()
    {
        var manager = new LanguageManager();

        ILanguageService? service = manager.GetService(".json");

        Assert.NotNull(service);
        Assert.Equal("JSON", service!.Name);
        Assert.Contains("JSON", manager.GetAvailableLanguages());
    }

    [Fact]
    public void TokenizeForRendering_UsesAbsoluteRangeAndClassifiesPropertyNames()
    {
        var service = new JsonLanguageService();

        IReadOnlyList<LanguageToken> tokens = service.TokenizeForRendering(
            new LanguageTextSegment(123, 10, """  "name": "Volt", true, null, 3"""),
            LanguageRenderState.Default);

        Assert.Contains(tokens,
            token => token.Kind == LanguageTokenKind.PropertyName
                     && token.Text == "\"name\""
                     && token.Range.Start == new TextPosition(123, 12));
        Assert.Contains(tokens,
            token => token.Kind == LanguageTokenKind.String && token.Text == "\"Volt\"");
        Assert.Contains(tokens,
            token => token.Kind == LanguageTokenKind.Boolean && token.Text == "true");
        Assert.Contains(tokens,
            token => token.Kind == LanguageTokenKind.Null && token.Text == "null");
        Assert.Contains(tokens,
            token => token.Kind == LanguageTokenKind.Number && token.Text == "3");
    }

    [Fact]
    public void TokenizeForRendering_ContinuesStringFromInitialState()
    {
        var service = new JsonLanguageService();
        LanguageRenderState state = service.GetRenderState(
            new LanguageTextSegment(4, 0, "\"abcdef"),
            LanguageRenderState.Default);

        IReadOnlyList<LanguageToken> tokens = service.TokenizeForRendering(
            new LanguageTextSegment(4, 7, "gh\""),
            state);

        LanguageToken token = Assert.Single(tokens);
        Assert.Equal(LanguageTokenKind.String, token.Kind);
        Assert.Equal(new TextPosition(4, 0), token.Range.Start);
        Assert.Equal(new TextPosition(4, 10), token.Range.End);
    }

    [Fact]
    public void TokenizeForRendering_PreservesEscapedQuoteAcrossSegmentBoundary()
    {
        var service = new JsonLanguageService();
        LanguageRenderState state = service.GetRenderState(
            new LanguageTextSegment(0, 0, "\"abc\\"),
            LanguageRenderState.Default);

        IReadOnlyList<LanguageToken> tokens = service.TokenizeForRendering(
            new LanguageTextSegment(0, 5, "\"still\""),
            state);

        LanguageToken token = Assert.Single(tokens);
        Assert.Equal(LanguageTokenKind.String, token.Kind);
        Assert.Equal(new TextPosition(0, 0), token.Range.Start);
        Assert.Equal(new TextPosition(0, 12), token.Range.End);
    }
}
