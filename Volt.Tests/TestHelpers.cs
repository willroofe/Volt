namespace Volt.Tests;

internal static class TestHelpers
{
    public static TextBuffer MakeBuffer(params string[] lines)
    {
        var buf = new TextBuffer();
        buf.SetContent(string.Join("\n", lines), tabSize: 4);
        return buf;
    }
}
