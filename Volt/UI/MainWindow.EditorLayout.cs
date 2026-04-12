using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public partial class MainWindow
{
    private List<TabInfo> AllTabsOrdered() => EditorLayoutTree.AllTabsOrdered(_editorLayoutRoot);

    private bool TabExistsInAnyPane(TabInfo tab) => EditorLayoutTree.FindLeafForTab(_editorLayoutRoot, tab) != null;

    private EditorLeafNode GetFocusedLeaf() =>
        EditorLayoutTree.FindLeafById(_editorLayoutRoot, _focusedLeafId)
        ?? EditorLayoutTree.EnumerateLeaves(_editorLayoutRoot).First();

    private (Panel strip, FrameworkElement drop) ResolveEditorTabStripForTab(TabInfo tab)
    {
        var leaf = EditorLayoutTree.FindLeafForTab(_editorLayoutRoot, tab);
        if (leaf == null || _layoutBuild == null ||
            !_layoutBuild.LeafChrome.TryGetValue(leaf.Id, out var chrome))
            return (_headerScratchStrip, _headerScratchDrop);
        return (chrome.TabStrip, chrome.TabDropIndicator);
    }

    private void RebuildEditorLayoutUi()
    {
        if (_layoutBuild != null)
        {
            foreach (var c in _layoutBuild.LeafChrome.Values)
                c.EditorHost.Child = null;
            UnwireLeafChromeHandlers(_layoutBuild);
        }

        EditorLayoutHost.Children.Clear();
        var h = (Style)FindResource("EditorHorizSplitterStyle");
        var v = (Style)FindResource("EditorVertSplitterStyle");
        _layoutBuild = EditorLayoutBuilder.Build(_editorLayoutRoot, h, v);
        EditorLayoutHost.Children.Add(_layoutBuild.RootGrid);
        foreach (var kv in _layoutBuild.LeafChrome)
            WireLeafChrome(kv.Key, kv.Value);
        RefreshEditorTabSplitDragHost();
        SyncAllStripChildrenFromModel();
        UpdateTabOverflowBrushes();
        foreach (var c in _layoutBuild.LeafChrome.Values)
            UpdateTabOverflowIndicators(c.TabScrollViewer, c.TabOverflowLeft, c.TabOverflowRight);

        var newTabGesture = _keyBindingManager.GetGestureText(VoltCommand.NewTab);
        var newTabTip = string.IsNullOrEmpty(newTabGesture) ? "New Tab" : $"New Tab ({newTabGesture})";
        foreach (var c in _layoutBuild.LeafChrome.Values)
            c.NewTabButton.ToolTip = newTabTip;

        RefreshEditorHostsAndVisibility();
    }

    private void UnwireLeafChromeHandlers(EditorLayoutBuildResult build)
    {
        foreach (var c in build.LeafChrome.Values)
        {
            c.NewTabButton.Click -= OnLeafChromeNewTabClick;
            c.TabScrollViewer.PreviewMouseWheel -= OnLeafChromeTabScrollPreviewMouseWheel;
            c.TabScrollViewer.ScrollChanged -= OnLeafChromeTabScrollChanged;
        }
    }

    private void WireLeafChrome(string leafId, EditorPaneChrome c)
    {
        c.NewTabButton.Tag = leafId;
        c.NewTabButton.Click += OnLeafChromeNewTabClick;
        c.TabScrollViewer.PreviewMouseWheel += OnLeafChromeTabScrollPreviewMouseWheel;
        c.TabScrollViewer.ScrollChanged += OnLeafChromeTabScrollChanged;
    }

    private void OnLeafChromeNewTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            _focusedLeafId = id;
        OnNewTab(sender, e);
    }

    private void OnLeafChromeTabScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv && TryHorizontalScrollTabStrip(sv, e.Delta))
            e.Handled = true;
    }

    private void OnLeafChromeTabScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || _layoutBuild == null) return;
        foreach (var c in _layoutBuild.LeafChrome.Values)
        {
            if (!ReferenceEquals(c.TabScrollViewer, sv)) continue;
            UpdateTabOverflowIndicators(sv, c.TabOverflowLeft, c.TabOverflowRight);
            break;
        }
    }

    private void SyncAllStripChildrenFromModel()
    {
        if (_layoutBuild == null) return;
        foreach (var leaf in EditorLayoutTree.EnumerateLeaves(_editorLayoutRoot))
        {
            if (_layoutBuild.LeafChrome.TryGetValue(leaf.Id, out var chrome))
                SyncStripChildrenToTabList(chrome.TabStrip, leaf.Tabs);
        }
    }

    private void RefreshEditorTabSplitDragHost()
    {
        if (_layoutBuild == null || !EditorLayoutTree.IsSplit(_editorLayoutRoot))
        {
            _tabHeaderFactory.SplitDragHost = null;
            return;
        }

        var rows = new List<EditorSplitLeafDragRow>();
        foreach (var leaf in EditorLayoutTree.EnumerateLeaves(_editorLayoutRoot))
        {
            if (!_layoutBuild.LeafChrome.TryGetValue(leaf.Id, out var c)) continue;
            rows.Add(new EditorSplitLeafDragRow(leaf.Id, c.TabBarBorder, c.TabStrip, c.TabDropIndicator));
        }

        _tabHeaderFactory.SplitDragHost = new EditorSplitTabDragHost(_layoutBuild.RootGrid, rows);
    }

    private void RefreshEditorHostsAndVisibility()
    {
        if (_layoutBuild == null) return;
        foreach (var leaf in EditorLayoutTree.EnumerateLeaves(_editorLayoutRoot))
        {
            if (!_layoutBuild.LeafChrome.TryGetValue(leaf.Id, out var chrome))
                continue;
            AttachScrollHostToEditorHost(chrome.EditorHost, leaf.ActiveTab?.ScrollHost);
        }
    }

    /// <summary>
    /// Hosts each tab's <see cref="TabInfo.ScrollHost"/> under exactly one <see cref="EditorPaneChrome.EditorHost"/>.
    /// Detaches from any stale parent first (required after layout rebuild — old Borders can still own the logical child).
    /// </summary>
    private static void AttachScrollHostToEditorHost(Decorator host, ScrollViewer? scroll)
    {
        if (ReferenceEquals(host.Child, scroll))
            return;

        if (scroll != null)
        {
            var p = scroll.Parent;
            if (p is Decorator oldDec && !ReferenceEquals(oldDec, host))
                oldDec.Child = null;
        }

        host.Child = scroll;
    }

    private TabInfo CreateTab(string? filePath = null)
    {
        var leaf = EditorLayoutTree.FindLeafById(_editorLayoutRoot, _focusedLeafId)
                   ?? EditorLayoutTree.EnumerateLeaves(_editorLayoutRoot).First();
        var tab = new TabInfo(ThemeManager, SyntaxManager) { FilePath = filePath };
        leaf.Tabs.Add(tab);
        tab.Editor.DirtyChanged += (_, _) => UpdateTabHeader(tab);
        tab.FileChangedExternally += OnFileChangedExternally;

        if (_layoutBuild == null || !_layoutBuild.LeafChrome.ContainsKey(leaf.Id))
            RebuildEditorLayoutUi();

        var chrome = _layoutBuild!.LeafChrome[leaf.Id];
        tab.HeaderElement = _tabHeaderFactory.CreateHeader(tab, chrome.TabStrip, chrome.TabDropIndicator);
        SyncStripChildrenToTabList(chrome.TabStrip, leaf.Tabs);
        tab.Editor.GotKeyboardFocus += (_, _) => OnEditorPaneKeyboardFocus(tab);
        return tab;
    }

    private void ActivateTab(TabInfo tab)
    {
        var leaf = EditorLayoutTree.FindLeafForTab(_editorLayoutRoot, tab);
        if (leaf == null) return;
        leaf.ActiveTab = tab;
        _focusedLeafId = leaf.Id;
        UpdateActiveTabHooks(tab);
        RefreshEditorHostsAndVisibility();
        UpdateAllTabHeaders();
        tab.HeaderElement.BringIntoView();
        _explorerPanel.SelectFile(tab.FilePath);
        Keyboard.Focus(tab.Editor);
    }

    private void ActivateTabAsSinglePane(TabInfo tab) => ActivateTab(tab);

    private void OnEditorPaneKeyboardFocus(TabInfo tab)
    {
        var leaf = EditorLayoutTree.FindLeafForTab(_editorLayoutRoot, tab);
        if (leaf == null) return;
        if (tab != _activeTab)
            ActivateTab(tab);
        else if (_focusedLeafId != leaf.Id)
            FocusLeaf(leaf.Id, updateHooks: false);
    }

    private void FocusLeaf(string leafId, bool updateHooks = true)
    {
        var leaf = EditorLayoutTree.FindLeafById(_editorLayoutRoot, leafId);
        if (leaf?.ActiveTab == null) return;
        _focusedLeafId = leaf.Id;
        if (updateHooks)
            UpdateActiveTabHooks(leaf.ActiveTab);
        UpdateAllTabHeaders();
        Keyboard.Focus(leaf.ActiveTab.Editor);
    }

    private void EnsureSecondTabForSplit()
    {
        var leaf = GetFocusedLeaf();
        if (leaf.Tabs.Count >= 2) return;
        CreateTab();
    }

    private void EnterEditorSplit()
    {
        if (_activeTab == null) return;
        EnsureSecondTabForSplit();
        var leaf = GetFocusedLeaf();
        if (leaf.Tabs.Count < 2) return;
        if (!EditorLayoutTree.TrySplitLeaf(ref _editorLayoutRoot, leaf, _activeTab, EditorSplitOrientation.Horizontal,
                out var secondLeaf))
            return;
        _focusedLeafId = secondLeaf.Id;
        RebuildEditorLayoutUi();
        UpdateActiveTabHooks(_activeTab);
        UpdateAllTabHeaders();
        UpdateEditorSplitMenuState();
        Keyboard.Focus(secondLeaf.ActiveTab!.Editor);
    }

    private void JoinEditorFlattenAll()
    {
        if (!EditorLayoutTree.IsSplit(_editorLayoutRoot)) return;
        _editorLayoutRoot = EditorLayoutTree.FlattenToSingleLeaf(_editorLayoutRoot, _activeTab);
        _focusedLeafId = ((EditorLeafNode)_editorLayoutRoot).Id;
        RebuildEditorLayoutUi();
        if (_activeTab != null)
            Keyboard.Focus(_activeTab.Editor);
        UpdateAllTabHeaders();
        UpdateEditorSplitMenuState();
        RefreshEditorTabSplitDragHost();
    }

    private void JoinEditorWithSibling()
    {
        if (!EditorLayoutTree.TryJoinFocusedLeafWithSibling(ref _editorLayoutRoot, _focusedLeafId, _activeTab,
                out var mergedId))
            return;
        _focusedLeafId = mergedId;
        RebuildEditorLayoutUi();
        if (_activeTab != null)
        {
            UpdateActiveTabHooks(_activeTab);
            Keyboard.Focus(_activeTab.Editor);
        }

        UpdateAllTabHeaders();
        UpdateEditorSplitMenuState();
        RefreshEditorTabSplitDragHost();
    }

    private void EnsureSingleLeafLayoutForRestore()
    {
        if (!EditorLayoutTree.IsSplit(_editorLayoutRoot)) return;
        _editorLayoutRoot = EditorLayoutTree.FlattenToSingleLeaf(_editorLayoutRoot, null);
        _focusedLeafId = ((EditorLeafNode)_editorLayoutRoot).Id;
        RebuildEditorLayoutUi();
        RefreshEditorTabSplitDragHost();
    }

    private void ToggleParentSplitOrientation()
    {
        var parent = EditorLayoutTree.FindParentSplitOfLeaf(_editorLayoutRoot, _focusedLeafId);
        if (parent == null) return;
        EditorLayoutTree.ToggleOrientation(parent);
        RebuildEditorLayoutUi();
        RefreshEditorHostsAndVisibility();
        FocusLeaf(_focusedLeafId);
    }

    /// <summary>Bound to Ctrl+\ — splits the focused editor group (nested splits supported). Use Join commands to collapse.</summary>
    private void ToggleEditorSplitFromCommand() => EnterEditorSplit();

    private void FocusNextEditorLeafFromCommand(int delta = 1)
    {
        if (!EditorLayoutTree.IsSplit(_editorLayoutRoot)) return;
        var next = EditorLayoutTree.NextLeaf(_editorLayoutRoot, _focusedLeafId, delta);
        if (next?.ActiveTab == null) return;
        FocusLeaf(next.Id);
    }

    private void UpdateEditorSplitMenuState()
    {
        bool split = EditorLayoutTree.IsSplit(_editorLayoutRoot);
        MenuSplitEditor.IsEnabled = _activeTab != null;
        MenuJoinEditor.IsEnabled = split && EditorLayoutTree.FindParentSplitOfLeaf(_editorLayoutRoot, _focusedLeafId) != null;
        MenuJoinEditorAll.IsEnabled = split;
        MenuSplitOrientation.IsEnabled = split && EditorLayoutTree.FindParentSplitOfLeaf(_editorLayoutRoot, _focusedLeafId) != null;
    }

    private void OnSplitEditor(object sender, RoutedEventArgs e) => EnterEditorSplit();

    private void OnJoinEditor(object sender, RoutedEventArgs e) => JoinEditorWithSibling();

    private void OnJoinEditorAll(object sender, RoutedEventArgs e) => JoinEditorFlattenAll();

    private void OnToggleSplitOrientation(object sender, RoutedEventArgs e) => ToggleParentSplitOrientation();

    private static void UpdateTabOverflowIndicators(ScrollViewer sv, Border left, Border right)
    {
        double offset = sv.HorizontalOffset;
        double scrollable = sv.ScrollableWidth;
        left.Visibility = offset > 1 ? Visibility.Visible : Visibility.Collapsed;
        right.Visibility = offset < scrollable - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTabOverflowBrushes()
    {
        if (_layoutBuild == null) return;
        var color = (Application.Current.Resources[ThemeResourceKeys.TabBarBg] as SolidColorBrush)?.Color ?? Colors.Black;
        var transparent = Color.FromArgb(0, color.R, color.G, color.B);

        var leftBrush = new LinearGradientBrush();
        leftBrush.StartPoint = new Point(0, 0);
        leftBrush.EndPoint = new Point(1, 0);
        leftBrush.GradientStops.Add(new GradientStop(color, 0.0));
        leftBrush.GradientStops.Add(new GradientStop(color, 0.6));
        leftBrush.GradientStops.Add(new GradientStop(transparent, 1.0));

        var rightBrush = new LinearGradientBrush();
        rightBrush.StartPoint = new Point(0, 0);
        rightBrush.EndPoint = new Point(1, 0);
        rightBrush.GradientStops.Add(new GradientStop(transparent, 0.0));
        rightBrush.GradientStops.Add(new GradientStop(color, 0.4));
        rightBrush.GradientStops.Add(new GradientStop(color, 1.0));

        foreach (var c in _layoutBuild.LeafChrome.Values)
        {
            c.TabOverflowLeft.Background = leftBrush;
            c.TabOverflowRight.Background = rightBrush;
        }
    }

    private void RemoveTab(TabInfo tab, bool recordClosedTabHistory = true)
    {
        if (recordClosedTabHistory && tab.FilePath is { } closedPath && File.Exists(closedPath))
            _closedTabPaths.Add(Path.GetFullPath(closedPath));

        var leaf = EditorLayoutTree.FindLeafForTab(_editorLayoutRoot, tab);
        if (leaf == null) return;
        int idx = leaf.Tabs.IndexOf(tab);

        if (tab == leaf.ActiveTab)
            leaf.ActiveTab = PickReplacementWhenRemovingTab(leaf.Tabs, tab);

        leaf.Tabs.Remove(tab);

        if (tab == _activeTab)
        {
            tab.Editor.DirtyChanged -= OnActiveDirtyChanged;
            tab.Editor.CaretMoved -= OnActiveCaretMoved;
            _activeTab = null;
        }

        tab.FileChangedExternally -= OnFileChangedExternally;
        tab.StopWatching();

        bool wasLarge = tab.Editor.ReleaseResources();

        _editorLayoutRoot = EditorLayoutTree.SimplifyEmptyLeaves(_editorLayoutRoot);
        if (EditorLayoutTree.FindLeafById(_editorLayoutRoot, _focusedLeafId) == null)
        {
            var first = EditorLayoutTree.EnumerateLeaves(_editorLayoutRoot).FirstOrDefault();
            if (first != null)
                _focusedLeafId = first.Id;
        }

        RebuildEditorLayoutUi();

        if (EditorLayoutTree.AllTabsOrdered(_editorLayoutRoot).Count == 0)
        {
            var newTab = CreateTab();
            ActivateTab(newTab);
        }
        else if (_activeTab == null)
        {
            var ordered = EditorLayoutTree.AllTabsOrdered(_editorLayoutRoot);
            var pick = ordered[0];
            UpdateActiveTabHooks(pick);
            RefreshEditorHostsAndVisibility();
            UpdateAllTabHeaders();
            Keyboard.Focus(pick.Editor);
        }
        else
        {
            RefreshEditorHostsAndVisibility();
            UpdateAllTabHeaders();
        }

        if (wasLarge)
            GC.Collect(2, GCCollectionMode.Optimized, false, false);
    }

    private void CommitTabMoveToLeaf(TabInfo tab, string targetLeafId, int insertIndex)
    {
        var srcLeaf = EditorLayoutTree.FindLeafForTab(_editorLayoutRoot, tab);
        var dstLeaf = EditorLayoutTree.FindLeafById(_editorLayoutRoot, targetLeafId);
        if (srcLeaf == null || dstLeaf == null || ReferenceEquals(srcLeaf, dstLeaf)) return;

        if (tab == srcLeaf.ActiveTab)
            srcLeaf.ActiveTab = PickReplacementWhenRemovingTab(srcLeaf.Tabs, tab);
        srcLeaf.Tabs.Remove(tab);

        insertIndex = Math.Clamp(insertIndex, 0, dstLeaf.Tabs.Count);
        dstLeaf.Tabs.Insert(insertIndex, tab);
        dstLeaf.ActiveTab = tab;
        _focusedLeafId = dstLeaf.Id;
        UpdateActiveTabHooks(tab);

        _editorLayoutRoot = EditorLayoutTree.SimplifyEmptyLeaves(_editorLayoutRoot);
        if (EditorLayoutTree.FindLeafById(_editorLayoutRoot, _focusedLeafId) == null)
        {
            var fl = EditorLayoutTree.EnumerateLeaves(_editorLayoutRoot).FirstOrDefault();
            if (fl != null) _focusedLeafId = fl.Id;
        }

        RebuildEditorLayoutUi();
        UpdateAllTabHeaders();
        Keyboard.Focus(tab.Editor);
    }

    private void CommitTabReorder(TabInfo tab, int targetIdx)
    {
        var leaf = EditorLayoutTree.FindLeafForTab(_editorLayoutRoot, tab);
        if (leaf == null) return;
        var list = leaf.Tabs;
        int currentIdx = list.IndexOf(tab);
        if (currentIdx == targetIdx) return;

        list.RemoveAt(currentIdx);
        list.Insert(targetIdx, tab);

        SyncAllStripChildrenFromModel();
    }

    private void UpdateAllTabHeaders()
    {
        foreach (var tab in AllTabsOrdered())
        {
            string bgKey = ThemeResourceKeys.TabInactive;
            if (tab == _activeTab)
                bgKey = ThemeResourceKeys.TabActive;
            else if (EditorLayoutTree.FindLeafForTab(_editorLayoutRoot, tab) is { ActiveTab: { } la } &&
                     ReferenceEquals(la, tab) && tab != _activeTab)
                bgKey = ThemeResourceKeys.TabActiveSecondary;

            if (tab.HeaderElement != null)
                tab.HeaderElement.SetResourceReference(Border.BackgroundProperty, bgKey);
        }
    }

    private void SwitchTab(int direction)
    {
        if (_activeTab == null) return;
        var leaf = GetFocusedLeaf();
        if (leaf.Tabs.Count <= 1) return;
        int i = leaf.Tabs.IndexOf(_activeTab);
        if (i < 0) return;
        int n = (i + direction + leaf.Tabs.Count) % leaf.Tabs.Count;
        ActivateTab(leaf.Tabs[n]);
    }

    internal bool TryHorizontalScrollEditorTabStrips(int delta)
    {
        if (_layoutBuild == null) return false;
        foreach (var c in _layoutBuild.LeafChrome.Values)
        {
            if (TryHorizontalScrollTabStrip(c.TabScrollViewer, delta))
                return true;
        }

        return false;
    }

    private void CloseAllTabsCore()
    {
        foreach (var tab in AllTabsOrdered().ToList())
        {
            tab.StopWatching();
            tab.Editor.ReleaseResources();
        }

        _editorLayoutRoot = new EditorLeafNode();
        _focusedLeafId = ((EditorLeafNode)_editorLayoutRoot).Id;
        _activeTab = null;
        RebuildEditorLayoutUi();
        RefreshEditorTabSplitDragHost();
    }

    private EditorLayoutSnapshot? BuildEditorLayoutSnapshotForSave() =>
        EditorLayoutSnapshotSerializer.BuildSnapshot(_editorLayoutRoot, _focusedLeafId);

    private void ApplyRestoredEditorLayoutIfAny(EditorLayoutSnapshot? snapshot)
    {
        if (snapshot?.Root == null) return;
        var ordered = AllTabsOrdered();
        var root = EditorLayoutSnapshotSerializer.MaterializeFromDto(snapshot.Root, ordered);
        if (root == null) return;
        _editorLayoutRoot = root;
        _focusedLeafId = snapshot.FocusedLeafId ?? EditorLayoutTree.EnumerateLeaves(root).First().Id;
        foreach (var leaf in EditorLayoutTree.EnumerateLeaves(_editorLayoutRoot))
        {
            if (_activeTab != null && leaf.Tabs.Contains(_activeTab))
                leaf.ActiveTab = _activeTab;
            else
                leaf.ActiveTab = leaf.Tabs.FirstOrDefault();
        }

        RebuildEditorLayoutUi();
    }
}
