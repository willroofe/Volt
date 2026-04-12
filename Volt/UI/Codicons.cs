using System.Windows;
using System.Windows.Media;

namespace Volt;

/// <summary>
/// Visual Studio Code <see href="https://github.com/microsoft/vscode-codicons">Codicons</see>
/// (@vscode/codicons 0.0.45). Icons are CC-BY-4.0; see <c>Resources/Fonts/Codicons-LICENSE.txt</c>.
/// Lookup: https://microsoft.github.io/vscode-codicons/dist/codicon.html
/// </summary>
internal static class Codicons
{
    /// <summary>
    /// WPF does not reliably resolve a single-string <c>pack://.../file.ttf#name</c> font reference.
    /// </summary>
    public static FontFamily Font => _font ??= CreateFont();

    private static FontFamily? _font;

    private static Typeface? _iconTypeface;

    /// <summary>Typeface for <see cref="FormattedText"/> / custom drawing (e.g. file tree).</summary>
    public static Typeface IconTypeface =>
        _iconTypeface ??= new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static FontFamily CreateFont()
    {
        var assemblyShortName = typeof(Codicons).Assembly.GetName().Name!;
        var baseUri = new Uri($"pack://application:,,,/{assemblyShortName};component/", UriKind.Absolute);
        return new FontFamily(baseUri, "./Resources/Fonts/codicon.ttf#codicon");
    }

    public const string Add = "\uEA60";
    public const string Check = "\uEAB2";
    public const string ChevronDown = "\uEAB4";
    public const string ChevronLeft = "\uEAB5";
    public const string ChevronRight = "\uEAB6";
    public const string ChevronUp = "\uEAB7";
    public const string ChromeClose = "\uEAB8";
    public const string ChromeMaximize = "\uEAB9";
    public const string ChromeMinimize = "\uEABA";
    public const string ChromeRestore = "\uEABB";
    public const string Close = "\uEA76";
    public const string Copy = "\uEBCC";
    public const string Clippy = "\uEAC0";
    public const string File = "\uEA7B";
    // File-type glyphs: vscode-codicons mapping.json keys are DECIMAL Unicode scalars (same as char code unit for BMP).
    // Wrong hex (e.g. guessing from nearby icons) maps to unrelated symbols — always key → U+{key:X4}.
    /// <summary>Plain document / log lines (codicon <c>file-text</c>, decimal 60510).</summary>
    public const string FileText = "\uEC5E";
    public const string FileCode = "\uEAE9";
    public const string FileBinary = "\uEAE8";
    public const string FileMedia = "\uEAEA";
    public const string FilePdf = "\uEAEB";
    public const string FileZip = "\uEAEF";
    public const string Json = "\uEB0F";
    public const string Markdown = "\uEB1D";
    public const string Python = "\uEC39";
    public const string Ruby = "\uEB48";
    public const string Database = "\uEACE";
    public const string Github = "\uEA84";
    public const string Package = "\uEB29";
    public const string Notebook = "\uEBAF";
    public const string Table = "\uEBB7";
    public const string Lock = "\uEA75";
    public const string Folder = "\uEA83";
    public const string FolderOpened = "\uEAF7";
    public const string Info = "\uEA74";
    public const string ListSelection = "\uEB85";
    public const string Loading = "\uEB19";
    public const string NewFile = "\uEA7F";
    public const string NewFolder = "\uEA80";
    public const string Project = "\uEB30";
    public const string Refresh = "\uEB37";
    public const string Search = "\uEA6D";
    public const string Rename = "\uEC61";
    public const string Save = "\uEB4B";
    public const string ScreenCut = "\uEC7F";
    public const string Settings = "\uEB52";
    public const string Terminal = "\uEA85";
    public const string Trash = "\uEA81";
}
