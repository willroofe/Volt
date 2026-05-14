using System.IO;
using System.Text;
using Volt;
using Xunit;

namespace Volt.Tests;

public class JsonLanguageServiceTests
{
    public static TheoryData<string> DiagnosticAlignmentCases => new()
    {
        """{ "name": "Volt" }""",
        """{ "missingColon" "value" }""",
        """{ "trailing": true, }""",
        """[1,]""",
        """{ "badLiteral": truth }""",
        """{ "badEscape": "\q" }""",
        """{ "badUnicode": "\u12Z" }""",
        """{ "badNumber": 01 }""",
        "{ \"unterminatedString\": \"nope",
        """true false""",
        """{ "first": true "second": false }""",
        """[1 { "nested": true }]""",
        """{} {}""",
        """@""",
        """{ "array": [1, 2 }""",
    };

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

    [Theory]
    [MemberData(nameof(DiagnosticAlignmentCases))]
    public void AnalyzeDiagnostics_MatchesFullAnalysisDiagnostics(string text)
    {
        var service = new JsonLanguageService();

        LanguageSnapshot fullSnapshot = service.Analyze(text, sourceVersion: 1);
        LanguageDiagnosticsSnapshot diagnosticsSnapshot = service.AnalyzeDiagnostics(
            new StringLanguageTextSource(text),
            sourceVersion: 1,
            progress: null,
            CancellationToken.None);

        var fullDiagnostics = fullSnapshot.Diagnostics
            .Select(diagnostic => (diagnostic.Range, diagnostic.Message))
            .ToArray();
        var streamingDiagnostics = diagnosticsSnapshot.Diagnostics
            .Select(diagnostic => (diagnostic.Range, diagnostic.Message))
            .ToArray();

        Assert.Equal(fullDiagnostics, streamingDiagnostics);
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

    [Theory]
    [InlineData("""{ "first": true "second": false }""", "Expected ',' or '}' after object property.")]
    [InlineData("""[1 { "nested": true }]""", "Expected ',' or ']' after array item.")]
    [InlineData("""{} {}""", "Only one top-level JSON value is allowed.")]
    public void AnalyzeDiagnostics_RecoversFromCommonMalformedJson_WithoutSecondaryNoise(
        string text,
        string expectedMessage)
    {
        var service = new JsonLanguageService();

        LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
            CreateSource(text),
            sourceVersion: 1,
            progress: null,
            CancellationToken.None);

        Assert.Collection(snapshot.Diagnostics,
            diagnostic => Assert.Equal(expectedMessage, diagnostic.Message));
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
        Assert.InRange(source.MaxRequestedSegmentLength, 1, 1024 * 1024);
    }

    [Fact]
    public void AnalyzeDiagnostics_WhenStreamingSourceAvailable_UsesStreamingSegments()
    {
        var service = new JsonLanguageService();
        var source = new StreamingSingleLineJsonSource("""{ "name": "Volt", "ok": true }""");

        LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
            source,
            sourceVersion: 1,
            progress: null,
            CancellationToken.None);

