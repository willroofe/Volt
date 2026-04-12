using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

/// <summary>Per-leaf hit targets for cross-leaf tab drag (tab-bar highlight matches panel dock drop styling).</summary>
internal sealed record EditorSplitLeafDragRow(
    string LeafId,
    Border TabBarBorder,
    StackPanel Strip,
    Border DropIndicator,
    Border EditorHost,
    EditorPaneChrome Chrome);

/// <summary>Hit targets and visuals for dragging editor tabs between N split leaves.</summary>
internal sealed record EditorSplitTabDragHost(Grid SplitRoot, IReadOnlyList<EditorSplitLeafDragRow> Leaves);

/// <summary>
/// Creates tab header UI elements and manages tab drag-to-reorder.
/// Extracted from MainWindow to reduce its size and isolate tab header concerns.
/// </summary>
internal class TabHeaderFactory
{
    private TabInfo? _dragTab;
    private Panel? _dragSourceStrip;
    private FrameworkElement? _dragSourceDropIndicator;
    private Window? _splitDragPreviewWindow;
    private Point _dragStartPos;
    private Point _dragStartWindow;
    private bool _isTabDragging;
    private int _dragTargetIndex = -1;
    private bool _dragIsCrossLeaf;
    private string? _dragCrossTargetLeafId;
    private Popup? _dragGhost;
    private bool _hasPendingEditorSplit;
    private string _pendingEditorSplitLeafId = "";
    private EditorSplitOrientation _pendingEditorSplitOrientation;
    private bool _pendingEditorSplitActiveInSecondPane;

    public bool FixedWidth { get; set; }
    private const double FixedTabWidth = 160;

    /// <summary>When set, tab drags can move headers between primary/secondary strips with dock-style tab bar highlighting.</summary>
    public EditorSplitTabDragHost? SplitDragHost { get; set; }

    /// <summary>
    /// Returns the strip and drop indicator for <paramref name="tab"/> based on which pane owns it today.
    /// Required after split/join: headers keep delegate closures from creation, but <see cref="TabInfo.HeaderElement"/> is reparented.
    /// </summary>
    public Func<TabInfo, (Panel strip, FrameworkElement drop)>? ResolveEditorTabStrip { get; set; }

    public event Action<TabInfo>? TabActivated;
    public event Action<TabInfo>? TabClosed;
    public event Action<TabInfo, int>? TabReordered;
    public event Action<TabInfo, string, int>? TabMovedToOtherLeaf;

    /// <summary>Drop on an editor half to split that leaf; <paramref name="activeInSecondPane"/> matches <see cref="EditorLayoutTree.TrySplitLeaf"/>.</summary>
    public event Action<TabInfo, string, EditorSplitOrientation, bool>? TabEditorSplitDrop;

    /// <summary>When null or returns false, editor-half drop is treated as a miss (no highlight / no split on release).</summary>
    public Func<TabInfo, string, EditorSplitOrientation, bool, bool>? CanTabEditorSplitOnLeaf { get; set; }

    public void ApplyFixedWidth(Border header)
    {
        if (FixedWidth)
        {
            header.Width = FixedTabWidth;
            header.MinWidth = FixedTabWidth;
        }
        else
        {
            header.Width = double.NaN;
            header.MinWidth = 60;
        }
    }

