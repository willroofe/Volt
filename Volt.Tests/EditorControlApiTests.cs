using System.Reflection;
using Xunit;

namespace Volt.Tests;

public class EditorControlApiTests
{
    [Fact]
    public void EditorControl_UsesExplicitEditorInvalidationApi()
    {
        var hiddenInvalidate = typeof(EditorControl).GetMethod(
            "InvalidateVisual",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        var editorInvalidate = typeof(EditorControl).GetMethod(
            nameof(EditorControl.InvalidateEditorVisual),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.Null(hiddenInvalidate);
        Assert.NotNull(editorInvalidate);
    }
}
