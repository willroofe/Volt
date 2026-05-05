using Volt;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Volt.Tests;

public class EditorControlLargeFileTests
{
    [StaFact]
    public void SetBusy_TogglesEditorBusyState()
    {
        var editor = new EditorControl(new ThemeManager(), new SyntaxManager());

        editor.SetBusy(true, "Loading file...");

        Assert.True(editor.IsBusy);

        editor.SetBusy(false);

        Assert.False(editor.IsBusy);
    }

    [StaFact]
    public void SetPreparedContent_SuppressesWordWrapForVeryManyLines()
    {
        var editor = new EditorControl(new ThemeManager(), new SyntaxManager())
        {
            WordWrap = true
        };

        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new FakeTextSource(lineCount: 1_000_001, lineLength: 10),
            LineEnding = "\n"
        });

        Assert.False(editor.WordWrap);
    }

    [StaFact]
    public void SetPreparedContent_SuppressesWordWrapForVeryLongLine()
    {
        var editor = new EditorControl(new ThemeManager(), new SyntaxManager())
        {
            WordWrap = true
        };

        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new FakeTextSource(lineCount: 1, lineLength: 500_001),
            LineEnding = "\n"
        });

        Assert.False(editor.WordWrap);
    }

    [StaFact]
    public void RenderAtHighLineNumber_KeepsRetainedVisualTransformsNearViewport()
    {
        var editor = new EditorControl(new ThemeManager(), new SyntaxManager());
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new FakeTextSource(lineCount: 12_000_000, lineLength: 32),
            LineEnding = "\n"
        });

        var size = new Size(1200, 800);
        editor.Measure(size);
        editor.Arrange(new Rect(size));
        editor.UpdateLayout();

        editor.SetVerticalOffset(160_000_000);
        var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);

        TranslateTransform textTransform = GetPrivateField<TranslateTransform>(editor, "_textTransform");
        TranslateTransform gutterTransform = GetPrivateField<TranslateTransform>(editor, "_gutterTransform");

        Assert.InRange(Math.Abs(textTransform.Y), 0, 2_000);
        Assert.InRange(Math.Abs(gutterTransform.Y), 0, 2_000);
    }

    [StaFact]
    public void TabOnHugeSelection_UsesPieceBackedUniformIndent()
    {
        const int lineCount = 1_200_000;
        var editor = new EditorControl(new ThemeManager(), new SyntaxManager());
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new FakeTextSource(lineCount, lineLength: 32),
            LineEnding = "\n"
        });

        SelectionManager selection = GetPrivateField<SelectionManager>(editor, "_selection");
        selection.AnchorLine = 0;
        selection.AnchorCol = 0;
        selection.HasSelection = true;
        SetPrivateField(editor, "_caretLine", lineCount - 1);
        SetPrivateField(editor, "_caretCol", 32);

        InvokePrivate(editor, "HandleTab", false);

        TextBuffer buffer = GetPrivateField<TextBuffer>(editor, "_buffer");
        Assert.Equal(lineCount, buffer.Count);
        Assert.StartsWith("    ", buffer[0]);
        Assert.StartsWith("    ", buffer[lineCount - 1]);

        InvokePrivate(editor, "Undo");

        Assert.False(buffer[0].StartsWith("    ", StringComparison.Ordinal));
        Assert.False(buffer[lineCount - 1].StartsWith("    ", StringComparison.Ordinal));

        InvokePrivate(editor, "Redo");

        Assert.StartsWith("    ", buffer[0]);
        Assert.StartsWith("    ", buffer[lineCount - 1]);
    }

    [StaFact]
    public void ShiftTabOnHugeSelection_UsesPieceBackedLeadingSpaceRemoval()
    {
        const int lineCount = 1_200_000;
        var editor = new EditorControl(new ThemeManager(), new SyntaxManager());
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new FakeTextSource(lineCount, lineLength: 36, prefix: "    "),
            LineEnding = "\n"
        });

        SelectionManager selection = GetPrivateField<SelectionManager>(editor, "_selection");
        selection.AnchorLine = 0;
        selection.AnchorCol = 0;
        selection.HasSelection = true;
        SetPrivateField(editor, "_caretLine", lineCount - 1);
        SetPrivateField(editor, "_caretCol", 36);

        InvokePrivate(editor, "HandleTab", true);

        TextBuffer buffer = GetPrivateField<TextBuffer>(editor, "_buffer");
        Assert.Equal(lineCount, buffer.Count);
        Assert.Equal("xxxx", buffer.GetLineSegment(0, 0, 4));
        Assert.Equal("xxxx", buffer.GetLineSegment(lineCount - 1, 0, 4));

        InvokePrivate(editor, "Undo");

        Assert.StartsWith("    ", buffer[0]);
        Assert.StartsWith("    ", buffer[lineCount - 1]);

        InvokePrivate(editor, "Redo");

        Assert.Equal("xxxx", buffer.GetLineSegment(0, 0, 4));
        Assert.Equal("xxxx", buffer.GetLineSegment(lineCount - 1, 0, 4));
    }

    [StaFact]
    public void InvalidatingLastLineSyntaxState_DoesNotRewalkLookbackWindow()
    {
        const int lineCount = 1_200_000;
        var source = new CountingTextSource(lineCount, lineLength: 32);
        var editor = new EditorControl(new ThemeManager(), new SyntaxManager());
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = source,
            LineEnding = "\n"
        });

        int lastLine = lineCount - 1;
        InvokePrivate(editor, "EnsureLineStates", lastLine);
        source.ResetCounts();

        InvokePrivate(editor, "InvalidateLineStatesFrom", lastLine);
        InvokePrivate(editor, "EnsureLineStates", lastLine);

        Assert.Equal(0, source.LineReads);
        Assert.Equal(0, source.LengthReads);
    }

    private sealed class FakeTextSource : ITextSource
    {
        private readonly string _line;

        public FakeTextSource(int lineCount, int lineLength, string prefix = "")
        {
            LineCount = lineCount;
            MaxLineLength = lineLength;
            CharCountWithoutLineEndings = (long)lineCount * lineLength;

            int materializedLength = Math.Min(lineLength, 1024);
            if (prefix.Length >= materializedLength)
            {
                _line = prefix[..materializedLength];
            }
            else
            {
                _line = prefix + new string('x', materializedLength - prefix.Length);
            }
        }

        public int LineCount { get; }
        public long CharCountWithoutLineEndings { get; }
        public int MaxLineLength { get; }

        public string GetLine(int line) => _line;
        public int GetLineLength(int line) => MaxLineLength;

        public string GetLineSegment(int line, int startColumn, int length)
        {
            if (length <= 0 || startColumn >= MaxLineLength)
                return "";

            int count = Math.Min(length, MaxLineLength - startColumn);
            if (startColumn >= _line.Length)
                return new string('x', count);

            int copied = Math.Min(count, _line.Length - startColumn);
            string segment = _line.Substring(startColumn, copied);
            return copied == count
                ? segment
                : segment + new string('x', count - copied);
        }

        public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
        {
            for (int i = 0; i < count && startLine + i < LineCount; i++)
                yield return _line;
        }

        public int GetMaxLineLength(int startLine, int count) => MaxLineLength;
        public long GetCharCountWithoutLineEndings(int startLine, int count) => (long)count * MaxLineLength;
    }

    private sealed class CountingTextSource : ITextSource
    {
        private readonly string _line;

        public CountingTextSource(int lineCount, int lineLength)
        {
            LineCount = lineCount;
            MaxLineLength = lineLength;
            CharCountWithoutLineEndings = (long)lineCount * lineLength;
            _line = new string('x', lineLength);
        }

        public int LineReads { get; private set; }
        public int LengthReads { get; private set; }
        public int LineCount { get; }
        public long CharCountWithoutLineEndings { get; }
        public int MaxLineLength { get; }

        public void ResetCounts()
        {
            LineReads = 0;
            LengthReads = 0;
        }

        public string GetLine(int line)
        {
            LineReads++;
            return _line;
        }

        public int GetLineLength(int line)
        {
            LengthReads++;
            return MaxLineLength;
        }

        public string GetLineSegment(int line, int startColumn, int length)
        {
            LineReads++;
            if (length <= 0 || startColumn >= MaxLineLength)
                return "";

            return _line.Substring(startColumn, Math.Min(length, MaxLineLength - startColumn));
        }

        public IEnumerable<string> EnumerateLines(int startLine, int count, bool cache = true)
        {
            for (int i = 0; i < count && startLine + i < LineCount; i++)
            {
                LineReads++;
                yield return _line;
            }
        }

        public int GetMaxLineLength(int startLine, int count) => MaxLineLength;
        public long GetCharCountWithoutLineEndings(int startLine, int count) => (long)count * MaxLineLength;
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

    private static void InvokePrivate(object instance, string name, params object[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(instance, args);
    }
}
