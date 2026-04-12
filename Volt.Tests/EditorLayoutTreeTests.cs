using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Xunit;

namespace Volt.Tests;

/// <summary>
/// Pure editor layout tree helpers + snapshot DTO JSON (requires STA + minimal WPF <see cref="Application"/> for <see cref="TabInfo"/>).
/// </summary>
public class EditorLayoutTreeTests
{
    private static readonly object AppGate = new();
    private static bool _scrollTemplateRegistered;

    private static void EnsureWpfResourcesForTabInfo()
    {
        lock (AppGate)
        {
            if (_scrollTemplateRegistered)
                return;

            if (Application.Current == null)
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            // TabInfo ctor resolves ThemedScrollViewer; do not merge full App.xaml (would construct Volt.App again).
            if (Application.Current!.TryFindResource("ThemedScrollViewer") == null)
            {
                const string scrollTemplateXaml =
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                    "TargetType='ScrollViewer'><Border><ScrollContentPresenter/></Border></ControlTemplate>";
                Application.Current.Resources["ThemedScrollViewer"] =
                    (ControlTemplate)XamlReader.Parse(scrollTemplateXaml);
            }

            _scrollTemplateRegistered = true;
        }
    }

    private static TabInfo NewTab(ThemeManager tm, SyntaxManager sm) => new(tm, sm);

    private static (EditorLeafNode left, EditorLeafNode right, EditorSplitNode root) SplitTwoTabs(
        TabInfo a, TabInfo b)
    {
        var l1 = new EditorLeafNode("L1");
        l1.Tabs.Add(a);
        l1.ActiveTab = a;
        var l2 = new EditorLeafNode("L2");
        l2.Tabs.Add(b);
        l2.ActiveTab = b;
        var root = new EditorSplitNode
        {
            Orientation = EditorSplitOrientation.Vertical,
            First = l1,
            Second = l2,
            FirstPaneStarRatio = 1.0
        };
        return (l1, l2, root);
    }

    [StaFact]
    public void AllTabsOrdered_IsDepthFirst()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var t1 = NewTab(tm, sm);
        var t2 = NewTab(tm, sm);
        var t3 = NewTab(tm, sm);
        var (l1, l2, s) = SplitTwoTabs(t1, t2);
        var l3 = new EditorLeafNode("L3");
        l3.Tabs.Add(t3);
        l3.ActiveTab = t3;
        s.Second = new EditorSplitNode
        {
            Orientation = EditorSplitOrientation.Horizontal,
            First = l2,
            Second = l3,
            FirstPaneStarRatio = 1.0
        };

