using Volt;
using Xunit;

namespace Volt.Tests;

public class RecentItemDisplayTests
{
    [Theory]
    [InlineData(RecentItemKind.Folder, @"C:\", @"C:\")]
    [InlineData(RecentItemKind.File, @"C:\work\notes.txt", @"notes.txt - C:\work")]
    [InlineData(RecentItemKind.Folder, @"C:\work\Project", @"Project - C:\work")]
    [InlineData(RecentItemKind.Workspace, @"C:\work\Project.volt-workspace", @"Project - C:\work")]
    public void GetMenuLabel_FormatsRecentItems(RecentItemKind kind, string path, string expected)
    {
        var recent = new RecentItem { Kind = kind, Path = path };

        string label = RecentItemDisplay.GetMenuLabel(recent);

        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData(RecentItemKind.Folder, @"C:\", @"C:\ (Folder)")]
    [InlineData(RecentItemKind.File, @"C:\work\notes.txt", @"notes.txt - C:\work")]
    [InlineData(RecentItemKind.Folder, @"C:\work\Project", @"Project (Folder) - C:\work")]
    [InlineData(RecentItemKind.Workspace, @"C:\work\Project.volt-workspace", @"Project (Workspace) - C:\work")]
    public void GetPaletteLabel_FormatsRecentItems(RecentItemKind kind, string path, string expected)
    {
        var recent = new RecentItem { Kind = kind, Path = path };

        string label = RecentItemDisplay.GetPaletteLabel(recent);

        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData(RecentItemKind.File, Codicons.File)]
    [InlineData(RecentItemKind.Folder, Codicons.FolderOpened)]
    [InlineData(RecentItemKind.Workspace, Codicons.Project)]
    public void GetIconGlyph_MapsRecentItemKinds(RecentItemKind kind, string expected)
    {
        string glyph = RecentItemDisplay.GetIconGlyph(kind);

        Assert.Equal(expected, glyph);
    }
}
