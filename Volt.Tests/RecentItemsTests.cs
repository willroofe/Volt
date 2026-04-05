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
        Assert.Single(settings.Application.RecentHistory);
    }

    [Fact]
    public void AddRecentItem_MenuCapsAt10()
    {
        var settings = new AppSettings();
        for (int i = 0; i < 15; i++)
            settings.AddRecentItem($@"C:\file{i}.txt", RecentItemKind.File);

        Assert.Equal(10, settings.Application.RecentItems.Count);
        Assert.Equal(@"C:\file14.txt", settings.Application.RecentItems[0].Path);
    }

    [Fact]
    public void AddRecentItem_HistoryCapsAt200()
    {
        var settings = new AppSettings();
        for (int i = 0; i < 210; i++)
            settings.AddRecentItem($@"C:\file{i}.txt", RecentItemKind.File);

        Assert.Equal(10, settings.Application.RecentItems.Count);
        Assert.Equal(200, settings.Application.RecentHistory.Count);
        Assert.Equal(@"C:\file209.txt", settings.Application.RecentHistory[0].Path);
    }

    [Fact]
    public void AddRecentItem_BothListsPopulated()
    {
        var settings = new AppSettings();
        settings.AddRecentItem(@"C:\a.txt", RecentItemKind.File);

        Assert.Single(settings.Application.RecentItems);
        Assert.Single(settings.Application.RecentHistory);
        Assert.Equal(@"C:\a.txt", settings.Application.RecentHistory[0].Path);
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

    [Fact]
    public void ClearMenuDoesNotAffectHistory()
    {
        var settings = new AppSettings();
        settings.AddRecentItem(@"C:\a.txt", RecentItemKind.File);
        settings.AddRecentItem(@"C:\b.txt", RecentItemKind.File);

        settings.Application.RecentItems.Clear();

        Assert.Empty(settings.Application.RecentItems);
        Assert.Equal(2, settings.Application.RecentHistory.Count);
    }

    [Fact]
    public void ClearHistoryDoesNotAffectMenu()
    {
        var settings = new AppSettings();
        settings.AddRecentItem(@"C:\a.txt", RecentItemKind.File);
        settings.AddRecentItem(@"C:\b.txt", RecentItemKind.File);

        settings.Application.RecentHistory.Clear();

        Assert.Equal(2, settings.Application.RecentItems.Count);
        Assert.Empty(settings.Application.RecentHistory);
    }
}
