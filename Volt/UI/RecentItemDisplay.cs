using System.IO;

namespace Volt;

internal static class RecentItemDisplay
{
    public static string GetMenuLabel(RecentItem recent) => FormatLabel(recent.Path, recent.Kind, includeKind: false);

    public static string GetPaletteLabel(RecentItem recent) => FormatLabel(recent.Path, recent.Kind, includeKind: true);

    public static string GetIconGlyph(RecentItemKind kind) => kind switch
    {
        RecentItemKind.Folder => Codicons.FolderOpened,
        RecentItemKind.Workspace => Codicons.Project,
        _ => Codicons.File
    };

    private static string FormatLabel(string path, RecentItemKind kind, bool includeKind)
    {
        string displayPath = Path.TrimEndingDirectorySeparator(path);
        string name = kind == RecentItemKind.Workspace
            ? Path.GetFileNameWithoutExtension(displayPath)
            : Path.GetFileName(displayPath);
        string? directory = Path.GetDirectoryName(displayPath);

        if (string.IsNullOrEmpty(name))
        {
            name = displayPath;
            directory = null;
        }

        if (includeKind && kind != RecentItemKind.File)
            name += " (" + kind + ")";

        return string.IsNullOrEmpty(directory)
            ? name
            : name + " - " + directory;
    }
}
