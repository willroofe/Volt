using System.Reflection;
using Volt;
using Xunit;

namespace Volt.Tests;

public class EditorControlFindTests
{
    [StaFact]
    public async Task FindNext_WithPreserveSelection_KeepsFindInSelectionBounds()
    {
        const string selectedLine = "needle needle";
        var editor = new EditorControl(new ThemeManager(), new LanguageManager());
        editor.SetContent(selectedLine + "\noutside needle");

        SelectionManager selection = GetPrivateField<SelectionManager>(editor, "_selection");
        selection.AnchorLine = 0;
        selection.AnchorCol = 0;
        selection.HasSelection = true;
        SetPrivateField(editor, "_caretLine", 0);
        SetPrivateField(editor, "_caretCol", selectedLine.Length);

        editor.SetFindMatches("needle", matchCase: false, selectionBounds: (0, 0, 0, selectedLine.Length),
            preserveSelection: true);
        await WaitUntil(() => editor.FindStatusText == "1 of 2");

        editor.FindNext(preserveSelection: true);
        await WaitUntil(() => editor.FindStatusText == "2 of 2");

        Assert.True(selection.HasSelection);
        Assert.Equal((0, 0, 0, selectedLine.Length), editor.GetSelectionBounds());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
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
}
