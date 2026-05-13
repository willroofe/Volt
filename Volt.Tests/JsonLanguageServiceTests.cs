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
}
