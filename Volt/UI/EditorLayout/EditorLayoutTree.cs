using System.Text.Json.Serialization;

namespace Volt;

/// <summary>Pure helpers for the editor split binary tree.</summary>
public static class EditorLayoutTree
{
    public static bool IsSplit(EditorLayoutNode root) => root is EditorSplitNode;

    public static IEnumerable<EditorLeafNode> EnumerateLeaves(EditorLayoutNode node)
    {
        switch (node)
        {
            case EditorLeafNode leaf:
                yield return leaf;
                yield break;
            case EditorSplitNode split:
                foreach (var x in EnumerateLeaves(split.First))
                    yield return x;
                foreach (var x in EnumerateLeaves(split.Second))
                    yield return x;
                break;
        }
    }

    /// <summary>Depth-first: entire left subtree, then entire right subtree.</summary>
    public static List<TabInfo> AllTabsOrdered(EditorLayoutNode root)
    {
        var r = new List<TabInfo>();
        CollectTabsDepthFirst(root, r);
        return r;
    }

    public static void CollectTabsDepthFirst(EditorLayoutNode node, List<TabInfo> sink)
    {
        switch (node)
        {
            case EditorLeafNode leaf:
                sink.AddRange(leaf.Tabs);
                break;
            case EditorSplitNode split:
                CollectTabsDepthFirst(split.First, sink);
                CollectTabsDepthFirst(split.Second, sink);
                break;
        }
    }

    public static EditorLeafNode? FindLeafForTab(EditorLayoutNode root, TabInfo tab)
    {
        foreach (var leaf in EnumerateLeaves(root))
        {
            if (leaf.Tabs.Contains(tab))
                return leaf;
        }

        return null;
    }

    public static List<TabInfo> GetPaneTabCloseTargets(EditorLayoutNode root, TabInfo clickedTab,
        TabCloseCommand command)
    {
        var leaf = FindLeafForTab(root, clickedTab);
        if (leaf == null)
            return [];

        int index = leaf.Tabs.IndexOf(clickedTab);
        if (index < 0)
            return [];

        return command switch
        {
            TabCloseCommand.Close => [clickedTab],
            TabCloseCommand.CloseAll => [.. leaf.Tabs],
            TabCloseCommand.CloseOthers => leaf.Tabs.Where(tab => !ReferenceEquals(tab, clickedTab)).ToList(),
            TabCloseCommand.CloseAllToLeft => leaf.Tabs.Take(index).ToList(),
            TabCloseCommand.CloseAllToRight => leaf.Tabs.Skip(index + 1).ToList(),
            _ => []
        };
    }

    public static EditorLeafNode? FindLeafById(EditorLayoutNode root, string leafId)
    {
        foreach (var leaf in EnumerateLeaves(root))
        {
            if (leaf.Id == leafId)
                return leaf;
        }

        return null;
    }

    /// <summary>Path of (parent split, took first child edge) from root down to the leaf, excluding the leaf itself.</summary>
    public static bool TryGetPathToLeaf(EditorLayoutNode root, string leafId,
        List<(EditorSplitNode parent, bool firstChild)> path)
    {
        path.Clear();
        return WalkPath(root, leafId, path);
    }

    private static bool WalkPath(EditorLayoutNode node, string leafId,
        List<(EditorSplitNode parent, bool firstChild)> path)
    {
        switch (node)
        {
            case EditorLeafNode lf when lf.Id == leafId:
                return true;
            case EditorSplitNode s:
                path.Add((s, true));
                if (WalkPath(s.First, leafId, path))
                    return true;
                path.RemoveAt(path.Count - 1);

                path.Add((s, false));
                if (WalkPath(s.Second, leafId, path))
                    return true;
                path.RemoveAt(path.Count - 1);
                return false;
            default:
                return false;
        }
    }

    /// <summary>Immediate parent split of <paramref name="leafId"/>, or null if the leaf is the root.</summary>
    public static EditorSplitNode? FindParentSplitOfLeaf(EditorLayoutNode root, string leafId)
    {
        var path = new List<(EditorSplitNode, bool)>();
        if (!TryGetPathToLeaf(root, leafId, path) || path.Count == 0)
            return null;
        return path[^1].Item1;
    }

