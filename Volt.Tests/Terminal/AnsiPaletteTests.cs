using System.Windows.Media;
using Xunit;
using Volt;

namespace Volt.Tests.Terminal;

public class AnsiPaletteTests
{
    [Fact]
    public void Resolve_AnsiIndex0_ReturnsBlack()
    {
        var c = AnsiPalette.ResolveDefault(0);
        Assert.Equal((byte)0, c.R);
        Assert.Equal((byte)0, c.G);
        Assert.Equal((byte)0, c.B);
    }

    [Fact]
    public void Resolve_XtermCube_Index16_IsBlack()
    {
        var c = AnsiPalette.ResolveDefault(16);
        Assert.Equal((byte)0, c.R);
    }

    [Fact]
    public void Resolve_XtermCube_Index231_IsNearWhite()
    {
        var c = AnsiPalette.ResolveDefault(231);
        Assert.Equal((byte)0xFF, c.R);
        Assert.Equal((byte)0xFF, c.G);
        Assert.Equal((byte)0xFF, c.B);
    }

    [Fact]
    public void Resolve_Grayscale_Index232_IsDarkGray()
    {
        var c = AnsiPalette.ResolveDefault(232);
        Assert.Equal((byte)8, c.R);
    }

    [Fact]
    public void Resolve_Grayscale_Index255_IsLightGray()
    {
        var c = AnsiPalette.ResolveDefault(255);
        Assert.Equal((byte)238, c.R);
    }

    [Fact]
    public void ResolveTrueColor_UnpacksArgb()
    {
        uint argb = 0xFF102030;
        var c = AnsiPalette.ResolveTrueColor(argb);
        Assert.Equal((byte)0x10, c.R);
        Assert.Equal((byte)0x20, c.G);
        Assert.Equal((byte)0x30, c.B);
    }
}
