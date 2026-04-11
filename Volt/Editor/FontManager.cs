using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Volt;

/// <summary>
/// Manages editor font state, metrics computation, and glyph rendering.
/// Owns the typeface, glyph metrics, DPI, and the monospace font cache.
/// </summary>
public class FontManager
{
    private const double DefaultFontSize = 14;
    private static readonly FontWeightConverter _fontWeightConverter = new();

    private Typeface _monoTypeface = null!;
    private GlyphTypeface _glyphTypeface = null!;
    private double _fontSize = DefaultFontSize;
    private FontWeight _fontWeight = FontWeights.Normal;
    private double _lineHeightMultiplier = 1.0;
    private double[] _uniformAdvanceWidths = Array.Empty<double>();

    public double CharWidth { get; private set; }
    public double LineHeight { get; private set; }
    public double GlyphBaseline { get; private set; }
    public double Dpi { get; set; } = 1.0;
    public double FontSize => _fontSize;

    public string FontFamilyName
    {
        get => _monoTypeface.FontFamily.Source;
        set => Apply(value, _fontSize, _fontWeight, null);
    }

    public double EditorFontSize
    {
        get => _fontSize;
        set => Apply(_monoTypeface.FontFamily.Source, value, _fontWeight, null);
    }

    public double LineHeightMultiplier
    {
        get => _lineHeightMultiplier;
        set
        {
            if (Math.Abs(value - _lineHeightMultiplier) < 0.001) return;
            _lineHeightMultiplier = value;
            RecalcLineHeight();
        }
    }

    public string EditorFontWeight
    {
        get => _fontWeightConverter.ConvertToString(_fontWeight) ?? "Normal";
        set
        {
            try
            {
                var fw = (FontWeight)_fontWeightConverter.ConvertFromString(value)!;
                Apply(_monoTypeface.FontFamily.Source, _fontSize, fw, null);
            }
            catch (Exception)
            {
                Apply(_monoTypeface.FontFamily.Source, _fontSize, FontWeights.Normal, null);
            }
        }
    }

    /// <summary>
    /// Called before Apply() changes metrics so the editor can snapshot scroll state.
    /// </summary>
    public event Action? BeforeFontChanged;

    /// <summary>
    /// Called after Apply() so the editor can respond to metrics changes.
    /// </summary>
    public event Action? FontChanged;

    public FontManager()
    {
        Apply(DefaultFontFamily(), DefaultFontSize, FontWeights.Normal, null);
    }

    /// <param name="dpiOverride">When set, used for metric layout instead of querying a <see cref="DrawingVisual"/>.</param>
    public void Apply(string familyName, double size, FontWeight weight, double? dpiOverride = null)
    {
        if (_monoTypeface != null! && familyName == _monoTypeface.FontFamily.Source
            && Math.Abs(size - _fontSize) < 0.001 && weight == _fontWeight
            && (dpiOverride == null || Math.Abs(Dpi - dpiOverride.Value) < 0.0001)) return;
        BeforeFontChanged?.Invoke();
        _fontWeight = weight;
        _monoTypeface = new Typeface(new FontFamily(familyName), FontStyles.Normal, weight, FontStretches.Normal);
        _fontSize = size;
        Dpi = dpiOverride ?? VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;

        GlyphTypeface? gt = null;
        if (!_monoTypeface.TryGetGlyphTypeface(out gt))
        {
            foreach (var face in new FontFamily(familyName).GetTypefaces())
                if (face.TryGetGlyphTypeface(out gt)) break;
        }
        // Fall back to Consolas if the requested font has no glyph typeface
        if (gt == null)
        {
            var fallback = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            fallback.TryGetGlyphTypeface(out gt);
        }
        if (gt != null) _glyphTypeface = gt;

        var sample = new FormattedText("X", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _monoTypeface, _fontSize, Brushes.White, Dpi);
        CharWidth = Math.Round(sample.WidthIncludingTrailingWhitespace * Dpi) / Dpi;
        LineHeight = Math.Round(sample.Height * _lineHeightMultiplier * Dpi) / Dpi;
        GlyphBaseline = sample.Baseline;
        _uniformAdvanceWidths = Array.Empty<double>();

        FontChanged?.Invoke();
    }

    private void RecalcLineHeight()
    {
        BeforeFontChanged?.Invoke();
        var sample = new FormattedText("X", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _monoTypeface, _fontSize, Brushes.White, Dpi);
        LineHeight = Math.Round(sample.Height * _lineHeightMultiplier * Dpi) / Dpi;
        FontChanged?.Invoke();
    }

    public void DrawGlyphRun(DrawingContext dc, string text, int startIndex, int length,
                              double x, double y, Brush brush)
    {
        if (length <= 0) return;
        var map = _glyphTypeface.CharacterToGlyphMap;
        // Must allocate per call — GlyphRun retains a reference to the array
        var glyphIndices = new ushort[length];
        for (int i = 0; i < length; i++)
        {
            char ch = text[startIndex + i];
            glyphIndices[i] = map.TryGetValue(ch, out var gi) ? gi : (ushort)0;
        }
        if (_uniformAdvanceWidths.Length < length)
        {
            _uniformAdvanceWidths = new double[Math.Max(length, 256)];
            Array.Fill(_uniformAdvanceWidths, CharWidth);
        }
        double snapX = Math.Round(x * Dpi) / Dpi;
        double snapY = Math.Round((y + GlyphBaseline) * Dpi) / Dpi;
        var origin = new Point(snapX, snapY);
        var run = new GlyphRun(
            _glyphTypeface, 0, false, _fontSize, (float)Dpi,
            glyphIndices, origin, new ArraySegment<double>(_uniformAdvanceWidths, 0, length),
            null, null, null, null, null, null);
        dc.DrawGlyphRun(brush, run);
    }

    // ── Monospace font discovery ────────────────────────────────────

    private static List<string>? _monoFontCache;

    public static List<string> GetMonospaceFonts()
    {
        if (_monoFontCache != null) return _monoFontCache;

        var dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        var mono = new List<string>();
        foreach (var family in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
        {
            try
            {
                var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var narrow = new FormattedText("i", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, DefaultFontSize, Brushes.White, dpi);
                var wide = new FormattedText("M", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, DefaultFontSize, Brushes.White, dpi);
                if (Math.Abs(narrow.WidthIncludingTrailingWhitespace - wide.WidthIncludingTrailingWhitespace) < 0.01)
                    mono.Add(family.Source);
            }
            catch (Exception) { }
        }
        _monoFontCache = mono;
        return _monoFontCache;
    }

    public static string DefaultFontFamily()
    {
        return Fonts.SystemFontFamilies.Any(f =>
            f.Source.Equals("Cascadia Code", StringComparison.OrdinalIgnoreCase))
            ? "Cascadia Code" : "Consolas";
    }
}
