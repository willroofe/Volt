using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Volt;

public partial class MainWindow : Window
{
    private readonly List<TabInfo> _tabs = [];
    private TabInfo? _activeTab;
    private AppSettings _settings;
    private readonly ProjectManager _projectManager = new();

    // Tab drag-to-reorder state
    private TabInfo? _dragTab;
    private Point _dragStartPos;
    private bool _isTabDragging;
    private int _dragTargetIndex = -1;
    private System.Windows.Controls.Primitives.Popup? _dragGhost;

    private EditorControl Editor => _activeTab!.Editor;

    private ThemeManager ThemeManager => App.Current.ThemeManager;
    private SyntaxManager SyntaxManager => App.Current.SyntaxManager;

    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    public MainWindow()
    {
        InitializeComponent();
        _settings = App.Current.Settings;

        // Restore session or create initial tab
        RestoreSession();

        ApplySettings();
        UpdateTabOverflowBrushes();
        RestoreWindowPosition();

        // Restore explorer state
        if (_settings.Editor.Explorer.PanelVisible)
        {
            if (_settings.Editor.Explorer.OpenFolderPath is string folderPath && Directory.Exists(folderPath))
                ExplorerPanel.OpenFolder(folderPath);
            SetExplorerVisible(true);
        }

        CmdPalette.Closed += (_, _) => { if (_activeTab != null) Keyboard.Focus(Editor); };
        FindBarControl.Closed += (_, _) => { if (_activeTab != null) Keyboard.Focus(Editor); };
        TabScrollViewer.ScrollChanged += (_, _) => UpdateTabOverflowIndicators();
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
        Activated += (_, _) => CheckAllTabsForExternalChanges();
        ThemeManager.ThemeChanged += (_, _) => { ApplyDwmTheme(); UpdateTabOverflowBrushes(); };
        ExplorerPanel.FileOpenRequested += OnExplorerFileOpen;
        ExplorerPanel.SetProjectManager(_projectManager);
        ExplorerPanel.AddFolderRequested += OnProjectAddFolder;
        ExplorerPanel.RemoveFolderRequested += OnProjectRemoveFolder;
        ExplorerPanel.NewVirtualFolderRequested += OnProjectNewVirtualFolder;
        ExplorerPanel.RemoveVirtualFolderRequested += OnProjectRemoveVirtualFolder;
        ExplorerPanel.RenameVirtualFolderRequested += OnProjectRenameVirtualFolder;
        ExplorerPanel.MoveToVirtualFolderRequested += OnProjectMoveToVirtualFolder;
        ExplorerPanel.CloseProjectRequested += CloseCurrentProject;
        ExplorerSplitter.DragCompleted += (_, _) =>
        {
            _settings.Editor.Explorer.PanelWidth = ExplorerColumn.ActualWidth;
            _settings.Save();
        };
        SourceInitialized += (_, _) =>
        {
            ApplyDwmTheme();
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        };
    }

    private TabInfo CreateTab(string? filePath = null)
    {
        var tab = new TabInfo(ThemeManager, SyntaxManager) { FilePath = filePath };
        _tabs.Add(tab);

        // Wire up per-tab dirty handler (for tab header updates — lives for the tab's lifetime)
        tab.Editor.DirtyChanged += (_, _) => UpdateTabHeader(tab);
        tab.FileChangedExternally += OnFileChangedExternally;

        tab.HeaderElement = CreateTabHeader(tab);
        TabStrip.Children.Add(tab.HeaderElement);
        return tab;
    }

    private void ActivateTab(TabInfo tab)
    {
        // Unhook events from previous active tab
        if (_activeTab != null)
        {
            _activeTab.Editor.DirtyChanged -= OnActiveDirtyChanged;
            _activeTab.Editor.CaretMoved -= OnActiveCaretMoved;
        }

        _activeTab = tab;

        // Swap the editor into the host
        EditorHost.Child = tab.ScrollHost;

        // Hook events for the new active tab
        tab.Editor.DirtyChanged += OnActiveDirtyChanged;
        tab.Editor.CaretMoved += OnActiveCaretMoved;

        // Update FindBar to target the new editor
        FindBarControl.SetEditor(tab.Editor);
        FindBarControl.RefreshSearch();

        // Apply current settings to the editor
        ApplySettingsToEditor(tab.Editor);

        // Update UI
        UpdateTitle();
        UpdateFileType();
        UpdateCaretPos();
        UpdateAllTabHeaders();
        BringTabIntoView(tab);

        Keyboard.Focus(tab.Editor);
    }

    private void BringTabIntoView(TabInfo tab)
    {
        tab.HeaderElement.BringIntoView();
    }

