using System;

namespace Volt;

[Flags]
public enum CellAttr : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Inverse = 1 << 4,
    Strikethrough = 1 << 5,
}

/// <summary>
/// One terminal grid cell. FgIndex/BgIndex semantics:
/// -1            = default (theme fg/bg)
/// 0..15         = ANSI 16 palette (from theme)
/// 16..255       = xterm 256 cube + grayscale ramp
/// &lt;-1         = truecolor; -(index+2) into TerminalGrid's truecolor side-table
/// </summary>
public struct Cell
{
    public char Glyph;
    public int FgIndex;
    public int BgIndex;
    public CellAttr Attr;

    public Cell()
    {
        Glyph = '\0';
        FgIndex = -1;
        BgIndex = -1;
        Attr = CellAttr.None;
    }

    public static Cell Blank => new Cell { Glyph = ' ', FgIndex = -1, BgIndex = -1, Attr = CellAttr.None };
}
