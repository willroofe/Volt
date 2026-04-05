using Volt;
using Xunit;

namespace Volt.Tests;

public class RecentItemsTests
{
    [Fact]
    public void AddRecentItem_InsertsAtFront()
    {
        var settings = new AppSettings();
        settings.AddRecentItem(@"C:\a.txt", RecentItemKind.File);
        settings.AddRecentItem(@"C:\b.txt", RecentItemKind.File);

        Assert.Equal(2, settings.Application.RecentItems.Count);
        Assert.Equal(@"C:\b.txt", settings.Application.RecentItems[0].Path);
        Assert.Equal(@"C:\a.txt", settings.Application.RecentItems[1].Path);
    }

    [Fact]
    public void AddRecentItem_DeduplicatesMoveToFront()
    {
        var settings = new AppSettings();
        settings.AddRecentItem(@"C:\a.txt", RecentItemKind.File);
        settings.AddRecentItem(@"C:\b.txt", RecentItemKind.File);
        settings.AddRecentItem(@"C:\a.txt", RecentItemKind.File);

        Assert.Equal(2, settings.Application.RecentItems.Count);
        Assert.Equal(@"C:\a.txt", settings.Application.RecentItems[0].Path);
    }

    [Fact]
    public void AddRecentItem_DeduplicatesCaseInsensitive()
    {
        var settings = new AppSettings();
        settings.AddRecentItem(@"C:\Folder\file.txt", RecentItemKind.File);
        settings.AddRecentItem(@"C:\folder\FILE.TXT", RecentItemKind.File);

        Assert.Single(settings.Application.RecentItems);
    }

    [Fact]
    public void AddRecentItem_CapsAtTen()
    {
        var settings = new AppSettings();
        for (int i = 0; i < 15; i++)
            settings.AddRecentItem($@"C:\file{i}.txt", RecentItemKind.File);

        Assert.Equal(10, settings.Application.RecentItems.Count);
        // Most recent should be first
        Assert.Equal(@"C:\file14.txt", settings.Application.RecentItems[0].Path);
    }

    [Fact]
    public void AddRecentItem_PreservesKind()
    {
        var settings = new AppSettings();
        settings.AddRecentItem(@"C:\project", RecentItemKind.Folder);
        settings.AddRecentItem(@"C:\work.volt-workspace", RecentItemKind.Workspace);

        Assert.Equal(RecentItemKind.Workspace, settings.Application.RecentItems[0].Kind);
        Assert.Equal(RecentItemKind.Folder, settings.Application.RecentItems[1].Kind);
    }

    [Fact]
    public void AddRecentItem_SamePathDifferentKind_BothKept()
    {
        var settings = new AppSettings();
        settings.AddRecentItem(@"C:\test.volt-workspace", RecentItemKind.Workspace);
        settings.AddRecentItem(@"C:\test.volt-workspace", RecentItemKind.File);

        Assert.Equal(2, settings.Application.RecentItems.Count);
        Assert.Equal(RecentItemKind.File, settings.Application.RecentItems[0].Kind);
        Assert.Equal(RecentItemKind.Workspace, settings.Application.RecentItems[1].Kind);
    }

    [Fact]
    public void AddRecentItem_SamePathSameKind_Deduplicates()
    {
        var settings = new AppSettings();
        settings.AddRecentItem(@"C:\test.volt-workspace", RecentItemKind.Workspace);
        settings.AddRecentItem(@"C:\other.txt", RecentItemKind.File);
        settings.AddRecentItem(@"C:\test.volt-workspace", RecentItemKind.Workspace);

        Assert.Equal(2, settings.Application.RecentItems.Count);
        Assert.Equal(RecentItemKind.Workspace, settings.Application.RecentItems[0].Kind);
    }
}
