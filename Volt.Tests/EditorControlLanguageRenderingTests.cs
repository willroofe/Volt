using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Volt;
using Xunit;

namespace Volt.Tests;

public class EditorControlLanguageRenderingTests
{
    [StaFact]
    public void SetLanguage_InvalidatesTextRenderCache()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        SetPrivateField(editor, "_textVisualDirty", false);

        editor.SetLanguage(new JsonLanguageService());

        Assert.True(GetPrivateField<bool>(editor, "_textVisualDirty"));
    }

    [StaFact]
    public void GetLanguageSnapshot_ReturnsAssignedLanguageTokens()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(new JsonLanguageService());
        editor.SetContent("""{ "name": "Volt", "enabled": true }""");

        LanguageSnapshot? snapshot = editor.GetLanguageSnapshot();

        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.Tokens,
            token => token.Kind == LanguageTokenKind.PropertyName && token.Text == "\"name\"");
        Assert.Contains(snapshot.Tokens,
            token => token.Kind == LanguageTokenKind.Boolean && token.Text == "true");
    }

    [StaFact]
    public void RenderLargeDocument_UsesSegmentTokenizationInsteadOfFullAnalysis()
    {
        var service = new CountingLanguageService();
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(service);
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new RepeatingTextSource(200_000, """{ "name": "Volt", "enabled": true }"""),
            LineEnding = "\n"
        });

        var size = new Size(640, 360);
        editor.Measure(size);
        editor.Arrange(new Rect(size));
        editor.UpdateLayout();

        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);

        Assert.Equal(0, service.AnalyzeCalls);
        Assert.True(service.TokenizeForRenderingCalls > 0);
    }

    [StaFact]
    public void RenderLongLine_ProvidesStringStateForSegmentTokenization()
    {
        var service = new CountingLanguageService();
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(service);
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new LongJsonStringTextSource(2_100_000),
            LineEnding = "\n"
        });

        var size = new Size(640, 360);
        editor.Measure(size);
        editor.Arrange(new Rect(size));
        editor.UpdateLayout();
        editor.SetHorizontalOffset(100_000);
        SetPrivateField(editor, "_textVisualDirty", true);
        editor.InvalidateVisual();
        editor.UpdateLayout();

        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);

        Assert.Equal(0, service.AnalyzeCalls);
        Assert.True(service.GetRenderStateCalls > 0);
        Assert.True(service.TokenizeForRenderingCalls > 0);
        Assert.False(service.LastInitialState.IsDefault);
        Assert.Equal(LanguageTokenKind.String, service.LastInitialState.TokenKind);
    }

    [StaFact]
    public void Diagnostics_MalformedJson_PublishesStatus()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        int changedCount = 0;
        editor.DiagnosticsChanged += (_, _) => changedCount++;

        editor.SetLanguage(new JsonLanguageService());
        editor.SetContent("""{ "name" "Volt" }""");
        InvokePrivate(editor, "StartDiagnosticsAnalysis");

        WaitUntil(() => editor.DiagnosticCount > 0);

        Assert.True(changedCount > 0);
        Assert.Contains("JSON error", editor.DiagnosticsStatusText);

        editor.SetCaretPosition(0, 9);

        Assert.Contains("Expected ':'", editor.CurrentDiagnosticMessage);
        Assert.Contains("Expected ':'", editor.DiagnosticsStatusText);
    }

    [StaFact]
    public void Diagnostics_StaleBackgroundResult_IsIgnored()
    {
        var service = new SlowDiagnosticsLanguageService();
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(service);
        editor.SetContent("first");

        InvokePrivate(editor, "StartDiagnosticsAnalysis");
        WaitUntil(() => service.Started);

        editor.SetContent("second");
        service.ReleaseFirstRun();
        InvokePrivate(editor, "StartDiagnosticsAnalysis");

        WaitUntil(() => service.CompletedCalls >= 2);

        Assert.Equal(0, editor.DiagnosticCount);
        Assert.Equal("", editor.DiagnosticsStatusText);
    }

    [StaFact]
    public void Diagnostics_LargeJson_DisablesAutomaticChecking()
    {
        var service = new CountingDiagnosticsJsonLanguageService();
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(service);
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new LongInvalidJsonTextSource(51 * 1024 * 1024),
            LineEnding = "\n"
        });

        InvokePrivate(editor, "StartDiagnosticsAnalysis");

        Assert.Equal(0, service.AnalyzeDiagnosticsCalls);
        Assert.Equal(0, editor.DiagnosticCount);
        Assert.Contains("disabled", editor.DiagnosticsStatusText);
        Assert.Contains("50 MiB", editor.DiagnosticsStatusText);
    }

    [StaFact]
    public void RenderDiagnostics_DoesNotCrashForWrappedAndLongLines()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager())
        {
            WordWrap = true
        };
        editor.SetLanguage(new JsonLanguageService());
        editor.SetContent("""{ "name" "Volt", "array": [1,], "unterminated": "value }""");
        AnalyzeDiagnosticsSynchronously(editor);

        var size = new Size(320, 180);
        editor.Measure(size);
        editor.Arrange(new Rect(size));
        editor.UpdateLayout();

        var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);

        editor.WordWrap = false;
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new LongInvalidJsonTextSource(1_000_000),
            LineEnding = "\n"
        });
        AnalyzeDiagnosticsSynchronously(editor);
        editor.SetHorizontalOffset(250_000);
        bitmap.Render(editor);
    }

    [StaFact]
    public void RenderMatchingPairs_DoesNotCrashForNormalWrappedAndLongLines()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(new JsonLanguageService());
        editor.SetContent("""{ "items": ["abc"] }""");
        editor.SetCaretPosition(0, 14);

        var size = new Size(320, 180);
        editor.Measure(size);
        editor.Arrange(new Rect(size));
        editor.UpdateLayout();

        var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);

        editor.WordWrap = true;
        editor.SetContent("""{ "items": ["abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz"] }""");
        editor.SetCaretPosition(0, 26);
        bitmap.Render(editor);

        editor.WordWrap = false;
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new LongJsonStringTextSource(600_000),
            LineEnding = "\n"
        });
        editor.SetCaretPosition(0, 250_000);
        editor.SetHorizontalOffset(249_500);
        bitmap.Render(editor);
    }

    [StaFact]
    public void MatchingPairs_LargeJsonWithoutFullSyntaxSnapshot_ReturnsEmpty()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(new JsonLanguageService());
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new LongJsonStringTextSource(2_100_000),
            LineEnding = "\n"
        });
        editor.SetCaretPosition(0, 100);

        var pairs = Assert.IsAssignableFrom<IReadOnlyList<LanguagePairHighlight>>(
            InvokePrivate(editor, "GetMatchingPairsForCaret"));

        Assert.Empty(pairs);
    }

    [StaFact]
    public void MatchingPairs_DisabledModeSkipsLanguageService()
    {
        var service = new CountingMatchingLanguageService();
        var editor = new EditorControl(new ThemeManager(), new LanguageManager())
        {
            BracketHighlightMode = AppSettings.BracketHighlightModeDisabled
        };
        editor.SetLanguage(service);
        editor.SetContent("[]");
        editor.SetCaretPosition(0, 1);

        IReadOnlyList<LanguagePairHighlight> pairs = editor.GetMatchingPairsForRendering();

        Assert.Empty(pairs);
        Assert.Equal(0, service.AnalyzeCalls);
        Assert.Equal(0, service.GetMatchingPairsCalls);
    }

    [StaFact]
    public void MatchingPairs_RenderingUsesNearestPairsFirstAndLevelLimit()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(new JsonLanguageService());
        editor.SetContent("""{ "items": ["abc"] }""");
        editor.SetCaretPosition(0, 14);

        IReadOnlyList<LanguagePairHighlight> pairs = editor.GetMatchingPairsForRendering();

        Assert.Collection(pairs,
            pair => Assert.Equal(LanguagePairKind.String, pair.Kind),
            pair => Assert.Equal(LanguagePairKind.Array, pair.Kind),
            pair => Assert.Equal(LanguagePairKind.Object, pair.Kind));

        editor.BracketHighlightLevels = 1;

        LanguagePairHighlight pair = Assert.Single(editor.GetMatchingPairsForRendering());
        Assert.Equal(LanguagePairKind.String, pair.Kind);
    }

    [StaFact]
    public void MatchingPairHighlights_ColourisedPaletteIndexesUseStableNestingDepth()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetLanguage(new JsonLanguageService());
        editor.SetContent("""{ "items": ["abc"] }""");
        editor.SetCaretPosition(0, 14);

        IReadOnlyList<EditorControl.MatchingPairRenderHighlight> highlights = editor.GetMatchingPairHighlightsForRendering();

        Assert.Collection(highlights,
            highlight =>
            {
                Assert.Equal(LanguagePairKind.String, highlight.Pair.Kind);
                Assert.Equal(2, highlight.PaletteIndex);
            },
            highlight =>
            {
                Assert.Equal(LanguagePairKind.Array, highlight.Pair.Kind);
                Assert.Equal(1, highlight.PaletteIndex);
            },
            highlight =>
            {
                Assert.Equal(LanguagePairKind.Object, highlight.Pair.Kind);
                Assert.Equal(0, highlight.PaletteIndex);
            });

        editor.BracketHighlightLevels = 1;

        EditorControl.MatchingPairRenderHighlight limited = Assert.Single(editor.GetMatchingPairHighlightsForRendering());
        Assert.Equal(LanguagePairKind.String, limited.Pair.Kind);
        Assert.Equal(2, limited.PaletteIndex);
    }

    [StaFact]
    public void MatchingPairPaint_ColourisedUsesThemePaletteAndCycles()
    {
        var theme = new ColorTheme
        {
            Editor = new EditorColors
            {
                MatchingBracketColors = ["#112233", "#445566"]
            }
        };
        var editor = new EditorControl(new ThemeManager(theme), new LanguageManager());

        AssertPaintColors(editor.CreateMatchingPairPaint(0), fill: "#26112233", border: "#112233");
        AssertPaintColors(editor.CreateMatchingPairPaint(1), fill: "#26445566", border: "#445566");
        AssertPaintColors(editor.CreateMatchingPairPaint(2), fill: "#26112233", border: "#112233");
    }

    [StaFact]
    public void MatchingPairPaint_SingleColourUsesThemeBrushes()
    {
        var theme = new ColorTheme
        {
            Editor = new EditorColors
            {
                MatchingBracket = "#112233",
                MatchingBracketBorder = "#445566"
            }
        };
        var editor = new EditorControl(new ThemeManager(theme), new LanguageManager())
        {
            BracketHighlightMode = AppSettings.BracketHighlightModeSingleColour
        };

        AssertPaintColors(editor.CreateMatchingPairPaint(0), fill: "#112233", border: "#445566");
        AssertPaintColors(editor.CreateMatchingPairPaint(3), fill: "#112233", border: "#445566");
    }

    private static T GetPrivateField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static void SetPrivateField<T>(object instance, string name, T value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(instance, value);
    }

    private static object? InvokePrivate(object instance, string name, params object[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(instance, args);
    }

    private static void AnalyzeDiagnosticsSynchronously(EditorControl editor)
    {
        var service = new JsonLanguageService();
        TextBuffer buffer = GetPrivateField<TextBuffer>(editor, "_buffer");
        TextBuffer.LineSnapshot source = buffer.SnapshotLines(0, buffer.Count);
        LanguageDiagnosticsSnapshot snapshot = service.AnalyzeDiagnostics(
            source,
            buffer.EditGeneration,
            progress: null,
            CancellationToken.None);

        SetPrivateField(editor, "_diagnosticsSnapshot", snapshot);
        SetPrivateField(editor, "_diagnosticsVisualDirty", true);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void AssertPaintColors(EditorControl.MatchingPairPaint paint, string fill, string border)
    {
        AssertBrushColor(fill, paint.Fill);
        AssertBrushColor(border, paint.Border.Brush);
    }

    private static void AssertBrushColor(string expectedHex, Brush brush)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        var expected = (Color)ColorConverter.ConvertFromString(expectedHex)!;
        Assert.Equal(expected, solid.Color);
    }

    private sealed class CountingMatchingLanguageService : ILanguageService
    {
        public int AnalyzeCalls { get; private set; }
        public int GetMatchingPairsCalls { get; private set; }
        public string Name => "Counting pairs";
        public IReadOnlyList<string> Extensions { get; } = [".json"];

        public LanguageSnapshot Analyze(string text, long sourceVersion)
        {
            AnalyzeCalls++;
            return new LanguageSnapshot(
                Name,
                sourceVersion,
                new SyntaxNode("Root", TextRange.FromBounds(0, 0, 0, text.Length), Array.Empty<SyntaxNode>()),
                Array.Empty<LanguageToken>(),
                Array.Empty<ParseDiagnostic>());
        }

        public LanguageDiagnosticsSnapshot AnalyzeDiagnostics(
            ILanguageTextSource source,
            long sourceVersion,
            IProgress<LanguageDiagnosticsProgress>? progress,
            CancellationToken cancellationToken) =>
            new(Name, sourceVersion, Array.Empty<ParseDiagnostic>(), true, null, false);

        public LanguageRenderState GetRenderState(LanguageTextSegment segment, LanguageRenderState initialState) =>
            LanguageRenderState.Default;

        public IReadOnlyList<LanguageToken> TokenizeForRendering(
            LanguageTextSegment segment,
            LanguageRenderState initialState) =>
            Array.Empty<LanguageToken>();

        public IReadOnlyList<LanguagePairHighlight> GetMatchingPairs(
            LanguageSnapshot snapshot,
            ILanguageTextSource source,
            TextPosition caret)
        {
            GetMatchingPairsCalls++;
            return
            [
                new LanguagePairHighlight(
                    LanguagePairKind.Array,
                    TextRange.FromBounds(0, 0, 0, 1),
                    TextRange.FromBounds(0, 1, 0, 2))
            ];
        }
    }

    private sealed class CountingLanguageService : ILanguageService
    {
        private readonly JsonLanguageService _json = new();

        public int AnalyzeCalls { get; private set; }
        public int GetRenderStateCalls { get; private set; }
        public int TokenizeForRenderingCalls { get; private set; }
        public LanguageRenderState LastInitialState { get; private set; } = LanguageRenderState.Default;
        public string Name => "Counting JSON";
        public IReadOnlyList<string> Extensions { get; } = [".json"];

        public LanguageSnapshot Analyze(string text, long sourceVersion)
        {
            AnalyzeCalls++;
            throw new InvalidOperationException("Large-file rendering should not request full-document analysis.");
        }

        public LanguageDiagnosticsSnapshot AnalyzeDiagnostics(
            ILanguageTextSource source,
            long sourceVersion,
            IProgress<LanguageDiagnosticsProgress>? progress,
            CancellationToken cancellationToken) =>
            _json.AnalyzeDiagnostics(source, sourceVersion, progress, cancellationToken);

        public LanguageRenderState GetRenderState(LanguageTextSegment segment, LanguageRenderState initialState)
        {
            GetRenderStateCalls++;
            return _json.GetRenderState(segment, initialState);
        }

        public IReadOnlyList<LanguageToken> TokenizeForRendering(
            LanguageTextSegment segment,
            LanguageRenderState initialState)
        {
            TokenizeForRenderingCalls++;
            LastInitialState = initialState;
            return _json.TokenizeForRendering(segment, initialState);
        }
    }

    private sealed class SlowDiagnosticsLanguageService : ILanguageService
    {
        private readonly ManualResetEventSlim _releaseFirstRun = new();
        private int _calls;
        private int _completedCalls;
        private int _started;

        public bool Started => Volatile.Read(ref _started) != 0;
        public int CompletedCalls => Volatile.Read(ref _completedCalls);
        public string Name => "Slow JSON";
        public IReadOnlyList<string> Extensions { get; } = [".json"];

        public LanguageSnapshot Analyze(string text, long sourceVersion) =>
            throw new InvalidOperationException("Diagnostics test should not request a syntax snapshot.");

        public LanguageDiagnosticsSnapshot AnalyzeDiagnostics(
            ILanguageTextSource source,
            long sourceVersion,
            IProgress<LanguageDiagnosticsProgress>? progress,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                Volatile.Write(ref _started, 1);
                _releaseFirstRun.Wait(TimeSpan.FromSeconds(5));
                Interlocked.Increment(ref _completedCalls);
                return new LanguageDiagnosticsSnapshot(
                    Name,
                    sourceVersion,
                    [new ParseDiagnostic(
                        TextRange.FromBounds(0, 0, 0, 1),
                        DiagnosticSeverity.Error,
                        "stale diagnostic")],
                    IsComplete: true,
                    Progress: null,
                    HasMoreDiagnostics: false);
            }

            Interlocked.Increment(ref _completedCalls);
            return new LanguageDiagnosticsSnapshot(
                Name,
                sourceVersion,
                Array.Empty<ParseDiagnostic>(),
                IsComplete: true,
                Progress: null,
                HasMoreDiagnostics: false);
        }

        public void ReleaseFirstRun() => _releaseFirstRun.Set();

        public LanguageRenderState GetRenderState(LanguageTextSegment segment, LanguageRenderState initialState) =>
            LanguageRenderState.Default;

        public IReadOnlyList<LanguageToken> TokenizeForRendering(
            LanguageTextSegment segment,
            LanguageRenderState initialState) =>
            Array.Empty<LanguageToken>();
    }

    private sealed class CountingDiagnosticsJsonLanguageService : ILanguageService
    {
        private readonly JsonLanguageService _json = new();

        public int AnalyzeDiagnosticsCalls { get; private set; }
        public string Name => "JSON";
        public IReadOnlyList<string> Extensions { get; } = [".json"];

        public LanguageSnapshot Analyze(string text, long sourceVersion) =>
            throw new InvalidOperationException("Diagnostics test should not request a syntax snapshot.");

        public LanguageDiagnosticsSnapshot AnalyzeDiagnostics(
            ILanguageTextSource source,
            long sourceVersion,
            IProgress<LanguageDiagnosticsProgress>? progress,
            CancellationToken cancellationToken)
        {
            AnalyzeDiagnosticsCalls++;
            return _json.AnalyzeDiagnostics(source, sourceVersion, progress, cancellationToken);
        }

        public LanguageRenderState GetRenderState(LanguageTextSegment segment, LanguageRenderState initialState) =>
            _json.GetRenderState(segment, initialState);

        public IReadOnlyList<LanguageToken> TokenizeForRendering(
            LanguageTextSegment segment,
            LanguageRenderState initialState) =>
            _json.TokenizeForRendering(segment, initialState);
    }

    private sealed class RepeatingTextSource : ITextSource
    {
        private readonly string _line;

        public RepeatingTextSource(int lineCount, string line)
        {
            LineCount = lineCount;
            _line = line;
        }

        public int LineCount { get; }
        public long CharCountWithoutLineEndings => (long)LineCount * _line.Length;
        public int MaxLineLength => _line.Length;

        public string GetLine(int line) => _line;
        public int GetLineLength(int line) => _line.Length;

        public string GetLineSegment(int line, int startColumn, int length)
        {
            if (length <= 0 || startColumn >= _line.Length)
                return "";

            return _line.Substring(startColumn, Math.Min(length, _line.Length - startColumn));
        }

        public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
        {
            int end = Math.Min(LineCount, startLine + count);
            for (int i = startLine; i < end; i++)
                yield return _line;
        }

        public int GetMaxLineLength(int startLine, int count) => count <= 0 ? 0 : _line.Length;

        public long GetCharCountWithoutLineEndings(int startLine, int count)
        {
            int actualCount = Math.Max(0, Math.Min(count, LineCount - startLine));
            return (long)actualCount * _line.Length;
        }
    }

    private sealed class LongJsonStringTextSource : ITextSource
    {
        private readonly int _lineLength;

        public LongJsonStringTextSource(int lineLength)
        {
            _lineLength = lineLength;
        }

        public int LineCount => 1;
        public long CharCountWithoutLineEndings => _lineLength;
        public int MaxLineLength => _lineLength;

        public string GetLine(int line) => GetLineSegment(line, 0, _lineLength);
        public int GetLineLength(int line) => _lineLength;

        public string GetLineSegment(int line, int startColumn, int length)
        {
            if (length <= 0 || startColumn >= _lineLength)
                return "";

            int count = Math.Min(length, _lineLength - startColumn);
            char[] chars = new char[count];
            for (int i = 0; i < chars.Length; i++)
            {
                int column = startColumn + i;
                chars[i] = column == 0 || column == _lineLength - 1 ? '"' : 'a';
            }

            return new string(chars);
        }

        public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
        {
            if (startLine == 0 && count > 0)
                yield return GetLine(0);
        }

        public int GetMaxLineLength(int startLine, int count) => count <= 0 ? 0 : _lineLength;

        public long GetCharCountWithoutLineEndings(int startLine, int count) =>
            startLine == 0 && count > 0 ? _lineLength : 0;
    }

    private sealed class LongInvalidJsonTextSource : ITextSource
    {
        private readonly int _lineLength;
        private const string Prefix = "{\"padding\":\"";
        private const string Suffix = "\",\"bad\": truX}";

        public LongInvalidJsonTextSource(int lineLength)
        {
            _lineLength = lineLength;
        }

        public int LineCount => 1;
        public long CharCountWithoutLineEndings => _lineLength;
        public int MaxLineLength => _lineLength;

        public string GetLine(int line) => GetLineSegment(line, 0, _lineLength);
        public int GetLineLength(int line) => _lineLength;

        public string GetLineSegment(int line, int startColumn, int length)
        {
            if (length <= 0 || startColumn >= _lineLength)
                return "";

            int count = Math.Min(length, _lineLength - startColumn);
            char[] chars = new char[count];
            int paddingEnd = Math.Max(Prefix.Length, _lineLength - Suffix.Length);
            for (int i = 0; i < count; i++)
            {
                int column = startColumn + i;
                if (column < Prefix.Length)
                    chars[i] = Prefix[column];
                else if (column >= paddingEnd)
                    chars[i] = Suffix[column - paddingEnd];
                else
                    chars[i] = 'a';
            }

            return new string(chars);
        }

        public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
        {
            if (startLine == 0 && count > 0)
                yield return GetLine(0);
        }

        public int GetMaxLineLength(int startLine, int count) => count <= 0 ? 0 : _lineLength;

        public long GetCharCountWithoutLineEndings(int startLine, int count) =>
            startLine == 0 && count > 0 ? _lineLength : 0;
    }
}
