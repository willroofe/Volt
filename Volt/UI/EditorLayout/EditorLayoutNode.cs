namespace Volt;

/// <summary>Row split: first pane above second. Column split: first pane left of second.</summary>
public enum EditorSplitOrientation
{
    Horizontal,
    Vertical
}

public enum TabCloseCommand
{
    Close,
    CloseAll,
    CloseOthers,
    CloseAllToLeft,
    CloseAllToRight
}

public abstract class EditorLayoutNode;

public sealed class EditorLeafNode : EditorLayoutNode
{
    public string Id { get; }

    public List<TabInfo> Tabs { get; } = [];

    public TabInfo? ActiveTab { get; set; }

    public EditorLeafNode(string? id = null) =>
        Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
}

public sealed class EditorSplitNode : EditorLayoutNode
{
    public EditorSplitOrientation Orientation { get; set; }

    public EditorLayoutNode First { get; set; } = null!;

    public EditorLayoutNode Second { get; set; } = null!;

    /// <summary>Star weight of the first pane relative to the second (1 = equal).</summary>
    public double FirstPaneStarRatio { get; set; } = 1.0;
}
