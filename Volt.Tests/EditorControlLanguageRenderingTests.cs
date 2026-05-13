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
        public int TokenizeForRenderingCalls { get; private set; }
        public string Name => "Counting JSON";
        public IReadOnlyList<string> Extensions { get; } = [".json"];

        public LanguageSnapshot Analyze(string text, long sourceVersion)
        {
            AnalyzeCalls++;
            throw new InvalidOperationException("Large-file rendering should not request full-document analysis.");
        }

        public IReadOnlyList<LanguageToken> TokenizeForRendering(LanguageTextSegment segment)
        {
            TokenizeForRenderingCalls++;
            return _json.TokenizeForRendering(segment);
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
}
