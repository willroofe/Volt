using System.Windows;
using System.Windows.Controls;

namespace Volt;

public sealed class EditorLayoutBuildResult
{
    public Grid RootGrid { get; }

    public Dictionary<string, EditorPaneChrome> LeafChrome { get; }

    public EditorLayoutBuildResult(Grid rootGrid, Dictionary<string, EditorPaneChrome> leafChrome)
    {
        RootGrid = rootGrid;
        LeafChrome = leafChrome;
    }
}

public static class EditorLayoutBuilder
{
    public static EditorLayoutBuildResult Build(EditorLayoutNode root, Style horizontalSplitterStyle,
        Style verticalSplitterStyle)
    {
        var leafChrome = new Dictionary<string, EditorPaneChrome>();
        var inner = BuildRecursive(root, leafChrome, horizontalSplitterStyle, verticalSplitterStyle);
        var wrap = new Grid();
        wrap.Children.Add(inner);
        return new EditorLayoutBuildResult(wrap, leafChrome);
    }

    private static UIElement BuildRecursive(EditorLayoutNode node, Dictionary<string, EditorPaneChrome> leafChrome,
        Style horizontalSplitterStyle, Style verticalSplitterStyle)
    {
        switch (node)
        {
            case EditorLeafNode lf:
            {
                var chrome = new EditorPaneChrome();
                leafChrome[lf.Id] = chrome;
                chrome.Tag = lf.Id;
                return chrome;
            }
            case EditorSplitNode sp:
            {
                var g = new Grid();
                if (sp.Orientation == EditorSplitOrientation.Horizontal)
                {
                    g.RowDefinitions.Add(new RowDefinition
                    {
                        Height = new GridLength(Math.Max(0.01, sp.FirstPaneStarRatio), GridUnitType.Star),
                        MinHeight = 120
                    });
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    g.RowDefinitions.Add(new RowDefinition
                        { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });

                    var first = BuildRecursive(sp.First, leafChrome, horizontalSplitterStyle, verticalSplitterStyle);
                    Grid.SetRow(first, 0);
                    Grid.SetColumn(first, 0);

                    var splitter = new GridSplitter
                    {
                        Height = 2,
                        Width = double.NaN,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        Style = horizontalSplitterStyle,
                        ResizeDirection = GridResizeDirection.Rows,
                        ResizeBehavior = GridResizeBehavior.PreviousAndNext
                    };
                    Grid.SetRow(splitter, 1);
                    Grid.SetColumn(splitter, 0);

                    var second = BuildRecursive(sp.Second, leafChrome, horizontalSplitterStyle, verticalSplitterStyle);
                    Grid.SetRow(second, 2);
                    Grid.SetColumn(second, 0);

                    g.Children.Add(first);
                    g.Children.Add(splitter);
                    g.Children.Add(second);
                }
                else
                {
                    g.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(Math.Max(0.01, sp.FirstPaneStarRatio), GridUnitType.Star),
                        MinWidth = 120
                    });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition
                        { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
                    g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                    var first = BuildRecursive(sp.First, leafChrome, horizontalSplitterStyle, verticalSplitterStyle);
                    Grid.SetRow(first, 0);
                    Grid.SetColumn(first, 0);

                    var splitter = new GridSplitter
                    {
                        Width = 2,
                        Height = double.NaN,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        Style = verticalSplitterStyle,
                        ResizeDirection = GridResizeDirection.Columns,
                        ResizeBehavior = GridResizeBehavior.PreviousAndNext
                    };
                    Grid.SetRow(splitter, 0);
                    Grid.SetColumn(splitter, 1);

                    var second = BuildRecursive(sp.Second, leafChrome, horizontalSplitterStyle, verticalSplitterStyle);
                    Grid.SetRow(second, 0);
                    Grid.SetColumn(second, 2);

                    g.Children.Add(first);
                    g.Children.Add(splitter);
                    g.Children.Add(second);
                }

                return g;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(node));
        }
    }
}
