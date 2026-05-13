using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
}