    public Border CreateHeader(TabInfo tab, Panel tabStrip, FrameworkElement dropIndicator)
    {
        (Panel strip, FrameworkElement drop) Cur() => ResolveEditorTabStrip?.Invoke(tab) ?? (tabStrip, dropIndicator);

        var textBlock = new TextBlock
        {
            Text = tab.DisplayName,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);

        var closeBtn = new Button { Style = (Style)Application.Current.FindResource("TabCloseButton") };
        closeBtn.Click += (_, _) => TabClosed?.Invoke(tab);

        var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(closeBtn, Dock.Right);
        panel.Children.Add(closeBtn);
        panel.Children.Add(textBlock);

        var header = new Border
        {
            Child = panel,
            Height = UIConstants.TabBarHeight,
            MinWidth = 60,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        if (FixedWidth)
        {
            header.Width = FixedTabWidth;
            header.MinWidth = FixedTabWidth;
        }
        header.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.TabBorder);

        header.MouseLeftButtonDown += (_, e) =>
        {
            UnhookSplitDragWindowPreview();
            TabActivated?.Invoke(tab);
            _dragTab = tab;
            var (curStrip, curDrop) = Cur();
            _dragSourceStrip = curStrip;
            _dragSourceDropIndicator = curDrop;
            _dragStartPos = e.GetPosition(curStrip);
            var win = Window.GetWindow(header);
            if (win != null)
                _dragStartWindow = e.GetPosition(win);
            _isTabDragging = false;
            _dragTargetIndex = -1;
            _dragIsCrossLeaf = false;
            _hasPendingEditorSplit = false;
            header.CaptureMouse();
            e.Handled = true;
        };

        header.MouseMove += (_, e) =>
        {
            if (_dragTab != tab || e.LeftButton != MouseButtonState.Pressed) return;
            var win = Window.GetWindow(header);
            if (win == null) return;

            if (!_isTabDragging)
            {
                var curWin = e.GetPosition(win);
                double dx = Math.Abs(curWin.X - _dragStartWindow.X);
                double dy = Math.Abs(curWin.Y - _dragStartWindow.Y);
                bool past = SplitDragHost != null
                    ? dx >= SystemParameters.MinimumHorizontalDragDistance ||
                      dy >= SystemParameters.MinimumVerticalDragDistance
                    : dx >= SystemParameters.MinimumHorizontalDragDistance;
                if (!past) return;
                _isTabDragging = true;
                ShowDragGhost(tab, header);
                header.Opacity = 0.4;
                HookSplitDragWindowPreview(header);
            }

            UpdateDragGhost(e, header);

            var (curStrip, curDrop) = Cur();
            if (SplitDragHost != null)
                UpdateDropIndicatorSplit(curStrip, curDrop, tab);
            else
            {
                var pos = e.GetPosition(curStrip);
                UpdateDropIndicator(pos.X, curStrip, curDrop, tab, headerInStrip: true);
            }
        };

        header.MouseLeftButtonUp += (_, _) =>
        {
            if (_dragTab == tab)
            {
                bool wasDragging = _isTabDragging;
                int targetIndex = _dragTargetIndex;
                bool cross = _dragIsCrossLeaf;
                var crossLeaf = _dragCrossTargetLeafId;
                bool editorSplit = _hasPendingEditorSplit;
                var editorLeaf = _pendingEditorSplitLeafId;
                var editorOrient = _pendingEditorSplitOrientation;
                bool editorSecond = _pendingEditorSplitActiveInSecondPane;
                UnhookSplitDragWindowPreview();
                header.ReleaseMouseCapture();
                if (wasDragging)
                {
                    header.Opacity = 1.0;
                    HideDragGhost();
                    ClearSplitDragUi(Cur().drop);
                    if (editorSplit)
                        TabEditorSplitDrop?.Invoke(tab, editorLeaf, editorOrient, editorSecond);
                    else if (targetIndex >= 0)
                    {
                        if (cross && crossLeaf != null)
                            TabMovedToOtherLeaf?.Invoke(tab, crossLeaf, targetIndex);
                        else
                            TabReordered?.Invoke(tab, targetIndex);
                    }
                }

                _dragTab = null;
                _dragSourceStrip = null;
                _dragSourceDropIndicator = null;
                _isTabDragging = false;
                _dragTargetIndex = -1;
                _dragIsCrossLeaf = false;
                _hasPendingEditorSplit = false;
            }
        };

        header.LostMouseCapture += (_, _) =>
        {
            if (_dragTab == tab && _isTabDragging)
            {
                header.Opacity = 1.0;
                HideDragGhost();
                ClearSplitDragUi(Cur().drop);
                UnhookSplitDragWindowPreview();
                _dragTab = null;
                _dragSourceStrip = null;
                _dragSourceDropIndicator = null;
                _isTabDragging = false;
                _dragTargetIndex = -1;
                _dragIsCrossLeaf = false;
                _hasPendingEditorSplit = false;
            }
        };

        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                TabClosed?.Invoke(tab);
                e.Handled = true;
            }
        };

        header.MouseRightButtonUp += (_, e) =>
        {
            var menu = ContextMenuHelper.Create();
            var canReveal = tab.FilePath != null && (File.Exists(tab.FilePath) || Directory.Exists(tab.FilePath));
            if (canReveal)
                menu.Items.Add(ContextMenuHelper.Item("Reveal in File Explorer", "\uE8B7",
                    () => FileHelper.RevealInFileExplorer(tab.FilePath!)));

            if (menu.Items.Count == 0)
                return;

            header.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        };

        return header;
    }

    private void HookSplitDragWindowPreview(UIElement relativeFrom)
    {
        if (SplitDragHost == null) return;
        var win = Window.GetWindow(relativeFrom);
        if (win == null || ReferenceEquals(_splitDragPreviewWindow, win)) return;
        UnhookSplitDragWindowPreview();
        _splitDragPreviewWindow = win;
        win.PreviewMouseMove += OnWindowPreviewMouseMoveDuringTabDrag;
    }

    private void UnhookSplitDragWindowPreview()
    {
        if (_splitDragPreviewWindow != null)
        {
            _splitDragPreviewWindow.PreviewMouseMove -= OnWindowPreviewMouseMoveDuringTabDrag;
            _splitDragPreviewWindow = null;
        }
    }

    private void OnWindowPreviewMouseMoveDuringTabDrag(object sender, MouseEventArgs e)
    {
        if (!_isTabDragging || _dragTab == null || SplitDragHost == null ||
            _dragSourceStrip == null || _dragSourceDropIndicator == null)
            return;
        UpdateDropIndicatorSplit(_dragSourceStrip, _dragSourceDropIndicator, _dragTab);
    }

    private void ClearSplitDragUi(FrameworkElement localDropIndicator)
    {
        localDropIndicator.Visibility = Visibility.Collapsed;
        _hasPendingEditorSplit = false;
        ClearEditorDropHighlights();
        if (SplitDragHost == null) return;
        foreach (var r in SplitDragHost.Leaves)
            r.DropIndicator.Visibility = Visibility.Collapsed;
        SetTabBarDropHighlight(null);
    }

    private void ClearEditorDropHighlights()
    {
        if (SplitDragHost == null) return;
        foreach (var r in SplitDragHost.Leaves)
            r.Chrome.SetEditorSplitDropHighlight(false, EditorSplitOrientation.Horizontal, false);
    }

    private void UpdateDropIndicatorSplit(Panel sourceStrip, FrameworkElement sourceDropIndicator, TabInfo dragTab)
    {
        var host = SplitDragHost!;
        string? sourceLeafId = null;
        foreach (var r in host.Leaves)
        {
            if (ReferenceEquals(r.Strip, sourceStrip))
            {
                sourceLeafId = r.LeafId;
                break;
            }
        }

        if (sourceLeafId == null) return;

        ClearEditorDropHighlights();

        var stripLeaf = HitLeafStripUnderCursor(host, sourceLeafId);
        if (stripLeaf != null)
        {
            _hasPendingEditorSplit = false;
            var overRow = host.Leaves.First(r => r.LeafId == stripLeaf);
            foreach (var r in host.Leaves)
            {
                if (!ReferenceEquals(r, overRow))
                    r.DropIndicator.Visibility = Visibility.Collapsed;
            }

            if (stripLeaf == sourceLeafId)
            {
                SetTabBarDropHighlight(null);
                UpdateDropIndicator(Mouse.GetPosition(overRow.Strip).X, overRow.Strip, overRow.DropIndicator, dragTab,
                    headerInStrip: true);
                _dragIsCrossLeaf = false;
            }
            else
            {
                SetTabBarDropHighlight(stripLeaf);
                sourceDropIndicator.Visibility = Visibility.Collapsed;
                UpdateDropIndicator(Mouse.GetPosition(overRow.Strip).X, overRow.Strip, overRow.DropIndicator, dragTab,
                    headerInStrip: false);
                _dragIsCrossLeaf = true;
                _dragCrossTargetLeafId = stripLeaf;
            }

            return;
        }

        if (TryGetEditorSplitDrop(host, out var edLeaf, out var orient, out var activeSecond) &&
            (CanTabEditorSplitOnLeaf?.Invoke(dragTab, edLeaf, orient, activeSecond) ?? false))
        {
            sourceDropIndicator.Visibility = Visibility.Collapsed;
            foreach (var r in host.Leaves)
                r.DropIndicator.Visibility = Visibility.Collapsed;
            SetTabBarDropHighlight(null);
            _dragTargetIndex = -1;
            _dragIsCrossLeaf = false;
            _hasPendingEditorSplit = true;
            _pendingEditorSplitLeafId = edLeaf;
            _pendingEditorSplitOrientation = orient;
            _pendingEditorSplitActiveInSecondPane = activeSecond;
            var hitRow = host.Leaves.First(r => r.LeafId == edLeaf);
            hitRow.Chrome.SetEditorSplitDropHighlight(true, orient, activeSecond);
            return;
        }

        sourceDropIndicator.Visibility = Visibility.Collapsed;
        foreach (var r in host.Leaves)
            r.DropIndicator.Visibility = Visibility.Collapsed;
        SetTabBarDropHighlight(null);
        _dragTargetIndex = -1;
        _dragIsCrossLeaf = false;
        _hasPendingEditorSplit = false;
    }

    private static string? HitLeafStripUnderCursor(EditorSplitTabDragHost host, string sourceLeafId)
    {
        if (!host.SplitRoot.IsVisible) return null;

        double band = UIConstants.TabBarHeight + 16;
        string? other = null;
        foreach (var row in host.Leaves)
        {
            if (row.LeafId == sourceLeafId) continue;
            var p = Mouse.GetPosition(row.TabBarBorder);
            bool inBar = row.TabBarBorder.ActualWidth > 0 &&
                         p.X >= 0 && p.X <= row.TabBarBorder.ActualWidth &&
                         p.Y >= 0 && p.Y <= band;
            if (inBar)
                other = row.LeafId;
        }

        if (other != null)
            return other;

        foreach (var row in host.Leaves)
        {
            if (row.LeafId != sourceLeafId) continue;
            var p = Mouse.GetPosition(row.TabBarBorder);
            bool inBar = row.TabBarBorder.ActualWidth > 0 &&
                         p.X >= 0 && p.X <= row.TabBarBorder.ActualWidth &&
                         p.Y >= 0 && p.Y <= band;
            if (inBar)
                return row.LeafId;
        }

        return null;
    }

    private static bool TryGetEditorSplitDrop(EditorSplitTabDragHost host, out string targetLeafId,
        out EditorSplitOrientation orientation, out bool activeInSecondPane)
    {
        targetLeafId = "";
        orientation = EditorSplitOrientation.Horizontal;
        activeInSecondPane = true;
        if (!host.SplitRoot.IsVisible) return false;

        foreach (var row in host.Leaves)
        {
            var ed = row.EditorHost;
            if (ed.ActualWidth <= 0 || ed.ActualHeight <= 0)
                continue;
            var p = Mouse.GetPosition(ed);
            if (p.X < 0 || p.X > ed.ActualWidth || p.Y < 0 || p.Y > ed.ActualHeight)
                continue;

            double w = ed.ActualWidth;
            double h = ed.ActualHeight;
            double distH = Math.Min(p.X, w - p.X);
            double distV = Math.Min(p.Y, h - p.Y);
            targetLeafId = row.LeafId;
            if (distH <= distV)
            {
                orientation = EditorSplitOrientation.Vertical;
                activeInSecondPane = p.X >= w * 0.5;
            }
            else
            {
                orientation = EditorSplitOrientation.Horizontal;
                activeInSecondPane = p.Y >= h * 0.5;
            }

            return true;
        }

        return false;
    }

    private void SetTabBarDropHighlight(string? leafId)
    {
        if (SplitDragHost == null) return;
        foreach (var r in SplitDragHost.Leaves)
            ApplyTabBarHighlight(r.TabBarBorder, leafId != null && r.LeafId == leafId);
    }

    private static void ApplyTabBarHighlight(Border bar, bool highlight)
    {
        if (highlight)
        {
            var fg = (Brush)Application.Current.Resources[ThemeResourceKeys.TextFg];
            var brush = fg.Clone();
            brush.Opacity = 0.32;
            brush.Freeze();
            bar.Background = brush;
        }
        else
            bar.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.TabBarBg);
    }

    private void ShowDragGhost(TabInfo tab, Visual relativeTo)
    {
        var text = new TextBlock
        {
            Text = tab.DisplayName,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);

        var border = new Border
        {
            Child = text,
            Height = 30,
            MinWidth = 60,
            CornerRadius = new CornerRadius(4),
            Opacity = 0.85
        };
        border.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.TabActive);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.TabBorder);
        border.BorderThickness = new Thickness(1);

        _dragGhost = new Popup
        {
            Child = border,
            AllowsTransparency = true,
            Placement = PlacementMode.AbsolutePoint,
            IsHitTestVisible = false,
            IsOpen = true
        };
    }

    private void UpdateDragGhost(MouseEventArgs e, Visual relativeTo)
    {
        if (_dragGhost == null) return;
        var window = Window.GetWindow(relativeTo);
        if (window == null) return;
        var screenPos = window.PointToScreen(e.GetPosition(window));
        var source = PresentationSource.FromVisual(window);
        double dpiScale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        _dragGhost.HorizontalOffset = screenPos.X * dpiScale + 12;
        _dragGhost.VerticalOffset = screenPos.Y * dpiScale + 4;
    }

    private void HideDragGhost()
    {
        if (_dragGhost != null)
        {
            _dragGhost.IsOpen = false;
            _dragGhost = null;
        }
    }

    private void UpdateDropIndicator(double mouseX, Panel tabStrip, FrameworkElement indicator, TabInfo dragTab, bool headerInStrip)
    {
        double offset = 0;
        int insertIdx = -1;
        double indicatorX = 0;
        int dragSourceIdx = -1;

        for (int i = 0; i < tabStrip.Children.Count; i++)
        {
            if (tabStrip.Children[i] is FrameworkElement el)
            {
                if (el == dragTab.HeaderElement) dragSourceIdx = i;
                double width = el.ActualWidth;
                double midpoint = offset + width / 2;
                if (insertIdx < 0 && mouseX < midpoint)
                {
                    insertIdx = i;
                    indicatorX = offset;
                }
                offset += width;
            }
        }

        if (insertIdx < 0)
        {
            insertIdx = tabStrip.Children.Count;
            indicatorX = offset;
        }

        if (headerInStrip && dragSourceIdx >= 0 && (insertIdx == dragSourceIdx || insertIdx == dragSourceIdx + 1))
        {
            indicator.Visibility = Visibility.Collapsed;
            _dragTargetIndex = -1;
            return;
        }

        // Header not in this strip (e.g. wrong headerInStrip) must use raw insertIdx — otherwise
        // insertIdx > -1 ? insertIdx - 1 yields -1 for insert-at-start and the drop is lost.
        if (headerInStrip && dragSourceIdx >= 0)
            _dragTargetIndex = insertIdx > dragSourceIdx ? insertIdx - 1 : insertIdx;
        else
            _dragTargetIndex = insertIdx;

        indicator.Visibility = Visibility.Visible;
        indicator.Margin = new Thickness(indicatorX - 1, 0, 0, 0);
    }
}