    private void OnTabScrollViewerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScrollViewer.ScrollToHorizontalOffset(
            TabScrollViewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void UpdateTabOverflowIndicators()
    {
        double offset = TabScrollViewer.HorizontalOffset;
        double scrollable = TabScrollViewer.ScrollableWidth;
        TabOverflowLeft.Visibility = offset > 1 ? Visibility.Visible : Visibility.Collapsed;
        TabOverflowRight.Visibility = offset < scrollable - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTabOverflowBrushes()
    {
        var color = (Application.Current.Resources["ThemeTabBarBg"] as SolidColorBrush)?.Color ?? Colors.Black;
        var transparent = Color.FromArgb(0, color.R, color.G, color.B);

        var leftBrush = new LinearGradientBrush();
        leftBrush.StartPoint = new Point(0, 0);
        leftBrush.EndPoint = new Point(1, 0);
        leftBrush.GradientStops.Add(new GradientStop(color, 0.0));
        leftBrush.GradientStops.Add(new GradientStop(color, 0.6));
        leftBrush.GradientStops.Add(new GradientStop(transparent, 1.0));
        TabOverflowLeft.Background = leftBrush;

        var rightBrush = new LinearGradientBrush();
        rightBrush.StartPoint = new Point(0, 0);
        rightBrush.EndPoint = new Point(1, 0);
        rightBrush.GradientStops.Add(new GradientStop(transparent, 0.0));
        rightBrush.GradientStops.Add(new GradientStop(color, 0.4));
        rightBrush.GradientStops.Add(new GradientStop(color, 1.0));
        TabOverflowRight.Background = rightBrush;
    }

    private void CloseTab(TabInfo tab)
    {
        if (tab.Editor.IsDirty && !PromptSaveTab(tab)) return;

        int idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        TabStrip.Children.Remove(tab.HeaderElement);

        // Unhook events if this was the active tab
        if (tab == _activeTab)
        {
            tab.Editor.DirtyChanged -= OnActiveDirtyChanged;
            tab.Editor.CaretMoved -= OnActiveCaretMoved;
            _activeTab = null;
        }

        tab.FileChangedExternally -= OnFileChangedExternally;
        tab.StopWatching();

        // Release undo history and buffer to free memory immediately
        bool wasLarge = tab.Editor.ReleaseResources();

        if (_tabs.Count == 0)
        {
            // Always keep at least one tab
            var newTab = CreateTab();
            ActivateTab(newTab);
        }
        else if (_activeTab == null)
        {
            int nextIdx = Math.Min(idx, _tabs.Count - 1);
            ActivateTab(_tabs[nextIdx]);
        }

        if (wasLarge)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }
    }

    private Border CreateTabHeader(TabInfo tab)
    {
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
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFg");

        var closeBtn = new Button { Style = (Style)FindResource("TabCloseButton") };
        closeBtn.Click += (_, _) => CloseTab(tab);

        var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(closeBtn, Dock.Right);
        panel.Children.Add(closeBtn);
        panel.Children.Add(textBlock);

        var header = new Border
        {
            Child = panel,
            Height = 33,
            MinWidth = 60,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        header.SetResourceReference(Border.BorderBrushProperty, "ThemeTabBorder");

        // Click to activate + drag to reorder
        header.MouseLeftButtonDown += (_, e) =>
        {
            ActivateTab(tab);
            _dragTab = tab;
            _dragStartPos = e.GetPosition(TabStrip);
            _isTabDragging = false;
            _dragTargetIndex = -1;
            header.CaptureMouse();
            e.Handled = true;
        };

        header.MouseMove += (_, e) =>
        {
            if (_dragTab != tab || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(TabStrip);
            if (!_isTabDragging)
            {
                if (Math.Abs(pos.X - _dragStartPos.X) < SystemParameters.MinimumHorizontalDragDistance)
                    return;
                _isTabDragging = true;
                ShowDragGhost(tab);
                header.Opacity = 0.4;
            }
            UpdateDragGhost(e);
            UpdateDropIndicator(pos.X, _tabs.IndexOf(tab));
        };

        header.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragTab == tab)
            {
                header.ReleaseMouseCapture();
                if (_isTabDragging)
                {
                    header.Opacity = 1.0;
                    HideDragGhost();
                    TabDropIndicator.Visibility = Visibility.Collapsed;
                    if (_dragTargetIndex >= 0)
                        CommitTabReorder(tab, _dragTargetIndex);
                }
                _dragTab = null;
                _isTabDragging = false;
                _dragTargetIndex = -1;
            }
        };

        // Middle-click to close tab
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                CloseTab(tab);
                e.Handled = true;
            }
        };