    /// <summary>
    /// True when a cross-leaf editor-half drop would end up as the same outer split: sole tab of the first child
    /// moved to the second sibling (horizontal parent, top-half of bottom), or sole tab of the second child moved to
    /// the first sibling (vertical parent, right-half of left) — same orientation as the parent split.
    /// </summary>
    public static bool WouldEditorSplitDropRecreateSiblingLayout(EditorLayoutNode root, TabInfo tab, string targetLeafId,
        EditorSplitOrientation dropOrientation, bool activeInSecondPane)
    {
        var src = FindLeafForTab(root, tab);
        var dst = FindLeafById(root, targetLeafId);
        if (src == null || dst == null || ReferenceEquals(src, dst)) return false;

        var parent = FindParentSplitOfLeaf(root, src.Id);
        if (parent == null || parent.Orientation != dropOrientation) return false;
        if (!ReferenceEquals(FindParentSplitOfLeaf(root, dst.Id), parent)) return false;

        if (src.Tabs.Count != 1 || !src.Tabs.Contains(tab)) return false;

        var srcIsFirst = ReferenceEquals(parent.First, src);
        var dstIsFirst = ReferenceEquals(parent.First, dst);

        return dropOrientation switch
        {
            EditorSplitOrientation.Horizontal =>
                srcIsFirst && !dstIsFirst && !activeInSecondPane,
            EditorSplitOrientation.Vertical =>
                !srcIsFirst && dstIsFirst && activeInSecondPane,
            _ => false
        };
    }

    public static void DedupeTabsPreserveOrder(List<TabInfo> tabs)
    {
        var seen = new HashSet<TabInfo>(EqualityComparer<TabInfo>.Default);
        int w = 0;
        for (int r = 0; r < tabs.Count; r++)
        {
            var t = tabs[r];
            if (seen.Add(t))
            {
                if (w != r)
                    tabs[w] = t;
                w++;
            }
        }

        if (w < tabs.Count)
            tabs.RemoveRange(w, tabs.Count - w);
    }

    /// <summary>Replaces <paramref name="split"/> with a single leaf containing merged tabs (first subtree, then second).</summary>
    public static EditorLayoutNode ReplaceSplitWithMergedLeaf(EditorLayoutNode root, EditorSplitNode split,
        TabInfo? preferredActiveTab)
    {
        var merged = new List<TabInfo>();
        CollectTabsDepthFirst(split.First, merged);
        CollectTabsDepthFirst(split.Second, merged);
        DedupeTabsPreserveOrder(merged);

        var leaf = new EditorLeafNode();
        foreach (var t in merged)
            leaf.Tabs.Add(t);

        leaf.ActiveTab = preferredActiveTab != null && merged.Contains(preferredActiveTab)
            ? preferredActiveTab
            : merged.FirstOrDefault();

        if (ReferenceEquals(root, split))
            return leaf;

        if (!TryReplaceReference(root, split, leaf))
            throw new InvalidOperationException("Split to merge not found in tree.");
        return root;
    }

    private static bool TryReplaceLeafReference(EditorLayoutNode node, EditorLeafNode needle, EditorLayoutNode replacement)
    {
        switch (node)
        {
            case EditorSplitNode s:
                if (ReferenceEquals(s.First, needle))
                {
                    s.First = replacement;
                    return true;
                }

                if (ReferenceEquals(s.Second, needle))
                {
                    s.Second = replacement;
                    return true;
                }

                return TryReplaceLeafReference(s.First, needle, replacement) ||
                       TryReplaceLeafReference(s.Second, needle, replacement);
            default:
                return false;
        }
    }

    private static bool TryReplaceReference(EditorLayoutNode node, EditorSplitNode needle, EditorLayoutNode replacement)
    {
        switch (node)
        {
            case EditorSplitNode s:
                if (ReferenceEquals(s.First, needle))
                {
                    s.First = replacement;
                    return true;
                }

                if (ReferenceEquals(s.Second, needle))
                {
                    s.Second = replacement;
                    return true;
                }

                return TryReplaceReference(s.First, needle, replacement) || TryReplaceReference(s.Second, needle, replacement);
            default:
                return false;
        }
    }

