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
        string name = kind == RecentItemKind.Workspace
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(path);
        string? directory = Path.GetDirectoryName(path);

        if (!includeKind || kind == RecentItemKind.File)
            return name + " - " + directory;

        return name + " (" + kind + ") - " + directory;
    }
}
