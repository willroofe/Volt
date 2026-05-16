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
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());

        editor.SetBusy(true, "Loading file...");

        Assert.True(editor.IsBusy);

        editor.SetBusy(false);

        Assert.False(editor.IsBusy);
    }

    [StaFact]
    public void SetBusy_WithProgressClampsAndClearsProgress()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());

        editor.SetBusy(true, "Loading file...", 125);

        Assert.True(editor.IsBusy);
        Assert.Equal(100, editor.BusyProgressPercent);

        editor.SetBusy(false);

        Assert.False(editor.IsBusy);
        Assert.Null(editor.BusyProgressPercent);
    }

    [StaFact]
    public void SetBusy_WithProgressRendersOverlay()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        var size = new Size(640, 360);

        editor.SetBusy(true, "Indexing file...", 42.5);
        editor.Measure(size);
        editor.Arrange(new Rect(size));
        editor.UpdateLayout();

        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);

        Assert.True(editor.IsBusy);
        Assert.Equal(42.5, editor.BusyProgressPercent);
    }

    [StaFact]
    public void SetPreparedContent_SuppressesWordWrapForVeryManyLines()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager())
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
    public void SetPreparedContent_AllowsWordWrapForVeryLongLine()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager())
        {
            WordWrap = true
        };

        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = new FakeTextSource(lineCount: 1, lineLength: 500_001),
            LineEnding = "\n"
        });

        Assert.True(editor.WordWrap);
    }

    [StaFact]
    public void RenderAtHighLineNumber_KeepsRetainedVisualTransformsNearViewport()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
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
    public void RenderLongLine_DrawsHorizontalBufferForRetainedScrollReuse()
    {
        var source = new FakeTextSource(lineCount: 1, lineLength: 2_000_000);
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetPreparedContent(new TextBuffer.PreparedContent
        {
            Source = source,
            LineEnding = "\n"
        });

        var size = new Size(640, 360);
        editor.Measure(size);
        editor.Arrange(new Rect(size));
        editor.UpdateLayout();

        FontManager font = GetPrivateField<FontManager>(editor, "_font");
        const double initialOffset = 100_000;
        const double retainedScrollRatio = 0.25;
        editor.SetHorizontalOffset(initialOffset);
        InvokePrivate(editor, "RenderTextVisual", 0, 0);

        double renderedOffset = editor.HorizontalOffset;
        int visibleStartColumn = (int)Math.Floor(renderedOffset / font.CharWidth);
        int visibleEndColumn = (int)Math.Ceiling((renderedOffset + size.Width) / font.CharWidth);

        Assert.True(source.LongestSegmentStartColumn <= visibleStartColumn,
            $"start {source.LongestSegmentStartColumn} should cover visible start {visibleStartColumn}");
        Assert.True(source.LongestSegmentEndColumn >= visibleEndColumn,
            $"end {source.LongestSegmentEndColumn} should cover visible end {visibleEndColumn}");

        double leadingBufferWidth = (visibleStartColumn - source.LongestSegmentStartColumn) * font.CharWidth;
        double trailingBufferWidth = (source.LongestSegmentEndColumn - visibleEndColumn) * font.CharWidth;

        Assert.True(leadingBufferWidth >= size.Width * retainedScrollRatio);
        Assert.True(trailingBufferWidth >= size.Width * retainedScrollRatio);
    }

    [StaFact]
    public void RenderWrappedLine_RetainsTextForOffscreenWrapSegments()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager())
        {
            WordWrap = true
        };
        editor.SetContent(new string('x', 20_000));

        var size = new Size(240, 120);
        editor.Measure(size);
        editor.Arrange(new Rect(size));
        editor.UpdateLayout();

        var initialBitmap = new RenderTargetBitmap(240, 120, 96, 96, PixelFormats.Pbgra32);
        initialBitmap.Render(editor);

        FontManager font = GetPrivateField<FontManager>(editor, "_font");
        editor.SetVerticalOffset(font.LineHeight * 120);

        var scrolledBitmap = new RenderTargetBitmap(240, 120, 96, 96, PixelFormats.Pbgra32);
        scrolledBitmap.Render(editor);

        Assert.True(CountNonBackgroundPixels(scrolledBitmap, 80, 2, 230, 80) > 20);
    }

    [StaFact]
    public void TabOnHugeSelection_UsesPieceBackedUniformIndent()
    {
        const int lineCount = 1_200_000;
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
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
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
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
        public int LongestSegmentStartColumn { get; private set; } = -1;
        public int LongestSegmentLength { get; private set; }
        public int LongestSegmentEndColumn => LongestSegmentStartColumn + LongestSegmentLength;

        public string GetLine(int line) => _line;
        public int GetLineLength(int line) => MaxLineLength;

        public string GetLineSegment(int line, int startColumn, int length)
        {
            if (length <= 0 || startColumn >= MaxLineLength)
                return "";

            int count = Math.Min(length, MaxLineLength - startColumn);
            if (count >= LongestSegmentLength)
            {
                LongestSegmentStartColumn = startColumn;
                LongestSegmentLength = count;
            }

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

    private static int CountNonBackgroundPixels(BitmapSource bitmap, int x0, int y0, int x1, int y1)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        int bgIndex = (height - 2) * stride + (width - 2) * 4;
        byte bgB = pixels[bgIndex];
        byte bgG = pixels[bgIndex + 1];
        byte bgR = pixels[bgIndex + 2];

        int count = 0;
        for (int y = Math.Clamp(y0, 0, height - 1); y < Math.Clamp(y1, 0, height); y++)
        {
            for (int x = Math.Clamp(x0, 0, width - 1); x < Math.Clamp(x1, 0, width); x++)
            {
                int index = y * stride + x * 4;
                int diff = Math.Abs(pixels[index] - bgB)
                    + Math.Abs(pixels[index + 1] - bgG)
                    + Math.Abs(pixels[index + 2] - bgR);
                if (diff > 30)
                    count++;
            }
        }

        return count;
    }
}
