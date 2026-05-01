using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Volt.Tests;

public class GlyphIndexListTests
{
    [StaFact]
    public void DrawGlyphRun_RendersNormalAndLongText()
    {
        var font = new FontManager();
        font.Apply("Consolas", 14, FontWeights.Normal, dpiOverride: 1.0);
        var visual = new DrawingVisual();

        using var dc = visual.RenderOpen();
        font.DrawGlyphRun(dc, "hello", 0, 5, 0, 0, Brushes.White);
        font.DrawGlyphRun(dc, new string('x', 300), 0, 300, 0, font.LineHeight, Brushes.White);
    }

    [Fact]
    public void GlyphIndexList_MapsCharactersThroughCapturedGlyphMap()
    {
        var glyphMap = new ushort[char.MaxValue + 1];
        glyphMap['A'] = 7;
        glyphMap['Z'] = 26;
        var replacementMap = new ushort[char.MaxValue + 1];
        replacementMap['A'] = 99;

        var list = new GlyphIndexList("xAZ", 1, 2, glyphMap);
        glyphMap = replacementMap;

        Assert.Equal(2, list.Count);
        Assert.True(list.IsReadOnly);
        Assert.Equal((ushort)7, list[0]);
        Assert.Equal((ushort)26, list[1]);
        Assert.Equal(1, list.IndexOf(26));
        Assert.Contains((ushort)7, list);
        Assert.Throws<NotSupportedException>(() => list[0] = 1);
    }
}
