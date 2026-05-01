using System.Collections;
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
    private ushort[] _charToGlyphMap = Array.Empty<ushort>();

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

        var (resolvedTypeface, resolvedGlyphTypeface) = ResolveTypeface(familyName, weight);
        var resolvedWeight = resolvedTypeface.Weight;
        double dpi = dpiOverride ?? VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;

        BeforeFontChanged?.Invoke();
        _fontWeight = resolvedWeight;
        _monoTypeface = resolvedTypeface;
        _glyphTypeface = resolvedGlyphTypeface;
        _fontSize = size;
        Dpi = dpi;

        BuildCharToGlyphMap();

        var sample = new FormattedText("X", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _monoTypeface, _fontSize, Brushes.White, Dpi);
        CharWidth = Math.Round(sample.WidthIncludingTrailingWhitespace * Dpi) / Dpi;
        LineHeight = Math.Round(sample.Height * _lineHeightMultiplier * Dpi) / Dpi;
        GlyphBaseline = sample.Baseline;
        _uniformAdvanceWidths = Array.Empty<double>();

        FontChanged?.Invoke();
    }

    private static (Typeface Typeface, GlyphTypeface GlyphTypeface) ResolveTypeface(string familyName, FontWeight weight)
    {
        if (TryResolveTypeface(familyName, weight, out var typeface, out var glyphTypeface))
            return (typeface, glyphTypeface);

        if (TryResolveTypeface("Consolas", weight, out typeface, out glyphTypeface))
            return (typeface, glyphTypeface);

        if (TryResolveTypeface("Consolas", FontWeights.Normal, out typeface, out glyphTypeface))
            return (typeface, glyphTypeface);

        throw new InvalidOperationException("No usable glyph typeface was found for editor rendering.");
    }

    private static bool TryResolveTypeface(string familyName, FontWeight weight,
        out Typeface typeface, out GlyphTypeface glyphTypeface)
    {
        typeface = new Typeface(new FontFamily(familyName), FontStyles.Normal, weight, FontStretches.Normal);
        if (typeface.TryGetGlyphTypeface(out var requestedGlyphTypeface) && requestedGlyphTypeface != null)
        {
            glyphTypeface = requestedGlyphTypeface;
            return true;
        }

        try
        {
            foreach (var face in new FontFamily(familyName).GetTypefaces())
            {
                if (face.TryGetGlyphTypeface(out var faceGlyphTypeface) && faceGlyphTypeface != null)
                {
                    typeface = face;
                    glyphTypeface = faceGlyphTypeface;
                    return true;
                }
            }
        }
        catch
        {
            // Fall back to Consolas in ResolveTypeface.
        }

        glyphTypeface = null!;
        return false;
    }

    private void BuildCharToGlyphMap()
    {
        _charToGlyphMap = new ushort[ushort.MaxValue + 1];
        foreach (var kv in _glyphTypeface.CharacterToGlyphMap)
        {
            if ((uint)kv.Key <= char.MaxValue)
                _charToGlyphMap[kv.Key] = kv.Value;
        }
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
        var glyphIndices = new GlyphIndexList(text, startIndex, length, _charToGlyphMap);
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

internal sealed class GlyphIndexList : IList<ushort>
{
    private readonly string _text;
    private readonly int _startIndex;
    private readonly int _count;
    private readonly ushort[] _charToGlyphMap;

    public GlyphIndexList(string text, int startIndex, int count, ushort[] charToGlyphMap)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(charToGlyphMap);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (startIndex > text.Length || count > text.Length - startIndex)
            throw new ArgumentOutOfRangeException(nameof(count));

        _text = text;
        _startIndex = startIndex;
        _count = count;
        _charToGlyphMap = charToGlyphMap;
    }

    public int Count => _count;
    public bool IsReadOnly => true;

    public ushort this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _charToGlyphMap[_text[_startIndex + index]];
        }
        set => throw new NotSupportedException();
    }

    public int IndexOf(ushort item)
    {
        for (int i = 0; i < _count; i++)
            if (this[i] == item)
                return i;
        return -1;
    }

    public bool Contains(ushort item) => IndexOf(item) >= 0;

    public void CopyTo(ushort[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        if (array.Length - arrayIndex < _count)
            throw new ArgumentException("Destination array is too small.", nameof(array));

        for (int i = 0; i < _count; i++)
            array[arrayIndex + i] = this[i];
    }

    public IEnumerator<ushort> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(ushort item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, ushort item) => throw new NotSupportedException();
    public bool Remove(ushort item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
