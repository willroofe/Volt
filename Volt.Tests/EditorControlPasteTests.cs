using System.Reflection;
using Volt;
using Xunit;

namespace Volt.Tests;

public class EditorControlPasteTests
{
    [StaFact]
    public void PasteText_MultilinePastePreservesCaretAndUndo()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetContent("abXY");
        editor.SetCaretPosition(0, 2);

        InvokePrivate(editor, "PasteText", "1\n2\n");

        Assert.Equal("ab1\n2\nXY", NormalizeLineEndings(editor.GetContent()));
        Assert.Equal(2, editor.CaretLine);
        Assert.Equal(0, editor.CaretCol);

        InvokePrivate(editor, "Undo");

        Assert.Equal("abXY", editor.GetContent());
        Assert.Equal(0, editor.CaretLine);
        Assert.Equal(2, editor.CaretCol);

        InvokePrivate(editor, "Redo");

        Assert.Equal("ab1\n2\nXY", NormalizeLineEndings(editor.GetContent()));
        Assert.Equal(2, editor.CaretLine);
        Assert.Equal(0, editor.CaretCol);
    }

    [StaFact]
    public void PasteText_LargeMultilinePasteUsesBatchReplacement()
    {
        const int lineCount = 20_000;
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetContent("prefixsuffix");
        editor.SetCaretPosition(0, "prefix".Length);
        string paste = string.Join("\n", Enumerable.Range(0, lineCount).Select(i => $"line{i}"));

        InvokePrivate(editor, "PasteText", paste);

        TextBuffer buffer = GetPrivateField<TextBuffer>(editor, "_buffer");
        Assert.Equal(lineCount, buffer.Count);
        Assert.Equal("prefixline0", buffer[0]);
        Assert.Equal($"line{lineCount - 1}suffix", buffer[lineCount - 1]);
        Assert.Equal(lineCount - 1, editor.CaretLine);
        Assert.Equal($"line{lineCount - 1}".Length, editor.CaretCol);
        Assert.True(GetPieceCount(buffer) <= 3);
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static T GetPrivateField<T>(object instance, string name)
    {
        FieldInfo? field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static int GetPieceCount(TextBuffer buffer)
    {
        FieldInfo? field = typeof(TextBuffer).GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var pieces = Assert.IsAssignableFrom<System.Collections.ICollection>(field.GetValue(buffer));
        return pieces.Count;
    }

    private static void InvokePrivate(object instance, string name, params object[] args)
    {
        MethodInfo? method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(instance, args);
    }
}