    /// <summary>Collapse the parent split of <paramref name="focusedLeafId"/> into one leaf (join with sibling).</summary>
    public static bool TryJoinFocusedLeafWithSibling(ref EditorLayoutNode root, string focusedLeafId,
        TabInfo? preferredActiveTab, out string mergedLeafId)
    {
        mergedLeafId = focusedLeafId;
        if (root is EditorLeafNode)
            return false;

        var parent = FindParentSplitOfLeaf(root, focusedLeafId);
        if (parent == null)
            return false;

        var newRoot = ReplaceSplitWithMergedLeaf(root, parent, preferredActiveTab);
        root = newRoot;
        if (root is EditorLeafNode nl)
            mergedLeafId = nl.Id;
        else if (preferredActiveTab != null && FindLeafForTab(root, preferredActiveTab) is { } lf)
            mergedLeafId = lf.Id;
        else if (EnumerateLeaves(root).FirstOrDefault() is { } any)
            mergedLeafId = any.Id;
        return true;
    }

    /// <summary>Flatten any split tree to a single leaf (VS Code "join all").</summary>
    public static EditorLeafNode FlattenToSingleLeaf(EditorLayoutNode root, TabInfo? preferredActiveTab)
    {
        var ordered = AllTabsOrdered(root);
        DedupeTabsPreserveOrder(ordered);
        var leaf = new EditorLeafNode();
        foreach (var t in ordered)
            leaf.Tabs.Add(t);
        leaf.ActiveTab = preferredActiveTab != null && ordered.Contains(preferredActiveTab)
            ? preferredActiveTab
            : ordered.FirstOrDefault();
        return leaf;
    }

    /// <summary>
    /// Replace <paramref name="leaf"/> with a split. The leaf that ends up containing <paramref name="activeToMove"/> alone (or as that pane's active tab) is returned as <paramref name="activePaneLeaf"/>.
    /// When <paramref name="activeInSecondPane"/> is true (default): first pane = all other tabs, second = <paramref name="activeToMove"/> only (right/bottom for vertical/horizontal).
    /// When false: first pane = <paramref name="activeToMove"/> only, second = all other tabs (left/top).
    /// </summary>
    public static bool TrySplitLeaf(ref EditorLayoutNode root, EditorLeafNode leaf, TabInfo activeToMove,
        EditorSplitOrientation orientation, out EditorLeafNode activePaneLeaf, bool activeInSecondPane = true)
    {
        activePaneLeaf = null!;
        if (!leaf.Tabs.Contains(activeToMove) || leaf.Tabs.Count < 2)
            return false;

        var remaining = new List<TabInfo>(leaf.Tabs.Count - 1);
        foreach (var t in leaf.Tabs)
        {
            if (!ReferenceEquals(t, activeToMove))
                remaining.Add(t);
        }

        EditorSplitNode split;
        if (activeInSecondPane)
        {
            leaf.Tabs.Clear();
            foreach (var t in remaining)
                leaf.Tabs.Add(t);
            leaf.ActiveTab = leaf.Tabs.FirstOrDefault();

            var newSecond = new EditorLeafNode();
            newSecond.Tabs.Add(activeToMove);
            newSecond.ActiveTab = activeToMove;

            split = new EditorSplitNode
            {
                Orientation = orientation,
                First = leaf,
                Second = newSecond,
                FirstPaneStarRatio = 1.0
            };
            activePaneLeaf = newSecond;
        }
        else
        {
            leaf.Tabs.Clear();
            leaf.Tabs.Add(activeToMove);
            leaf.ActiveTab = activeToMove;

            var otherLeaf = new EditorLeafNode();
            foreach (var t in remaining)
                otherLeaf.Tabs.Add(t);
            otherLeaf.ActiveTab = remaining.FirstOrDefault();

            split = new EditorSplitNode
            {
                Orientation = orientation,
                First = leaf,
                Second = otherLeaf,
                FirstPaneStarRatio = 1.0
            };
            activePaneLeaf = leaf;
        }

        if (ReferenceEquals(root, leaf))
        {
            root = split;
            return true;
        }

        if (!TryReplaceLeafReference(root, leaf, split))
            return false;

        return true;
    }

    /// <summary>Remove empty leaves by promoting the non-empty side of a split (bottom-up).</summary>
    public static EditorLayoutNode SimplifyEmptyLeaves(EditorLayoutNode node)
    {
        switch (node)
        {
            case EditorLeafNode:
                return node;
            case EditorSplitNode s:
            {
                var a = SimplifyEmptyLeaves(s.First);
                var b = SimplifyEmptyLeaves(s.Second);
                if (a is EditorLeafNode la && la.Tabs.Count == 0)
                    return b;
                if (b is EditorLeafNode lb && lb.Tabs.Count == 0)
                    return a;
                s.First = a;
                s.Second = b;
                return s;
            }
            default:
                return node;
        }
    }