        var ordered = EditorLayoutTree.AllTabsOrdered(s);
        Assert.Equal(new[] { t1, t2, t3 }, ordered);
    }

    [StaFact]
    public void FindParentSplitOfLeaf_SingleLeafRoot_ReturnsNull()
    {
        var leaf = new EditorLeafNode("root");
        Assert.Null(EditorLayoutTree.FindParentSplitOfLeaf(leaf, leaf.Id));
    }

    [StaFact]
    public void FindParentSplitOfLeaf_ReturnsImmediateParent()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var (l1, l2, root) = SplitTwoTabs(NewTab(tm, sm), NewTab(tm, sm));

        Assert.Same(root, EditorLayoutTree.FindParentSplitOfLeaf(root, l1.Id));
        Assert.Same(root, EditorLayoutTree.FindParentSplitOfLeaf(root, l2.Id));
    }

    [StaFact]
    public void TryJoinFocusedLeafWithSibling_MergesSiblingTabs()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var a = NewTab(tm, sm);
        var b = NewTab(tm, sm);
        var (l1, l2, root) = SplitTwoTabs(a, b);
        EditorLayoutNode r = root;

        var ok = EditorLayoutTree.TryJoinFocusedLeafWithSibling(ref r, l2.Id, b, out var mergedId);
        Assert.True(ok);
        var leaf = Assert.IsType<EditorLeafNode>(r);
        Assert.Equal(2, leaf.Tabs.Count);
        Assert.Same(b, leaf.ActiveTab);
        Assert.Equal(leaf.Id, mergedId);
    }

    [StaFact]
    public void TrySplitLeaf_MovesActiveToSecondChild()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var a = NewTab(tm, sm);
        var b = NewTab(tm, sm);
        var leaf = new EditorLeafNode("solo");
        leaf.Tabs.Add(a);
        leaf.Tabs.Add(b);
        leaf.ActiveTab = b;
        EditorLayoutNode root = leaf;

        var splitOk = EditorLayoutTree.TrySplitLeaf(ref root, leaf, b, EditorSplitOrientation.Horizontal,
            out var activePane);
        Assert.True(splitOk);
        var split = Assert.IsType<EditorSplitNode>(root);
        Assert.Same(leaf, split.First);
        var secondLeaf = Assert.IsType<EditorLeafNode>(split.Second);
        Assert.Same(activePane, secondLeaf);
        Assert.Single(secondLeaf.Tabs);
        Assert.Same(b, secondLeaf.ActiveTab);
        Assert.Single(leaf.Tabs);
        Assert.Same(a, leaf.ActiveTab);
    }

    [StaFact]
    public void TrySplitLeaf_ActiveInFirstPane_PlacesMovedTabInFirstChild()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var a = NewTab(tm, sm);
        var b = NewTab(tm, sm);
        var leaf = new EditorLeafNode("solo");
        leaf.Tabs.Add(a);
        leaf.Tabs.Add(b);
        leaf.ActiveTab = b;
        EditorLayoutNode root = leaf;

        var splitOk = EditorLayoutTree.TrySplitLeaf(ref root, leaf, b, EditorSplitOrientation.Vertical,
            out var activePane, activeInSecondPane: false);
        Assert.True(splitOk);
        var split = Assert.IsType<EditorSplitNode>(root);
        Assert.Same(leaf, split.First);
        Assert.Same(activePane, leaf);
        Assert.Single(leaf.Tabs);
        Assert.Same(b, leaf.ActiveTab);
        var other = Assert.IsType<EditorLeafNode>(split.Second);
        Assert.Single(other.Tabs);
        Assert.Same(a, other.ActiveTab);
    }

    [StaFact]
    public void WouldEditorSplitDropRecreateSiblingLayout_TrueWhenSoleTopTabToBottomTopHalf()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var topTab = NewTab(tm, sm);
        var bottomTab = NewTab(tm, sm);
        var topLeaf = new EditorLeafNode("top");
        topLeaf.Tabs.Add(topTab);
        topLeaf.ActiveTab = topTab;
        var bottomLeaf = new EditorLeafNode("bottom");
        bottomLeaf.Tabs.Add(bottomTab);
        bottomLeaf.ActiveTab = bottomTab;
        EditorLayoutNode root = new EditorSplitNode
        {
            Orientation = EditorSplitOrientation.Horizontal,
            First = topLeaf,
            Second = bottomLeaf,
            FirstPaneStarRatio = 1.0
        };

        Assert.True(EditorLayoutTree.WouldEditorSplitDropRecreateSiblingLayout(
            root, topTab, bottomLeaf.Id, EditorSplitOrientation.Horizontal, activeInSecondPane: false));
        Assert.False(EditorLayoutTree.WouldEditorSplitDropRecreateSiblingLayout(
            root, topTab, bottomLeaf.Id, EditorSplitOrientation.Horizontal, activeInSecondPane: true));
        Assert.False(EditorLayoutTree.WouldEditorSplitDropRecreateSiblingLayout(
            root, topTab, bottomLeaf.Id, EditorSplitOrientation.Vertical, activeInSecondPane: false));
    }

    [StaFact]
    public void WouldEditorSplitDropRecreateSiblingLayout_FalseWhenTopLeafHasTwoTabs()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var t1 = NewTab(tm, sm);
        var t2 = NewTab(tm, sm);
        var bottomTab = NewTab(tm, sm);
        var topLeaf = new EditorLeafNode("top");
        topLeaf.Tabs.Add(t1);
        topLeaf.Tabs.Add(t2);
        var bottomLeaf = new EditorLeafNode("bottom");
        bottomLeaf.Tabs.Add(bottomTab);
        EditorLayoutNode root = new EditorSplitNode
        {
            Orientation = EditorSplitOrientation.Horizontal,
            First = topLeaf,
            Second = bottomLeaf,
            FirstPaneStarRatio = 1.0
        };

        Assert.False(EditorLayoutTree.WouldEditorSplitDropRecreateSiblingLayout(
            root, t1, bottomLeaf.Id, EditorSplitOrientation.Horizontal, activeInSecondPane: false));
    }

    [StaFact]
    public void WouldEditorSplitDropRecreateSiblingLayout_TrueWhenSoleRightTabToLeftRightHalf()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var leftTab = NewTab(tm, sm);
        var rightTab = NewTab(tm, sm);
        var leftLeaf = new EditorLeafNode("left");
        leftLeaf.Tabs.Add(leftTab);
        leftLeaf.ActiveTab = leftTab;
        var rightLeaf = new EditorLeafNode("right");
        rightLeaf.Tabs.Add(rightTab);
        rightLeaf.ActiveTab = rightTab;
        EditorLayoutNode root = new EditorSplitNode
        {
            Orientation = EditorSplitOrientation.Vertical,
            First = leftLeaf,
            Second = rightLeaf,
            FirstPaneStarRatio = 1.0
        };

        Assert.True(EditorLayoutTree.WouldEditorSplitDropRecreateSiblingLayout(
            root, rightTab, leftLeaf.Id, EditorSplitOrientation.Vertical, activeInSecondPane: true));
        Assert.False(EditorLayoutTree.WouldEditorSplitDropRecreateSiblingLayout(
            root, rightTab, leftLeaf.Id, EditorSplitOrientation.Vertical, activeInSecondPane: false));
        Assert.False(EditorLayoutTree.WouldEditorSplitDropRecreateSiblingLayout(
            root, rightTab, leftLeaf.Id, EditorSplitOrientation.Horizontal, activeInSecondPane: true));
    }

    [StaFact]
    public void FlattenToSingleLeaf_PreservesDfsOrderAndDedupes()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var a = NewTab(tm, sm);
        var (l1, l2, root) = SplitTwoTabs(a, NewTab(tm, sm));
        l2.Tabs.Add(a); // duplicate ref

        var flat = EditorLayoutTree.FlattenToSingleLeaf(root, a);
        Assert.Equal(2, flat.Tabs.Count);
        Assert.Same(a, flat.ActiveTab);
    }

    [StaFact]
    public void SimplifyEmptyLeaves_PromotesNonEmptySide()
    {
        var empty = new EditorLeafNode("e");
        var full = new EditorLeafNode("f");
        var split = new EditorSplitNode
        {
            Orientation = EditorSplitOrientation.Vertical,
            First = empty,
            Second = full,
            FirstPaneStarRatio = 1.0
        };

        var simplified = EditorLayoutTree.SimplifyEmptyLeaves(split);
        Assert.Same(full, simplified);
    }

    [StaFact]
    public void NextLeaf_CyclesInDfsOrder()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var (l1, l2, root) = SplitTwoTabs(NewTab(tm, sm), NewTab(tm, sm));

        Assert.Same(l2, EditorLayoutTree.NextLeaf(root, l1.Id, 1));
        Assert.Same(l1, EditorLayoutTree.NextLeaf(root, l2.Id, 1));
    }

    [StaFact]
    public void EditorLayoutSnapshot_JsonRoundTrip_Polymorphic()
    {
        var snap = new EditorLayoutSnapshot
        {
            FocusedLeafId = "leaf-b",
            Root = new EditorLayoutSplitDto
            {
                Orientation = EditorSplitOrientation.Horizontal,
                FirstPaneStarRatio = 1.5,
                First = new EditorLayoutLeafDto { Id = "leaf-a", TabCount = 2 },
                Second = new EditorLayoutLeafDto { Id = "leaf-b", TabCount = 1 }
            }
        };

        var json = JsonSerializer.Serialize(snap);
        var back = JsonSerializer.Deserialize<EditorLayoutSnapshot>(json);
        Assert.NotNull(back);
        Assert.Equal("leaf-b", back.FocusedLeafId);
        var split = Assert.IsType<EditorLayoutSplitDto>(back.Root);
        Assert.Equal(EditorSplitOrientation.Horizontal, split.Orientation);
        Assert.Equal(1.5, split.FirstPaneStarRatio);
        Assert.IsType<EditorLayoutLeafDto>(split.First);
        Assert.IsType<EditorLayoutLeafDto>(split.Second);
    }

    [StaFact]
    public void MaterializeFromDto_AssignsTabsInDfsOrder()
    {
        EnsureWpfResourcesForTabInfo();
        var tm = new ThemeManager();
        var sm = new SyntaxManager();
        var tabs = new[] { NewTab(tm, sm), NewTab(tm, sm), NewTab(tm, sm) };
        var dto = new EditorLayoutSplitDto
        {
            Orientation = EditorSplitOrientation.Vertical,
            First = new EditorLayoutLeafDto { Id = "a", TabCount = 2 },
            Second = new EditorLayoutLeafDto { Id = "b", TabCount = 1 }
        };

        var root = EditorLayoutSnapshotSerializer.MaterializeFromDto(dto, tabs);
        var split = Assert.IsType<EditorSplitNode>(root);
        var la = Assert.IsType<EditorLeafNode>(split.First);
        var lb = Assert.IsType<EditorLeafNode>(split.Second);
        Assert.Equal(2, la.Tabs.Count);
        Assert.Single(lb.Tabs);
        Assert.Same(tabs[0], la.Tabs[0]);
        Assert.Same(tabs[1], la.Tabs[1]);
        Assert.Same(tabs[2], lb.Tabs[0]);
    }
}