        return header;
    }

    private void ShowDragGhost(TabInfo tab)
    {
        var text = new TextBlock
        {
            Text = tab.DisplayName,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFg");

        var border = new Border
        {
            Child = text,
            Height = 30,
            MinWidth = 60,
            CornerRadius = new CornerRadius(4),
            Opacity = 0.85
        };
        border.SetResourceReference(Border.BackgroundProperty, "ThemeTabActive");
        border.SetResourceReference(Border.BorderBrushProperty, "ThemeTabBorder");
        border.BorderThickness = new Thickness(1);

        _dragGhost = new System.Windows.Controls.Primitives.Popup
        {
            Child = border,
            AllowsTransparency = true,
            Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint,
            IsHitTestVisible = false,
            IsOpen = true
        };
    }

    private void UpdateDragGhost(MouseEventArgs e)
    {
        if (_dragGhost == null) return;
        var screenPos = PointToScreen(e.GetPosition(this));
        // PointToScreen returns physical pixels; Popup offsets use DIPs — convert back
        var source = PresentationSource.FromVisual(this);
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

    private void UpdateDropIndicator(double mouseX, int dragSourceIdx)
    {
        // Calculate the insertion index and the X position for the indicator line
        double offset = 0;
        int insertIdx = -1;
        double indicatorX = 0;

        for (int i = 0; i < TabStrip.Children.Count; i++)
        {
            if (TabStrip.Children[i] is FrameworkElement el)
            {
                double width = el.ActualWidth;
                double midpoint = offset + width / 2;
                if (mouseX < midpoint)
                {
                    insertIdx = i;
                    indicatorX = offset;
                    break;
                }
                offset += width;
            }
        }

        if (insertIdx < 0)
        {
            // Past the last tab — insert at end
            insertIdx = TabStrip.Children.Count;
            indicatorX = offset;
        }

        // If dropping at the same position or adjacent (no actual move), hide indicator
        if (insertIdx == dragSourceIdx || insertIdx == dragSourceIdx + 1)
        {
            TabDropIndicator.Visibility = Visibility.Collapsed;
            _dragTargetIndex = -1;
            return;
        }

        _dragTargetIndex = insertIdx > dragSourceIdx ? insertIdx - 1 : insertIdx;
        TabDropIndicator.Visibility = Visibility.Visible;
        TabDropIndicator.Margin = new Thickness(indicatorX - 1, 0, 0, 0);
    }

    private void CommitTabReorder(TabInfo tab, int targetIdx)
    {
        int currentIdx = _tabs.IndexOf(tab);
        if (targetIdx == currentIdx) return;

        _tabs.RemoveAt(currentIdx);
        _tabs.Insert(targetIdx, tab);

        TabStrip.Children.Remove(tab.HeaderElement);
        TabStrip.Children.Insert(targetIdx, tab.HeaderElement);
    }

    private void UpdateTabHeader(TabInfo tab)
    {
        if (tab.HeaderElement?.Child is DockPanel panel)
        {
            // TextBlock is the last child (fill)
            foreach (var child in panel.Children)
            {
                if (child is TextBlock tb)
                {
                    var name = tab.DisplayName;
                    tb.Text = tab.Editor.IsDirty ? "\u2022 " + name : name;
                    break;
                }
            }
        }
        // Also update window title if this is the active tab
        if (tab == _activeTab) UpdateTitle();
    }

    private void UpdateAllTabHeaders()
    {
        foreach (var tab in _tabs)
        {
            var isActive = tab == _activeTab;
            if (tab.HeaderElement != null)
            {
                tab.HeaderElement.SetResourceReference(Border.BackgroundProperty,
                    isActive ? "ThemeTabActive" : "ThemeTabInactive");
            }
        }
    }

    private void SwitchTab(int direction)
    {
        if (_tabs.Count <= 1 || _activeTab == null) return;
        int idx = _tabs.IndexOf(_activeTab);
        int next = (idx + direction + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[next]);
    }

    private void OnActiveDirtyChanged(object? sender, EventArgs e) => UpdateTitle();
    private void OnActiveCaretMoved(object? sender, EventArgs e) => UpdateCaretPos();

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST && WindowState != WindowState.Maximized)
        {
            int x = (short)(lParam.ToInt64() & 0xFFFF);
            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var pt = PointFromScreen(new Point(x, y));
            const int side = 3;
            const int edge = 6;

            bool left = pt.X < side + 1;
            bool right = pt.X >= ActualWidth - side;
            bool top = pt.Y < side + 1;
            bool bottom = pt.Y >= ActualHeight - edge;

            if (top || bottom || left || right)
            {
                handled = true;
                if (top && left) return (IntPtr)HTTOPLEFT;
                if (top && right) return (IntPtr)HTTOPRIGHT;
                if (bottom && left) return (IntPtr)HTBOTTOMLEFT;
                if (bottom && right) return (IntPtr)HTBOTTOMRIGHT;
                if (left) return (IntPtr)HTLEFT;
                if (right) return (IntPtr)HTRIGHT;
                if (top) return (IntPtr)HTTOP;
                return (IntPtr)HTBOTTOM;
            }
        }

        if (msg == WM_MOUSEHWHEEL)
        {
            int delta = (short)(wParam.ToInt64() >> 16);
            var pos = Mouse.GetPosition(TabScrollViewer);
            bool overTabBar = pos.Y >= 0 && pos.Y <= TabScrollViewer.ActualHeight
                           && pos.X >= 0 && pos.X <= TabScrollViewer.ActualWidth;

            if (overTabBar)
            {
                TabScrollViewer.ScrollToHorizontalOffset(
                    TabScrollViewer.HorizontalOffset + delta);
                handled = true;
            }
            else if (_activeTab != null)
            {
                _activeTab.Editor.SetHorizontalOffset(
                    _activeTab.Editor.HorizontalOffset + delta);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ApplyDwmTheme() => DwmHelper.ApplyTheme(this, ThemeManager);

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            var thickness = SystemParameters.WindowResizeBorderThickness;
            BorderThickness = new Thickness(
                thickness.Left + 1, thickness.Top + 1,
                thickness.Right + 1, thickness.Bottom + 1);
        }
        else
        {
            BorderThickness = new Thickness(0);
        }

        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void ApplySettings()
    {
        foreach (var tab in _tabs)
            ApplySettingsToEditor(tab.Editor);
        FindBarControl.SetPosition(_settings.Editor.Find.BarPosition);
        MenuWordWrap.IsChecked = _settings.Editor.WordWrap;
    }

    private void ApplySettingsToEditor(EditorControl editor)
    {
        editor.TabSize = _settings.Editor.TabSize;
        editor.WordWrap = _settings.Editor.WordWrap;
        editor.BlockCaret = _settings.Editor.Caret.BlockCaret;
        editor.CaretBlinkMs = _settings.Editor.Caret.BlinkMs;
        if (_settings.Editor.Font.Family != null) editor.FontFamilyName = _settings.Editor.Font.Family;
        editor.EditorFontSize = _settings.Editor.Font.Size;
        editor.EditorFontWeight = _settings.Editor.Font.Weight;
        editor.LineHeightMultiplier = _settings.Editor.Font.LineHeight;
    }

    private void ToggleExplorer()
    {
        bool show = ExplorerColumn.Width.Value == 0;
        SetExplorerVisible(show);
        _settings.Editor.Explorer.PanelVisible = show;
        _settings.Save();
    }

    private void SetExplorerVisible(bool visible)
    {
        if (visible)
        {
            double width = Math.Clamp(_settings.Editor.Explorer.PanelWidth, 150, 600);
            bool rightSide = _settings.Editor.Explorer.PanelSide == "Right";

            // Always: col0=explorer, col1=splitter, col2=editor
            // Use FlowDirection to mirror for right-side layout
            Grid.SetColumn(ExplorerPanel, 0);
            Grid.SetColumn(ExplorerSplitter, 1);
            Grid.SetColumn(EditorArea, 2);

            MainContentGrid.FlowDirection = rightSide ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            ExplorerPanel.FlowDirection = FlowDirection.LeftToRight;
            EditorArea.FlowDirection = FlowDirection.LeftToRight;

            ExplorerColumn.Width = new GridLength(width);
            ExplorerColumn.MinWidth = 150;
            ExplorerColumn.MaxWidth = 600;
            SplitterColumn.Width = new GridLength(1);
            EditorColumn.Width = new GridLength(1, GridUnitType.Star);
            EditorColumn.MinWidth = 0;
            EditorColumn.MaxWidth = double.PositiveInfinity;
            ExplorerSplitter.Visibility = Visibility.Visible;
            HeaderBorderBridge.Visibility = Visibility.Visible;
        }
        else
        {
            MainContentGrid.FlowDirection = FlowDirection.LeftToRight;
            ExplorerColumn.MinWidth = 0;
            ExplorerColumn.MaxWidth = double.PositiveInfinity;
            ExplorerColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            EditorColumn.Width = new GridLength(1, GridUnitType.Star);
            EditorColumn.MinWidth = 0;
            EditorColumn.MaxWidth = double.PositiveInfinity;
            ExplorerSplitter.Visibility = Visibility.Collapsed;
            HeaderBorderBridge.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenFolderInExplorer()
    {
        // Close project if one is open (mode exclusivity)
        if (_projectManager.CurrentProject != null)
            CloseCurrentProject();

        var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (_settings.Editor.Explorer.OpenFolderPath is string prev && Directory.Exists(prev))
            dlg.SelectedPath = prev;

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        ExplorerPanel.OpenFolder(dlg.SelectedPath);
        _settings.Editor.Explorer.OpenFolderPath = dlg.SelectedPath;
        SetExplorerVisible(true);
        _settings.Editor.Explorer.PanelVisible = true;
        _settings.Save();
    }

    private void CloseFolderInExplorer()
    {
        ExplorerPanel.CloseFolder();
        SetExplorerVisible(false);
        _settings.Editor.Explorer.OpenFolderPath = null;
        _settings.Editor.Explorer.PanelVisible = false;
        _settings.Save();
    }

    private void OnExplorerFileOpen(string path)
    {
        var tab = OpenFileInTab(path, reuseUntitled: true);
        if (tab != null)
        {
            ActivateTab(tab);
            FindBarControl.RefreshSearch();
        }
    }

    /// <summary>
    /// Opens a file in a tab, reusing an existing tab if already open.
    /// Returns the tab, or null if the file was too large or already active.
    /// When <paramref name="reuseUntitled"/> is true and the active tab is untitled and clean, it is reused.
    /// </summary>
    private TabInfo? OpenFileInTab(string path, bool reuseUntitled)
    {
        var fullPath = Path.GetFullPath(path);

        // Switch to existing tab if already open
        var existing = _tabs.FirstOrDefault(t =>
            t.FilePath != null && string.Equals(Path.GetFullPath(t.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return existing;

        if (!CheckFileSize(path)) return null;

        // Reuse current tab if untitled and clean
        TabInfo tab;
        if (reuseUntitled && _activeTab != null && _activeTab.FilePath == null && !_activeTab.Editor.IsDirty)
            tab = _activeTab;
        else
            tab = CreateTab();

        tab.FilePath = path;
        tab.FileEncoding = FileHelper.DetectEncoding(path);
        tab.Editor.SetContent(FileHelper.ReadAllText(path, tab.FileEncoding));
        tab.LastKnownFileSize = new FileInfo(path).Length;
        tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(path, tab.LastKnownFileSize);
        tab.StartWatching();
        UpdateTabHeader(tab);
        return tab;
    }

    private void RestoreSession()
    {
        // Restore project if one was open
        if (_settings.LastOpenProjectPath is string projPath && System.IO.File.Exists(projPath))
        {
            OpenProjectFromPath(projPath);
            return;
        }

        var session = _settings.Session;
        if (session.Tabs.Count > 0)
        {
            TabInfo? activeTab = null;
            int tabIndex = 0;
            foreach (var st in session.Tabs)
            {
                // Skip file-backed tabs whose file no longer exists
                if (st.FilePath != null && !st.IsDirty && !File.Exists(st.FilePath))
                    continue;

                var tab = CreateTab();

                if (st.FilePath != null && File.Exists(st.FilePath))
                {
                    tab.FilePath = st.FilePath;
                    tab.FileEncoding = FileHelper.DetectEncoding(st.FilePath);

                    if (st.IsDirty)
                    {
                        // Load the on-disk version first, then overlay saved dirty content
                        var savedContent = SessionSettings.LoadTabContent(tabIndex);
                        tab.Editor.SetContent(savedContent ?? FileHelper.ReadAllText(st.FilePath, tab.FileEncoding));
                        tab.Editor.MarkDirty();
                    }
                    else
                    {
                        tab.Editor.SetContent(FileHelper.ReadAllText(st.FilePath, tab.FileEncoding));
                    }

                    tab.LastKnownFileSize = new FileInfo(st.FilePath).Length;
                    tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(st.FilePath, tab.LastKnownFileSize);
                    tab.StartWatching();
                }
                else if (st.FilePath == null)
                {
                    // Untitled tab — restore saved content if available
                    var savedContent = SessionSettings.LoadTabContent(tabIndex);
                    if (savedContent != null)
                    {
                        tab.Editor.SetContent(savedContent);
                        tab.Editor.MarkDirty();
                    }
                }
                else
                {
                    // File no longer exists but was dirty — restore content as untitled
                    var savedContent = SessionSettings.LoadTabContent(tabIndex);
                    if (savedContent == null) { tabIndex++; continue; }
                    tab.FilePath = null;
                    tab.Editor.SetContent(savedContent);
                    tab.Editor.MarkDirty();
                }

                UpdateTabHeader(tab);

                // Defer caret/scroll restore until after layout so the editor has valid extents
                var savedTab = st;
                RoutedEventHandler? onLoaded = null;
                onLoaded = (_, _) =>
                {
                    tab.Editor.Loaded -= onLoaded;
                    tab.Editor.SetCaretPosition(savedTab.CaretLine, savedTab.CaretCol);
                    tab.Editor.SetVerticalOffset(savedTab.ScrollVertical);
                    tab.Editor.SetHorizontalOffset(savedTab.ScrollHorizontal);
                    tab.Editor.InvalidateVisual();
                };
                tab.Editor.Loaded += onLoaded;

                if (tabIndex == session.ActiveTabIndex)
                    activeTab = tab;
                tabIndex++;
            }

            if (_tabs.Count == 0)
            {
                var tab = CreateTab();
                ActivateTab(tab);
            }
            else
            {
                ActivateTab(activeTab ?? _tabs[0]);
            }
        }
        else
        {
            var tab = CreateTab();
            ActivateTab(tab);
        }

        // Clean up session files after restore
        SessionSettings.ClearSessionDir();
    }

    private void SaveSession()
    {
        SessionSettings.ClearSessionDir();

        var sessionTabs = new List<SessionTab>();
        int activeIdx = 0;
        foreach (var t in _tabs)
        {
            bool dirty = t.Editor.IsDirty;
            bool untitled = t.FilePath == null;

            // Skip empty untitled tabs
            if (untitled && !dirty && string.IsNullOrEmpty(t.Editor.GetContent()))
                continue;

            if (t == _activeTab)
                activeIdx = sessionTabs.Count;

            int idx = sessionTabs.Count;

            // Save content for dirty or untitled tabs
            if (dirty || untitled)
                _settings.Session.SaveTabContent(idx, t.Editor.GetContent());

            sessionTabs.Add(new SessionTab
            {
                FilePath = t.FilePath,
                IsDirty = dirty,
                CaretLine = t.Editor.CaretLine,
                CaretCol = t.Editor.CaretCol,
                ScrollVertical = t.Editor.VerticalOffset,
                ScrollHorizontal = t.Editor.HorizontalOffset,
            });
        }

        _settings.Session = new SessionSettings
        {
            ActiveTabIndex = activeIdx,
            Tabs = sessionTabs
        };
    }

    private void RestoreWindowPosition()
    {
        if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue
            && _settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
        {
            double left = _settings.WindowLeft.Value;
            double top = _settings.WindowTop.Value;
            double width = _settings.WindowWidth.Value;
            double height = _settings.WindowHeight.Value;

            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

            bool visible = left + 100 <= vsRight &&
                           top + 100 <= vsBottom &&
                           left + width >= vsLeft + 100 &&
                           top + height >= vsTop + 100;

            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }
        }

        if (_settings.WindowMaximized)
            Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save project session if a project is open
        if (_projectManager.CurrentProject != null)
        {
            try
            {
                CaptureProjectSession();
                _projectManager.SaveProject();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Project save failed: {ex.Message}");
            }
        }

        // Try to persist session; if it fails, fall back to prompting for dirty tabs
        bool sessionSaved = false;
        try
        {
            SaveSession();
            sessionSaved = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Session save failed: {ex.Message}");
        }

        if (!sessionSaved)
        {
            foreach (var tab in _tabs.ToList())
            {
                if (tab.Editor.IsDirty)
                {
                    ActivateTab(tab);
                    if (!PromptSaveTab(tab))
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }
        }

        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();

        foreach (var tab in _tabs)
            tab.StopWatching();
    }

    private void UpdateCaretPos()
    {
        if (_activeTab == null) return;
        CaretPosText.Text = $"Ln {Editor.CaretLine + 1}, Col {Editor.CaretCol + 1}";
        CharCountText.Text = $"{Editor.CharCount:N0} {(Editor.CharCount == 1 ? "Character" : "Characters")}";
    }

    private string GetEncodingLabel()
    {
        if (_activeTab == null) return "UTF-8";
        var enc = _activeTab.FileEncoding;
        if (enc is UTF8Encoding utf8)
            return utf8.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8";
        if (enc is UnicodeEncoding)
            return "UTF-16";
        return enc.EncodingName;
    }

    private void UpdateFileType()
    {
        if (_activeTab == null) return;
        var ext = _activeTab.FilePath != null ? Path.GetExtension(_activeTab.FilePath).ToLowerInvariant() : "";
        SyntaxManager.SetLanguageByExtension(ext);
        Editor.InvalidateSyntax();
        FileTypeText.Text = FileHelper.GetFileTypeName(ext);
        EncodingText.Text = GetEncodingLabel();
        LineEndingText.Text = Editor.LineEnding;
    }

    private void UpdateTitle()
    {
        if (_activeTab == null) return;
        Title = "Volt";
    }

    private const long MaxFileSizeBytes = 500L * 1024 * 1024;

    private bool CheckFileSize(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxFileSizeBytes) return true;
        double sizeMb = info.Length / (1024.0 * 1024.0);
        ThemedMessageBox.Show(this,
            $"The file is {sizeMb:F0} MB which exceeds the 500 MB limit.",
            "File Too Large");
        return false;
    }

    private bool PromptSaveTab(TabInfo tab)
    {
        if (!tab.Editor.IsDirty) return true;
        var name = tab.FilePath != null ? Path.GetFileName(tab.FilePath) : "Untitled";
        var result = ThemedMessageBox.Show(this,
            $"Do you want to save changes to {name}?",
            "Volt", MessageBoxButton.YesNoCancel);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) SaveTab(tab);
        return !tab.Editor.IsDirty || result == MessageBoxResult.No;
    }

    private void SaveTab(TabInfo tab)
    {
        if (tab.FilePath == null)
        {
            SaveTabAs(tab);
            return;
        }
        if (!WriteAndFinishSave(tab)) return;
        if (tab == _activeTab) UpdateTitle();
    }

    private void SaveTabAs(TabInfo tab)
    {
        int filterIndex = 1;
        if (tab.FilePath != null)
        {
            var ext = Path.GetExtension(tab.FilePath).ToLowerInvariant();
            filterIndex = ext switch
            {
                ".pl" => 2,
                ".txt" => 1,
                _ => 3
            };
        }
        var dlg = new SaveFileDialog
        {
            Filter = string.Join("|", FileHelper.SaveFilters),
            FilterIndex = filterIndex,
            FileName = tab.FilePath != null ? Path.GetFileName(tab.FilePath) : ""
        };
        if (dlg.ShowDialog() != true) return;
        tab.FilePath = dlg.FileName;
        if (!WriteAndFinishSave(tab)) return;
        if (tab == _activeTab)
        {
            UpdateTitle();
            UpdateFileType();
        }
    }

    /// <summary>
    /// Writes the tab content to disk and updates post-save state (watcher, file size, dirty flag, header).
    /// Returns false if the write failed (error already shown to user).
    /// </summary>
    private bool WriteAndFinishSave(TabInfo tab)
    {
        tab.StopWatching();
        try
        {
            FileHelper.AtomicWriteText(tab.FilePath!, tab.Editor.GetContent(), tab.FileEncoding);
        }
        catch (Exception ex)
        {
            tab.StartWatching();
            ThemedMessageBox.Show(this, $"Could not save '{tab.DisplayName}':\n\n{ex.Message}",
                "Save Failed");
            return false;
        }
        tab.StartWatching();
        tab.LastKnownFileSize = new FileInfo(tab.FilePath!).Length;
        tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(tab.FilePath!, tab.LastKnownFileSize);
        tab.Editor.MarkClean();
        UpdateTabHeader(tab);
        return true;
    }

    private void OnFileChangedExternally(TabInfo tab)
    {
        if (tab.IsHandlingExternalChange) return;
        if (tab.FilePath == null || !File.Exists(tab.FilePath)) return;

        var diskTime = File.GetLastWriteTimeUtc(tab.FilePath);
        if (diskTime <= tab.LastKnownWriteTimeUtc) return;

        tab.IsHandlingExternalChange = true;
        try
        {
            if (tab.Editor.IsDirty)
            {
                var result = ThemedMessageBox.Show(this,
                    $"'{tab.DisplayName}' has been modified externally.\n\nDo you want to reload it? Your unsaved changes will be lost.",
                    "File Changed", MessageBoxButton.YesNo);
                if (result != MessageBoxResult.Yes)
                {
                    tab.LastKnownWriteTimeUtc = diskTime;
                    return;
                }
            }

            ReloadTabFromDisk(tab);
        }
        finally
        {
            tab.IsHandlingExternalChange = false;
        }
    }

    private void CheckAllTabsForExternalChanges()
    {
        foreach (var tab in _tabs.ToList())
            OnFileChangedExternally(tab);
    }

    private void ReloadTabFromDisk(TabInfo tab)
    {
        if (tab.FilePath == null || !File.Exists(tab.FilePath)) return;

        try
        {
            var currentSize = new FileInfo(tab.FilePath).Length;

            // Fast path: file only grew, buffer is clean, and old content is untouched
            if (!tab.Editor.IsDirty
                && currentSize > tab.LastKnownFileSize
                && tab.LastKnownFileSize > 0
                && tab.TailVerifyBytes != null
                && FileHelper.VerifyAppendOnly(tab.FilePath, tab.LastKnownFileSize, tab.TailVerifyBytes))
            {
                var (tail, newSize) = FileHelper.ReadTail(tab.FilePath, tab.FileEncoding, tab.LastKnownFileSize);
                if (tail.Length > 0)
                    tab.Editor.AppendContent(tail);
                tab.LastKnownFileSize = newSize;
                tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(tab.FilePath, newSize);
            }
            else
            {
                // Full reload: file was truncated, edited in place, or this is the first load
                tab.FileEncoding = FileHelper.DetectEncoding(tab.FilePath);
                tab.Editor.ReloadContent(FileHelper.ReadAllText(tab.FilePath, tab.FileEncoding));
                tab.LastKnownFileSize = currentSize;
                tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(tab.FilePath, currentSize);
            }

            tab.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(tab.FilePath);
            tab.Editor.MarkClean();
            UpdateTabHeader(tab);
            if (tab == _activeTab) UpdateTitle();
        }
        catch (IOException)
        {
            // File may still be locked by the writing process; the watcher will fire again
        }
    }

    private void OnNewTab(object sender, RoutedEventArgs e)
    {
        var tab = CreateTab();
        ActivateTab(tab);
    }

    private void OnNew(object sender, RoutedEventArgs e) => OnNewTab(sender, e);

    private void OnOpenFolder(object sender, RoutedEventArgs e) => OpenFolderInExplorer();

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "All Files (*.*)|*.*|Text Files (*.txt)|*.txt|Perl Files (*.pl)|*.pl",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        TabInfo? lastTab = null;
        foreach (var fileName in dlg.FileNames)
        {
            // Only reuse untitled tab for the first file opened
            var tab = OpenFileInTab(fileName, reuseUntitled: lastTab == null);
            if (tab != null)
                lastTab = tab;
        }

        if (lastTab != null)
        {
            ActivateTab(lastTab);
            FindBarControl.RefreshSearch();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        SaveTab(_activeTab);
    }


    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        SaveTabAs(_activeTab);
    }

    private void OnToggleWordWrap(object sender, RoutedEventArgs e)
    {
        // When triggered from menu click, IsChecked is already toggled.
        // When triggered from command palette, we need to flip it.
        if (sender != MenuWordWrap)
            MenuWordWrap.IsChecked = !MenuWordWrap.IsChecked;
        _settings.Editor.WordWrap = MenuWordWrap.IsChecked;
        foreach (var tab in _tabs)
            tab.Editor.WordWrap = _settings.Editor.WordWrap;
        _settings.Save();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var snapshot = new SettingsSnapshot(
            Editor.TabSize, _settings.Editor.Caret.BlockCaret, _settings.Editor.Caret.BlinkMs,
            Editor.FontFamilyName, Editor.EditorFontSize, Editor.EditorFontWeight,
            Editor.LineHeightMultiplier, _settings.Application.ColorTheme, _settings.Editor.Find.BarPosition,
            _settings.Editor.Explorer.PanelSide);
        var dlg = new SettingsWindow(ThemeManager, snapshot) { Owner = this };
        dlg.Applied += (_, _) => ApplySettingsFromDialog(dlg);
        if (dlg.ShowDialog() == true)
            ApplySettingsFromDialog(dlg);
    }

    private void ApplySettingsFromDialog(SettingsWindow dlg)
    {
        _settings.Editor.TabSize = dlg.TabSize;
        _settings.Editor.Caret.BlockCaret = dlg.BlockCaret;
        _settings.Editor.Caret.BlinkMs = dlg.CaretBlinkMs;
        _settings.Editor.Font.Family = dlg.SelectedFontFamily;
        _settings.Editor.Font.Size = dlg.SelectedFontSize;
        _settings.Editor.Font.Weight = dlg.SelectedFontWeight;
        _settings.Editor.Font.LineHeight = dlg.SelectedLineHeight;
        _settings.Application.ColorTheme = dlg.ColorThemeName;
        _settings.Editor.Find.BarPosition = dlg.FindBarPosition;
        _settings.Editor.Explorer.PanelSide = dlg.PanelSide;
        if (_settings.Editor.Explorer.PanelVisible)
            SetExplorerVisible(true);
        _settings.Save();
        ApplySettings();
        ThemeManager.Apply(dlg.ColorThemeName);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            Keyboard.ClearFocus();
            DragMove();
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnExit(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // Intercept Ctrl+Tab before WPF's built-in tab navigation
        if (ctrl && !shift && e.Key == Key.Tab) { SwitchTab(+1); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Tab) { SwitchTab(-1); e.Handled = true; return; }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && !shift && e.Key == Key.N) { OnNew(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.O) { OnOpen(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.F) { FindBarControl.Open(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.H) { FindBarControl.ToggleReplace(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.P) { OpenCommandPalette(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.O) { OpenFolderInExplorer(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.S) { OnSaveAs(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && (Keyboard.Modifiers & ModifierKeys.Alt) != 0 && (e.Key == Key.S || e.SystemKey == Key.S)) { OnSettings(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.S) { OnSave(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.W) { if (_activeTab != null) CloseTab(_activeTab); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { StepFontSize(1); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { StepFontSize(-1); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.B) { ToggleExplorer(); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    private void StepFontSize(int direction)
    {
        if (_activeTab == null) return;
        var sizes = AppSettings.FontSizeOptions;
        int idx = Array.IndexOf(sizes, Editor.EditorFontSize);
        if (idx < 0) idx = Array.IndexOf(sizes, 14);
        int next = idx + direction;
        if (next < 0 || next >= sizes.Length) return;
        var newSize = sizes[next];
        foreach (var tab in _tabs)
            tab.Editor.EditorFontSize = newSize;
        _settings.Editor.Font.Size = newSize;
        _settings.Save();
    }

    private void OpenCommandPalette()
    {
        if (_activeTab == null) return;
        var commands = CommandPaletteCommands.Build(new CommandPaletteContext(
            _tabs, _settings, ThemeManager, Editor, FindBarControl, () => _settings.Save(),
            ToggleExplorer, OpenFolderInExplorer, CloseFolderInExplorer,
            () => { if (_settings.Editor.Explorer.PanelVisible) SetExplorerVisible(true); },
            () => OnNewProject(this, new RoutedEventArgs()),
            () => OnOpenProject(this, new RoutedEventArgs()),
            () => OnSaveProject(this, new RoutedEventArgs()),
            CloseCurrentProject,
            () => OnToggleWordWrap(this, new RoutedEventArgs())));
        CmdPalette.SetCommands(commands);
        CmdPalette.Open();
    }

    // ── Project menu handlers ────────────────────────────────────────────────

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Project Files (*.vproj)|*.vproj",
            DefaultExt = ".vproj",
            FileName = "MyProject.vproj"
        };
        if (dlg.ShowDialog() != true) return;

        if (_projectManager.CurrentProject != null)
            CloseCurrentProject();
        else if (ExplorerPanel.OpenFolderPath != null)
            CloseFolderInExplorer();

        CloseAllTabs();

        _projectManager.NewProject(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName));
        _projectManager.CurrentProject!.FilePath = dlg.FileName;
        _projectManager.SaveProject();

        ExplorerPanel.OpenProject(_projectManager.CurrentProject);
        SetExplorerVisible(true);
        UpdateProjectMenuState(true);

        _settings.LastOpenProjectPath = dlg.FileName;
        _settings.Save();
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Project Files (*.vproj)|*.vproj",
            DefaultExt = ".vproj"
        };
        if (dlg.ShowDialog() != true) return;

        OpenProjectFromPath(dlg.FileName);
    }

    private void OnSaveProject(object sender, RoutedEventArgs e)
    {
        if (_projectManager.CurrentProject == null) return;
        CaptureProjectSession();
        _projectManager.SaveProject();
    }

    private void OnCloseProject(object sender, RoutedEventArgs e)
    {
        CloseCurrentProject();
    }

    // ── Project helpers ──────────────────────────────────────────────────────

    private void OpenProjectFromPath(string vprojPath)
    {
        if (!System.IO.File.Exists(vprojPath)) return;

        if (_projectManager.CurrentProject != null)
            CloseCurrentProject();
        else if (ExplorerPanel.OpenFolderPath != null)
            CloseFolderInExplorer();

        PromptSaveDirtyTabs();
        CloseAllTabs();

        var project = _projectManager.OpenProject(vprojPath);

        ExplorerPanel.OpenProject(project);
        SetExplorerVisible(true);
        UpdateProjectMenuState(true);

        _settings.LastOpenProjectPath = vprojPath;
        _settings.Editor.Explorer.PanelVisible = true;
        _settings.Save();

        RestoreProjectSession(project);
    }

    private void CloseCurrentProject()
    {
        if (_projectManager.CurrentProject == null) return;

        CaptureProjectSession();
        _projectManager.SaveProject();
        _projectManager.CloseProject();

        ExplorerPanel.CloseProject();
        UpdateProjectMenuState(false);

        _settings.LastOpenProjectPath = null;
        _settings.Save();

        CloseAllTabs();
        CreateTab();
        ActivateTab(_tabs[0]);
    }

    private void UpdateProjectMenuState(bool projectOpen)
    {
        MenuSaveProject.IsEnabled = projectOpen;
        MenuCloseProject.IsEnabled = projectOpen;
    }

    private void CloseAllTabs()
    {
        foreach (var tab in _tabs.ToList())
        {
            tab.StopWatching();
            tab.Editor.ReleaseResources();
        }
        _tabs.Clear();
        TabStrip.Children.Clear();
        _activeTab = null;
    }

    private void PromptSaveDirtyTabs()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (tab.Editor.IsDirty)
            {
                ActivateTab(tab);
                PromptSaveTab(tab);
            }
        }
    }

    // ── Session capture / restore ────────────────────────────────────────────

    private void CaptureProjectSession()
    {
        if (_projectManager.CurrentProject == null) return;

        var sessionTabs = new List<ProjectSessionTab>();
        int activeIdx = 0;

        foreach (var t in _tabs)
        {
            if (t == _activeTab)
                activeIdx = sessionTabs.Count;

            sessionTabs.Add(new ProjectSessionTab
            {
                FilePath = t.FilePath,
                IsDirty = t.Editor.IsDirty,
                CaretLine = t.Editor.CaretLine,
                CaretCol = t.Editor.CaretCol,
                ScrollX = t.Editor.HorizontalOffset,
                ScrollY = t.Editor.VerticalOffset,
            });
        }

        _projectManager.CurrentProject.Session = new ProjectSession
        {
            Tabs = sessionTabs,
            ActiveTabIndex = activeIdx
        };
    }

    private void RestoreProjectSession(Project project)
    {
        if (project.Session.Tabs.Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            return;
        }

        TabInfo? activeTab = null;
        for (int i = 0; i < project.Session.Tabs.Count; i++)
        {
            var st = project.Session.Tabs[i];
            if (st.FilePath != null && !System.IO.File.Exists(st.FilePath))
                continue;

            var tab = CreateTab();

            if (st.FilePath != null)
            {
                tab.FilePath = st.FilePath;
                tab.FileEncoding = FileHelper.DetectEncoding(st.FilePath);
                tab.Editor.SetContent(FileHelper.ReadAllText(st.FilePath, tab.FileEncoding));
                tab.LastKnownFileSize = new System.IO.FileInfo(st.FilePath).Length;
                tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(st.FilePath, tab.LastKnownFileSize);
                tab.StartWatching();
            }

            if (i == project.Session.ActiveTabIndex)
                activeTab = tab;

            UpdateTabHeader(tab);
        }

        if (_tabs.Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            return;
        }

        ActivateTab(activeTab ?? _tabs[0]);

        Dispatcher.InvokeAsync(() =>
        {
            for (int i = 0; i < _tabs.Count && i < project.Session.Tabs.Count; i++)
            {
                var st = project.Session.Tabs[i];
                var tab = _tabs[i];
                tab.Editor.SetCaretPosition(st.CaretLine, st.CaretCol);
                tab.ScrollHost?.ScrollToVerticalOffset(st.ScrollY);
                tab.ScrollHost?.ScrollToHorizontalOffset(st.ScrollX);
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Project context menu handlers ────────────────────────────────────────

    private void OnProjectAddFolder(string? virtualFolderName)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _projectManager.AddFolder(dlg.SelectedPath);
        if (virtualFolderName != null)
            _projectManager.AssignToVirtualFolder(dlg.SelectedPath, virtualFolderName);
        ExplorerPanel.RefreshProjectTree();
        _projectManager.SaveProject();
    }

    private void OnProjectRemoveFolder(string path)
    {
        _projectManager.RemoveFolder(path);
        ExplorerPanel.RefreshProjectTree();
        _projectManager.SaveProject();
    }

    private void OnProjectNewVirtualFolder()
    {
        var name = PromptForInput("New Virtual Folder", "Enter a name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        _projectManager.CreateVirtualFolder(name);
        ExplorerPanel.RefreshProjectTree();
        _projectManager.SaveProject();
    }

    private void OnProjectRemoveVirtualFolder(string name)
    {
        _projectManager.RemoveVirtualFolder(name);
        ExplorerPanel.RefreshProjectTree();
        _projectManager.SaveProject();
    }

    private void OnProjectRenameVirtualFolder(string oldName)
    {
        var newName = PromptForInput("Rename Virtual Folder", "Enter a new name:", oldName);
        if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

        _projectManager.RenameVirtualFolder(oldName, newName);
        ExplorerPanel.RefreshProjectTree();
        _projectManager.SaveProject();
    }

    private void OnProjectMoveToVirtualFolder(string folderPath, string? virtualFolderName)
    {
        _projectManager.AssignToVirtualFolder(folderPath, virtualFolderName);
        ExplorerPanel.RefreshProjectTree();
        _projectManager.SaveProject();
    }

    private static string? PromptForInput(string title, string prompt, string defaultValue = "")
    {
        var window = new Window
        {
            Title = title,
            Width = 380,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Owner = Application.Current.MainWindow
        };

        // Use WindowChrome for draggable title bar
        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 32,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(window, chrome);

        // Outer layout matching ThemedMessageBox structure
        var root = new DockPanel();
        root.SetResourceReference(DockPanel.BackgroundProperty, "ThemeContentBg");

        // Title bar
        var titleBar = new Grid { Height = 32 };
        titleBar.SetResourceReference(Grid.BackgroundProperty, "ThemeChromeBrush");
        DockPanel.SetDock(titleBar, Dock.Top);
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            IsHitTestVisible = false
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFg");
        Grid.SetColumn(titleText, 0);
        titleBar.Children.Add(titleText);

        var closeBtn = new Button
        {
            Content = "\uE8BB",
            Style = (Style)Application.Current.FindResource("CloseButton")
        };
        closeBtn.Click += (_, _) => { window.DialogResult = false; };
        Grid.SetColumn(closeBtn, 1);
        titleBar.Children.Add(closeBtn);
        root.Children.Add(titleBar);

        // Separator
        var sep = new Border { Height = 1 };
        sep.SetResourceReference(Border.BackgroundProperty, "ThemeBorderBrush");
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        // Content area
        var content = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptText = new TextBlock
        {
            Text = prompt,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        promptText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFg");
        Grid.SetRow(promptText, 0);
        content.Children.Add(promptText);

        var textBox = new TextBox
        {
            Text = defaultValue,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Padding = new Thickness(4, 3, 4, 3)
        };
        textBox.SetResourceReference(TextBox.BackgroundProperty, "ThemeContentBg");
        textBox.SetResourceReference(TextBox.ForegroundProperty, "ThemeTextFg");
        textBox.SetResourceReference(TextBox.BorderBrushProperty, "ThemeMenuPopupBorder");
        textBox.SetResourceReference(TextBox.CaretBrushProperty, "ThemeTextFg");
        textBox.SelectAll();
        Grid.SetRow(textBox, 1);
        content.Children.Add(textBox);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        Grid.SetRow(btnPanel, 2);

        // Helper to create themed buttons matching ThemedMessageBox's DialogButton style
        Button MakeButton(string text, bool isDefault, bool isCancel)
        {
            var btn = new Button
            {
                Content = text,
                MinWidth = 80,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(12, 6, 12, 6),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            btn.SetResourceReference(Button.ForegroundProperty, "ThemeButtonFg");

            // Build a simple template matching DialogButton style
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
            borderFactory.SetResourceReference(Border.BackgroundProperty, "ThemeContentBg");
            borderFactory.SetResourceReference(Border.BorderBrushProperty, "ThemeMenuPopupBorder");
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
            borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cpFactory);
            template.VisualTree = borderFactory;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new DynamicResourceExtension("ThemeButtonHover"), "Bd"));
            template.Triggers.Add(hoverTrigger);

            btn.Template = template;
            return btn;
        }

        var okBtn = MakeButton("OK", isDefault: true, isCancel: false);
        okBtn.Click += (_, _) => { window.DialogResult = true; };
        btnPanel.Children.Add(okBtn);

        var cancelBtn = MakeButton("Cancel", isDefault: false, isCancel: true);
        btnPanel.Children.Add(cancelBtn);

        content.Children.Add(btnPanel);
        root.Children.Add(content);

        window.Content = root;
        textBox.Focus();

        return window.ShowDialog() == true ? textBox.Text : null;
    }
}