    /// <summary>Leaves in DFS order for cycling focus.</summary>
    public static List<EditorLeafNode> LeavesInOrder(EditorLayoutNode root) =>
        EnumerateLeaves(root).ToList();

    public static EditorLeafNode? NextLeaf(EditorLayoutNode root, string focusedLeafId, int delta)
    {
        var leaves = LeavesInOrder(root);
        int i = leaves.FindIndex(l => l.Id == focusedLeafId);
        if (i < 0 || leaves.Count == 0)
            return null;
        int n = (i + delta + leaves.Count) % leaves.Count;
        return leaves[n];
    }

    public static void ToggleOrientation(EditorSplitNode split)
    {
        split.Orientation = split.Orientation == EditorSplitOrientation.Horizontal
            ? EditorSplitOrientation.Vertical
            : EditorSplitOrientation.Horizontal;
    }
}

/// <summary>JSON DTO for persisting editor split layout (optional in session).</summary>
public class EditorLayoutSnapshot
{
    [JsonPropertyName("focusedLeafId")]
    public string? FocusedLeafId { get; set; }

    [JsonPropertyName("root")]
    public EditorLayoutNodeDto? Root { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EditorLayoutLeafDto), "leaf")]
[JsonDerivedType(typeof(EditorLayoutSplitDto), "split")]
public abstract class EditorLayoutNodeDto;

public sealed class EditorLayoutLeafDto : EditorLayoutNodeDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Number of consecutive tabs from the flat session tab list assigned to this leaf (DFS order).</summary>
    [JsonPropertyName("tabCount")]
    public int TabCount { get; set; }
}

public sealed class EditorLayoutSplitDto : EditorLayoutNodeDto
{
    [JsonPropertyName("orientation")]
    public EditorSplitOrientation Orientation { get; set; }

    [JsonPropertyName("firstPaneStarRatio")]
    public double FirstPaneStarRatio { get; set; } = 1.0;

    [JsonPropertyName("first")]
    public EditorLayoutNodeDto First { get; set; } = null!;

    [JsonPropertyName("second")]
    public EditorLayoutNodeDto Second { get; set; } = null!;
}

public static class EditorLayoutSnapshotSerializer
{
    public static EditorLayoutSnapshot? BuildSnapshot(EditorLayoutNode root, string focusedLeafId)
    {
        if (root is EditorLeafNode)
            return null;

        return new EditorLayoutSnapshot
        {
            FocusedLeafId = focusedLeafId,
            Root = ToDto(root)
        };
    }

    private static EditorLayoutNodeDto ToDto(EditorLayoutNode node) => node switch
    {
        EditorLeafNode lf => new EditorLayoutLeafDto { Id = lf.Id, TabCount = lf.Tabs.Count },
        EditorSplitNode s => new EditorLayoutSplitDto
        {
            Orientation = s.Orientation,
            FirstPaneStarRatio = s.FirstPaneStarRatio,
            First = ToDto(s.First),
            Second = ToDto(s.Second)
        },
        _ => throw new ArgumentOutOfRangeException(nameof(node))
    };

    /// <summary>Assigns tabs from <paramref name="orderedTabs"/> to leaves in DFS order according to the DTO.</summary>
    public static EditorLayoutNode? MaterializeFromDto(EditorLayoutNodeDto? dto, IReadOnlyList<TabInfo> orderedTabs)
    {
        if (dto == null)
            return null;
        int i = 0;
        return Materialize(dto, orderedTabs, ref i);
    }

    private static EditorLayoutNode Materialize(EditorLayoutNodeDto dto, IReadOnlyList<TabInfo> orderedTabs, ref int index)
    {
        switch (dto)
        {
            case EditorLayoutLeafDto lf:
            {
                var leaf = new EditorLeafNode(lf.Id);
                for (int k = 0; k < lf.TabCount && index < orderedTabs.Count; k++, index++)
                    leaf.Tabs.Add(orderedTabs[index]);
                return leaf;
            }
            case EditorLayoutSplitDto sp:
            {
                var split = new EditorSplitNode
                {
                    Orientation = sp.Orientation,
                    FirstPaneStarRatio = sp.FirstPaneStarRatio,
                    First = Materialize(sp.First, orderedTabs, ref index),
                    Second = Materialize(sp.Second, orderedTabs, ref index)
                };
                return split;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(dto));
        }
    }
}
