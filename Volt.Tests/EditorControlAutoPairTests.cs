using System.Reflection;
using Volt;
using Xunit;

namespace Volt.Tests;

public class EditorControlAutoPairTests
{
    [StaFact]
    public void TextInput_AutoClosesOpeningPairs()
    {
        (string input, string expected, int caretCol)[] cases =
        [
            ("{", "{}", 1),
            ("(", "()", 1),
            ("[", "[]", 1),
            ("\"", "\"\"", 1),
            ("'", "''", 1),
        ];

        foreach ((string input, string expected, int caretCol) in cases)
        {
            var editor = new EditorControl(new ThemeManager(), new LanguageManager());

            InvokePrivate(editor, "HandleTextInput", input);

            Assert.Equal(expected, editor.GetContent());
            Assert.Equal(caretCol, editor.CaretCol);
        }
    }

    [StaFact]
    public void TextInput_OvertypesGeneratedCloser()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());

        InvokePrivate(editor, "HandleTextInput", "(");
        InvokePrivate(editor, "HandleTextInput", ")");

        Assert.Equal("()", editor.GetContent());
        Assert.Equal(2, editor.CaretCol);

        editor.SetContent("");
        InvokePrivate(editor, "HandleTextInput", "\"");
        InvokePrivate(editor, "HandleTextInput", "\"");

        Assert.Equal("\"\"", editor.GetContent());
        Assert.Equal(2, editor.CaretCol);
    }

    [StaFact]
    public void Backspace_BetweenEmptyPairDeletesBothCharacters()
    {
        string[] inputs = ["{", "(", "[", "\"", "'"];

        foreach (string input in inputs)
        {
            var editor = new EditorControl(new ThemeManager(), new LanguageManager());
            InvokePrivate(editor, "HandleTextInput", input);

            InvokePrivate(editor, "HandleBackspace");

            Assert.Equal("", editor.GetContent());
            Assert.Equal(0, editor.CaretCol);
        }
    }

    [StaFact]
    public void Return_BetweenBracketPairCreatesIndentedMiddleLine()
    {
        (string input, string expected)[] cases =
        [
            ("{", "{\n    \n}"),
            ("(", "(\n    \n)"),
            ("[", "[\n    \n]"),
            ("\"", "\"\n    \n\""),
            ("'", "'\n    \n'"),
        ];

        foreach ((string input, string expected) in cases)
        {
            var editor = new EditorControl(new ThemeManager(), new LanguageManager());

            InvokePrivate(editor, "HandleTextInput", input);
            InvokePrivate(editor, "HandleReturn");

            Assert.Equal(expected, NormalizeLineEndings(editor.GetContent()));
            Assert.Equal(1, editor.CaretLine);
            Assert.Equal(4, editor.CaretCol);
        }
    }

    [StaFact]
    public void TextInput_DoesNotAutoCloseInsideString()
    {
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetContent("\"abc\"");
        editor.SetCaretPosition(0, 2);

        InvokePrivate(editor, "HandleTextInput", "{");

        Assert.Equal("\"a{bc\"", editor.GetContent());
        Assert.Equal(3, editor.CaretCol);
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void InvokePrivate(object instance, string name, params object[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(instance, args);
    }
}
