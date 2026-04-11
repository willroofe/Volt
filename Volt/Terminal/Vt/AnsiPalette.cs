using System.Windows.Media;

namespace Volt;

public static class AnsiPalette
{
    // Built-in fallback palette (xterm defaults) used when theme has no terminal section
    // or App/ThemeManager is not initialized (e.g. in unit tests).
    private static readonly uint[] FallbackAnsi16 = new uint[]
    {
        0xFF000000, 0xFFCD0000, 0xFF00CD00, 0xFFCDCD00,
        0xFF0000EE, 0xFFCD00CD, 0xFF00CDCD, 0xFFE5E5E5,
        0xFF7F7F7F, 0xFFFF0000, 0xFF00FF00, 0xFFFFFF00,
        0xFF5C5CFF, 0xFFFF00FF, 0xFF00FFFF, 0xFFFFFFFF,
    };

    private static readonly uint[] XtermCube = BuildXtermCube();

    /// <summary>
    /// Resolves a palette index to a Color. 0..15 uses theme ANSI table (or fallback),
    /// 16..255 uses the fixed xterm 256-color cube + grayscale ramp. Out-of-range indices
    /// return the default foreground color.
    /// </summary>
    public static Color ResolveDefault(int index)
    {
        if (index < 0) return Color.FromRgb(0xD4, 0xD4, 0xD4);
        if (index < 16) return FromTheme(index) ?? Unpack(FallbackAnsi16[index]);
        if (index < 256) return Unpack(XtermCube[index]);
        return Color.FromRgb(0xD4, 0xD4, 0xD4);
    }

    public static Color ResolveTrueColor(uint argb) => Unpack(argb);

    public static Color DefaultFg()
    {
        var tm = App.Current?.ThemeManager;
        var hex = tm?.TerminalColors?.Foreground;
        return ParseHex(hex) ?? Color.FromRgb(0xD4, 0xD4, 0xD4);
    }

    public static Color DefaultBg()
    {
        var tm = App.Current?.ThemeManager;
        var hex = tm?.TerminalColors?.Background;
        return ParseHex(hex) ?? Color.FromRgb(0x1E, 0x1E, 0x1E);
    }

    private static Color? FromTheme(int index)
    {
        var tm = App.Current?.ThemeManager;
        var arr = tm?.TerminalColors?.Ansi;
        if (arr == null || index >= arr.Length) return null;
        return ParseHex(arr[index]);
    }

    private static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var obj = ColorConverter.ConvertFromString(hex);
            return obj is Color c ? c : (Color?)null;
        }
        catch { return null; }
    }

    private static Color Unpack(uint argb)
        => Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

    private static uint[] BuildXtermCube()
    {
        var arr = new uint[256];
        for (int i = 0; i < 16; i++) arr[i] = FallbackAnsi16[i];
        int[] steps = { 0, 95, 135, 175, 215, 255 };
        int idx = 16;
        for (int r = 0; r < 6; r++)
            for (int g = 0; g < 6; g++)
                for (int b = 0; b < 6; b++)
                    arr[idx++] = 0xFF000000u | ((uint)steps[r] << 16) | ((uint)steps[g] << 8) | (uint)steps[b];
        for (int i = 0; i < 24; i++)
        {
            int v = 8 + i * 10;
            arr[232 + i] = 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v;
        }
        return arr;
    }
}
