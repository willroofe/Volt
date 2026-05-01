using System.Windows;
using Xunit;

namespace Volt.Tests;

public class FontManagerTests
{
    [StaFact]
    public void Apply_MissingFontFallsBackToUsableTypeface()
    {
        var font = new FontManager();

        font.Apply("__Volt_Missing_Font__", 14, FontWeights.Normal, dpiOverride: 1.0);

        Assert.NotEqual("__Volt_Missing_Font__", font.FontFamilyName);
        Assert.True(font.CharWidth > 0);
        Assert.True(font.LineHeight > 0);
        Assert.True(font.GlyphBaseline > 0);
    }

}