        Assert.Empty(snapshot.Diagnostics);
        Assert.True(source.StreamReadCount > 0);
        Assert.Equal(0, source.FallbackSegmentRequestCount);
    }

    [Fact]
    public void AnalyzeDiagnostics_WhenStreamingUnavailable_UsesSegmentFallback()
    {
        var service = new JsonLanguageService();
        var source = new HugeSingleLineJsonSource(200_000);

        service.AnalyzeDiagnostics(
            source,
            sourceVersion: 1,
            progress: null,
            CancellationToken.None);

        Assert.True(source.SegmentRequestCount > 0);
    }

    [Fact]
    public void AnalyzeDiagnostics_FileBackedCrlfSource_StreamsMultiLineChunks()
    {
        string path = Path.Combine(Path.GetTempPath(), "Volt.Tests." + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\r\n  \"name\": \"Volt\",\r\n  \"ok\": true\r\n}", new UTF8Encoding(false));
        try
        {
            var buffer = new TextBuffer();
            buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));
            TextBuffer.LineSnapshot source = buffer.SnapshotLines(0, buffer.Count);

            var streamSource = Assert.IsAssignableFrom<ILanguageTextStreamSource>(source);
            Assert.True(streamSource.TryCreateTextStream(0, source.LineCount, out ILanguageTextStream stream));
            using (stream)
            {
                LanguageTextReadSegment segment = stream.ReadSegment(4096, CancellationToken.None);
                Assert.Contains("\r\n", segment.Text);
                Assert.False(segment.IsEnd);
            }

            var service = new JsonLanguageService();
            LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
                source,
                sourceVersion: 1,
                progress: null,
                CancellationToken.None);

            Assert.Empty(snapshot.Diagnostics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnalyzeDiagnostics_FileBackedMultiChunkJson_DoesNotCrash()
    {
        string path = Path.Combine(Path.GetTempPath(), "Volt.Tests." + Guid.NewGuid().ToString("N") + ".json");
        var text = new StringBuilder();
        text.Append("{\r\n  \"items\": [");
        int item = 0;
        while (text.Length < 2 * 1024 * 1024)
        {
            text.Append(item == 0 ? "\r\n    " : ",\r\n    ");
            text.Append("{ \"id\": ");
            text.Append(item);
            text.Append(", \"name\": \"fixture item\", \"active\": true, \"payload\": \"abcdefghijklmnopqrstuvwxyz0123456789\" }");
            item++;
        }

        text.Append("\r\n  ]\r\n}\r\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        try
        {
            var buffer = new TextBuffer();
            buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));
            TextBuffer.LineSnapshot source = buffer.SnapshotLines(0, buffer.Count);
            var service = new JsonLanguageService();

            LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
                source,
                sourceVersion: 1,
                progress: null,
                CancellationToken.None);

            Assert.Empty(snapshot.Diagnostics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnalyzeDiagnostics_CancelsDuringLongStreamingString()
    {
        var service = new JsonLanguageService();
        using var cts = new CancellationTokenSource();
        var source = new StreamingSingleLineJsonSource("\"" + new string('a', 128 * 1024), cts);

        Assert.Throws<OperationCanceledException>(() => service.AnalyzeDiagnostics(
            source,
            sourceVersion: 1,
            progress: null,
            cts.Token));

        Assert.True(source.StreamReadCount > 0);
    }

    [Fact]
    public void AnalyzeDiagnostics_ReportsMonotonicProgress()
    {
        var service = new JsonLanguageService();
        var source = new HugeSingleLineJsonSource(200_000);
        var values = new List<LanguageDiagnosticsProgress>();

        service.AnalyzeDiagnostics(
            source,
            sourceVersion: 1,
            progress: new CapturingProgress(values),
            CancellationToken.None);

        Assert.NotEmpty(values);
        Assert.Equal(0, values[0].CharactersProcessed);
        Assert.Equal(source.CharCountWithoutLineEndings, values[^1].CharactersProcessed);
        Assert.All(values.Zip(values.Skip(1)), pair =>
            Assert.True(pair.First.CharactersProcessed <= pair.Second.CharactersProcessed));
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

    [Fact]
    public void GetMatchingPairs_CaretInsideArrayString_ReturnsStringAndArrayPairs()
    {
        var service = new JsonLanguageService();
        const string text = """["abc"]""";
        TextBuffer.LineSnapshot source = CreateSource(text);
        LanguageSnapshot snapshot = service.Analyze(text, sourceVersion: 1);

        IReadOnlyList<LanguagePairHighlight> pairs = service.GetMatchingPairs(
            snapshot,
            source,
            new TextPosition(0, 3));

        Assert.Collection(pairs,
            pair => AssertPair(pair, LanguagePairKind.Array, 0, 6),
            pair => AssertPair(pair, LanguagePairKind.String, 1, 5));
    }

    [Fact]
    public void GetMatchingPairs_CaretInsideNestedJson_ReturnsAllContainingPairs()
    {
        var service = new JsonLanguageService();
        const string text = """{ "items": ["abc"] }""";
        TextBuffer.LineSnapshot source = CreateSource(text);
        LanguageSnapshot snapshot = service.Analyze(text, sourceVersion: 1);

        IReadOnlyList<LanguagePairHighlight> pairs = service.GetMatchingPairs(
            snapshot,
            source,
            new TextPosition(0, 14));

        Assert.Collection(pairs,
            pair => AssertPair(pair, LanguagePairKind.Object, 0, 19),
            pair => AssertPair(pair, LanguagePairKind.Array, 11, 17),
            pair => AssertPair(pair, LanguagePairKind.String, 12, 16));
    }

    [Fact]
    public void GetMatchingPairs_CaretOnDelimiter_ReturnsRelevantPair()
    {
        var service = new JsonLanguageService();
        const string text = """["abc"]""";
        TextBuffer.LineSnapshot source = CreateSource(text);
        LanguageSnapshot snapshot = service.Analyze(text, sourceVersion: 1);

        IReadOnlyList<LanguagePairHighlight> pairs = service.GetMatchingPairs(
            snapshot,
            source,
            new TextPosition(0, 1));

        Assert.Collection(pairs,
            pair => AssertPair(pair, LanguagePairKind.Array, 0, 6),
            pair => AssertPair(pair, LanguagePairKind.String, 1, 5));
    }

    [Fact]
    public void GetMatchingPairs_UnterminatedJson_DoesNotReturnIncompletePairs()
    {
        var service = new JsonLanguageService();
        const string text = "[\"abc]";
        TextBuffer.LineSnapshot source = CreateSource(text);
        LanguageSnapshot snapshot = service.Analyze(text, sourceVersion: 1);

        IReadOnlyList<LanguagePairHighlight> pairs = service.GetMatchingPairs(
            snapshot,
            source,
            new TextPosition(0, 3));

        Assert.Empty(pairs);
    }

    [Fact]
    public void GetMatchingPairs_SourceOnlyNestedJson_ReturnsAllContainingPairs()
    {
        var service = new JsonLanguageService();
        const string text = """{ "items": ["abc"] }""";
        TextBuffer.LineSnapshot source = CreateSource(text);

        IReadOnlyList<LanguagePairHighlight> pairs = service.GetMatchingPairs(
            source,
            new TextPosition(0, 14),
            CancellationToken.None);

        Assert.Collection(pairs,
            pair => AssertPair(pair, LanguagePairKind.Object, 0, 19),
            pair => AssertPair(pair, LanguagePairKind.Array, 11, 17),
            pair => AssertPair(pair, LanguagePairKind.String, 12, 16));
    }

    [Fact]
    public void GetMatchingPairs_SourceOnlyIgnoresBracketsInsideStrings()
    {
        var service = new JsonLanguageService();
        const string text = """{ "text": "[{}]" }""";
        TextBuffer.LineSnapshot source = CreateSource(text);

        IReadOnlyList<LanguagePairHighlight> pairs = service.GetMatchingPairs(
            source,
            new TextPosition(0, 12),
            CancellationToken.None);

        Assert.Collection(pairs,
            pair => AssertPair(pair, LanguagePairKind.Object, 0, 17),
            pair => AssertPair(pair, LanguagePairKind.String, 10, 15));
    }

    [Fact]
    public void GetMatchingPairs_SourceOnlyUnterminatedString_ReturnsEmpty()
    {
        var service = new JsonLanguageService();
        const string text = "[\"abc]";
        TextBuffer.LineSnapshot source = CreateSource(text);

        IReadOnlyList<LanguagePairHighlight> pairs = service.GetMatchingPairs(
            source,
            new TextPosition(0, 3),
            CancellationToken.None);

        Assert.Empty(pairs);
    }

    [Fact]
    public void GetMatchingPairs_SourceOnlyUsesStreamingSourceWhenAvailable()
    {
        var service = new JsonLanguageService();
        var source = new StreamingSingleLineJsonSource("""{ "items": ["abc"] }""");

        IReadOnlyList<LanguagePairHighlight> pairs = service.GetMatchingPairs(
            source,
            new TextPosition(0, 14),
            CancellationToken.None);

        Assert.Collection(pairs,
            pair => AssertPair(pair, LanguagePairKind.Object, 0, 19),
            pair => AssertPair(pair, LanguagePairKind.Array, 11, 17),
            pair => AssertPair(pair, LanguagePairKind.String, 12, 16));
        Assert.True(source.StreamReadCount > 0);
        Assert.Equal(0, source.FallbackSegmentRequestCount);
    }

    private static TextBuffer.LineSnapshot CreateSource(string text)
    {
        var buffer = new TextBuffer();
        buffer.SetContent(text, tabSize: 4);
        return buffer.SnapshotLines(0, buffer.Count);
    }

    private static void AssertPair(LanguagePairHighlight pair, LanguagePairKind kind, int openColumn, int closeColumn)
    {
        Assert.Equal(kind, pair.Kind);
        Assert.Equal(TextRange.FromBounds(0, openColumn, 0, openColumn + 1), pair.OpenRange);
        Assert.Equal(TextRange.FromBounds(0, closeColumn, 0, closeColumn + 1), pair.CloseRange);
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
        public int SegmentRequestCount { get; private set; }

        public int GetLineLength(int line) => Prefix.Length + _paddingLength + Suffix.Length;

        public string GetLineSegment(int line, int startColumn, int length)
        {
            SegmentRequestCount++;
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

    private sealed class StreamingSingleLineJsonSource : ILanguageTextSource, ILanguageTextStreamSource
    {
        private readonly string _text;
        private readonly CancellationTokenSource? _cancelOnFirstRead;

        public StreamingSingleLineJsonSource(string text, CancellationTokenSource? cancelOnFirstRead = null)
        {
            _text = text;
            _cancelOnFirstRead = cancelOnFirstRead;
        }

        public int LineCount => 1;
        public long CharCountWithoutLineEndings => _text.Length;
        public int StreamReadCount { get; private set; }
        public int FallbackSegmentRequestCount { get; private set; }

        public int GetLineLength(int line) => _text.Length;

        public string GetLineSegment(int line, int startColumn, int length)
        {
            FallbackSegmentRequestCount++;
            return "";
        }

        public bool TryCreateTextStream(int startLine, int lineCount, out ILanguageTextStream stream)
        {
            stream = new Reader(this);
            return true;
        }

        private sealed class Reader : ILanguageTextStream
        {
            private readonly StreamingSingleLineJsonSource _source;
            private int _position;

            public Reader(StreamingSingleLineJsonSource source)
            {
                _source = source;
            }

            public LanguageTextReadSegment ReadSegment(int maxLength, CancellationToken cancellationToken)
            {
                if (_position >= _source._text.Length)
                    return new LanguageTextReadSegment(0, _position, "", EndsAtLineEnd: true, IsEnd: true);

                _source.StreamReadCount++;
                if (_source.StreamReadCount == 1)
                    _source._cancelOnFirstRead?.Cancel();

                int start = _position;
                int count = Math.Min(Math.Min(maxLength, 4096), _source._text.Length - _position);
                _position += count;
                return new LanguageTextReadSegment(
                    0,
                    start,
                    _source._text.Substring(start, count),
                    _position >= _source._text.Length,
                    IsEnd: false);
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class CapturingProgress : IProgress<LanguageDiagnosticsProgress>
    {
        private readonly List<LanguageDiagnosticsProgress> _values;

        public CapturingProgress(List<LanguageDiagnosticsProgress> values)
        {
            _values = values;
        }

        public void Report(LanguageDiagnosticsProgress value) => _values.Add(value);
    }

    private sealed class StringLanguageTextSource : ILanguageTextSource
    {
        private readonly string[] _lines;

        public StringLanguageTextSource(string text)
        {
            _lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        }

        public int LineCount => _lines.Length;
        public long CharCountWithoutLineEndings => _lines.Sum(line => line.Length);

        public int GetLineLength(int line) => _lines[line].Length;

        public string GetLineSegment(int line, int startColumn, int length)
        {
            string value = _lines[line];
            if (startColumn >= value.Length)
                return "";

            int count = Math.Min(length, value.Length - startColumn);
            return value.Substring(startColumn, count);
        }
    }
}
