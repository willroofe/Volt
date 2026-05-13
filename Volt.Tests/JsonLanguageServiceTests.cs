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
    public void AnalyzeDiagnostics_ValidJson_ReturnsNoDiagnostics()
    {
        var service = new JsonLanguageService();

        LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
            CreateSource("""{ "name": "Volt", "items": [true, false, null, 3] }"""),
            sourceVersion: 7,
            progress: null,
            CancellationToken.None);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(7, snapshot.SourceVersion);
        Assert.Empty(snapshot.Diagnostics);
        Assert.False(snapshot.HasMoreDiagnostics);
    }

    [Fact]
    public void AnalyzeDiagnostics_MalformedJson_ReportsCommonErrors()
    {
        var service = new JsonLanguageService();

        LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
            CreateSource("""
            {
              "missingColon" "value",
              "trailingObject": true,
              "badLiteral": truth,
              "badEscape": "\q",
              "badUnicode": "\u12Z",
              "badNumber": 01,
              "array": [1,],
              "unterminatedString": "nope
            """),
            sourceVersion: 1,
            progress: null,
            CancellationToken.None);

        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Expected ':'"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Trailing commas"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Unexpected literal"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Invalid escape sequence"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Unicode escape"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("leading zeroes"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("String literal is not terminated"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Object is not terminated"));
    }

    [Fact]
    public void AnalyzeDiagnostics_MultipleTopLevelValues_ReportsDiagnostic()
    {
        var service = new JsonLanguageService();

        LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
            CreateSource("true false"),
            sourceVersion: 1,
            progress: null,
            CancellationToken.None);

        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Only one top-level"));
    }

    [Fact]
    public void AnalyzeDiagnostics_DeepSingleLineError_ReadsBoundedSegments()
    {
        var service = new JsonLanguageService();
        var source = new HugeSingleLineJsonSource(200_000);

        LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
            source,
            sourceVersion: 1,
            progress: null,
            CancellationToken.None);

        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Message.Contains("Unexpected literal"));
        Assert.InRange(source.MaxRequestedSegmentLength, 1, 64 * 1024);
    }

    [Fact]
    public void AnalyzeDiagnostics_CapsStoredDiagnostics()
    {
        var service = new JsonLanguageService();
        string text = string.Join('\n', Enumerable.Repeat("@", 1_050));

        LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
            CreateSource(text),
            sourceVersion: 1,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1000, snapshot.Diagnostics.Count);
        Assert.True(snapshot.HasMoreDiagnostics);
    }

    [Fact]
    public void AnalyzeDiagnostics_WhenCancelled_Throws()
    {
        var service = new JsonLanguageService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => service.AnalyzeDiagnostics(
            CreateSource("""{ "name": "Volt" }"""),
            sourceVersion: 1,
            progress: null,
            cts.Token));
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

    private static TextBuffer.LineSnapshot CreateSource(string text)
    {
        var buffer = new TextBuffer();
        buffer.SetContent(text, tabSize: 4);
        return buffer.SnapshotLines(0, buffer.Count);
    }

    private sealed class HugeSingleLineJsonSource : ILanguageTextSource
    {
        private const string Prefix = "{\"padding\":\"";
        private const string Suffix = "\",\"bad\": truX}";
        private readonly int _paddingLength;

        public HugeSingleLineJsonSource(int paddingLength)
        {
            _paddingLength = paddingLength;
        }

        public int LineCount => 1;
        public long CharCountWithoutLineEndings => GetLineLength(0);
        public int MaxRequestedSegmentLength { get; private set; }

        public int GetLineLength(int line) => Prefix.Length + _paddingLength + Suffix.Length;

        public string GetLineSegment(int line, int startColumn, int length)
        {
            MaxRequestedSegmentLength = Math.Max(MaxRequestedSegmentLength, length);
            int lineLength = GetLineLength(line);
            int count = Math.Min(length, lineLength - startColumn);
            if (count <= 0)
                return "";

            char[] chars = new char[count];
            for (int i = 0; i < count; i++)
            {
                int column = startColumn + i;
                chars[i] = GetChar(column);
            }

            return new string(chars);
        }

        private char GetChar(int column)
        {
            if (column < Prefix.Length)
                return Prefix[column];

            int paddingEnd = Prefix.Length + _paddingLength;
            if (column < paddingEnd)
                return 'a';

            return Suffix[column - paddingEnd];
        }
    }
}
