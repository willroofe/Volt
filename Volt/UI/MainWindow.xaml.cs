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
    private readonly AppSettings _settings;
    private readonly WorkspaceManager _workspaceManager = new();
    private readonly SessionManager _sessionManager = new();

    private readonly TabHeaderFactory _tabHeaderFactory = new();
    private readonly FileExplorerPanel ExplorerPanel = new();

    private EditorControl? Editor => _activeTab?.Editor;

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

        // Move editor content from root Grid into PanelShell center
        var rootGrid = (Grid)Content;
        rootGrid.Children.Remove(EditorColumnGrid);
        EditorColumnGrid.Visibility = Visibility.Visible;
        Shell.CenterContent = EditorColumnGrid;

        _settings = App.Current.Settings;

        _tabHeaderFactory.TabActivated += tab => ActivateTab(tab);
        _tabHeaderFactory.TabClosed += tab => CloseTab(tab);
        _tabHeaderFactory.TabReordered += CommitTabReorder;

        // Restore session or create initial tab
        RestoreSession();

        ApplySettings();
        UpdateTabOverflowBrushes();
        RestoreWindowPosition();

        // Register explorer panel with shell
        Shell.RegisterPanel(ExplorerPanel, PanelPlacement.Left, 250);
        RestorePanelLayout();
        SyncViewMenuChecks();

        if (ExplorerPanel.OpenFolderPath == null &&
            _settings.Editor.Explorer.OpenFolderPath is string folderPath && Directory.Exists(folderPath))
        {
            ExplorerPanel.OpenFolder(folderPath);
            if (_settings.Editor.Explorer.ExpandedPaths.Count > 0)
                ExplorerPanel.RestoreExpandedPaths(_settings.Editor.Explorer.ExpandedPaths);
        }

        CmdPalette.Closed += (_, _) => { if (Editor is { } ed) Keyboard.Focus(ed); };
        FindBarControl.Closed += (_, _) => { if (Editor is { } ed) Keyboard.Focus(ed); };
        TabScrollViewer.ScrollChanged += (_, _) => UpdateTabOverflowIndicators();
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
        Activated += (_, _) => CheckAllTabsForExternalChanges();
        ThemeManager.ThemeChanged += (_, _) => { ApplyDwmTheme(); UpdateTabOverflowBrushes(); };
        ExplorerPanel.FileOpenRequested += OnExplorerFileOpen;
        ExplorerPanel.SetWorkspaceManager(_workspaceManager);
        ExplorerPanel.AddFolderRequested += OnWorkspaceAddFolder;
        ExplorerPanel.RemoveFolderRequested += OnWorkspaceRemoveFolder;
        ExplorerPanel.CloseWorkspaceRequested += CloseCurrentWorkspace;
        ExplorerPanel.FileRenamed += OnExplorerFileRenamed;
        ExplorerPanel.FileDeleted += OnExplorerFileDeleted;
        Shell.PanelLayoutChanged += OnPanelLayoutChanged;
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
        ExplorerPanel.SelectFile(tab.FilePath);

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
        var color = (Application.Current.Resources[ThemeResourceKeys.TabBarBg] as SolidColorBrush)?.Color ?? Colors.Black;
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
        RemoveTab(tab);
    }

    private void RemoveTab(TabInfo tab)
    {
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
            GC.Collect(2, GCCollectionMode.Optimized, false, false);
    }

    private Border CreateTabHeader(TabInfo tab) =>
        _tabHeaderFactory.CreateHeader(tab, TabStrip, TabDropIndicator);

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
                    isActive ? ThemeResourceKeys.TabActive : ThemeResourceKeys.TabInactive);
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
            BorderThickness = new Thickness(0, 0, 1, 1);
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
        Shell.TogglePanel("file-explorer");
    }

    private void RestorePanelLayout()
    {
        if (_settings.Editor.PanelLayouts.Count > 0)
            Shell.RestoreLayout(_settings.Editor.PanelLayouts);
        if (_settings.Editor.OpenRegions.Count > 0)
            Shell.RestoreOpenRegions(_settings.Editor.OpenRegions);
    }

    private void OnPanelLayoutChanged(string panelId, PanelPlacement placement, double size)
    {
        _settings.Editor.PanelLayouts = Shell.GetCurrentLayout();
        _settings.Editor.OpenRegions = Shell.GetOpenRegions();
        _settings.Save();
        SyncViewMenuChecks();
    }

    private void OpenFolderInExplorer()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (_settings.Editor.Explorer.OpenFolderPath is string prev && Directory.Exists(prev))
            dlg.SelectedPath = prev;

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        // Close workspace if one is open (mode exclusivity) — after dialog confirms
        if (_workspaceManager.CurrentWorkspace != null)
        {
            if (!PromptCloseUnsavedWorkspace()) return;
            CloseCurrentWorkspace();
        }

        SwitchToFolder(dlg.SelectedPath);
    }

    private void SwitchToFolder(string newFolderPath)
    {
        // Save current folder's tabs before switching
        var currentFolder = ExplorerPanel.OpenFolderPath;
        if (currentFolder != null)
        {
            SaveFolderSession(currentFolder);
            _settings.Editor.Explorer.ExpandedPaths = ExplorerPanel.GetExpandedPaths();
        }

        CloseAllTabs();

        ExplorerPanel.OpenFolder(newFolderPath);
        _settings.Editor.Explorer.OpenFolderPath = newFolderPath;
        Shell.ShowPanel("file-explorer");

        // Restore tabs for the new folder
        RestoreFolderTabs(newFolderPath);
        _settings.Save();
    }

    private void CloseFolderInExplorer()
    {
        var folderPath = ExplorerPanel.OpenFolderPath;
        if (folderPath != null)
            SaveFolderSession(folderPath);

        CloseAllTabs();
        ExplorerPanel.CloseFolder();
        Shell.HidePanel("file-explorer");
        _settings.Editor.Explorer.OpenFolderPath = null;
        _settings.Editor.Explorer.ExpandedPaths.Clear();
        _settings.Save();

        // Create a fresh empty tab
        var tab = CreateTab();
        ActivateTab(tab);
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

    private void OnExplorerFileRenamed(string oldPath, string newPath)
    {
        foreach (var tab in _tabs)
        {
            if (tab.FilePath == null) continue;
            if (string.Equals(tab.FilePath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                tab.FilePath = newPath;
                tab.Editor.SetGrammar(SyntaxManager.GetDefinition(Path.GetExtension(newPath)));
                UpdateTabHeader(tab);
                if (tab == _activeTab) UpdateTitle();
            }
            else if (tab.FilePath.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                // File was inside a renamed/moved directory
                tab.FilePath = Path.Combine(newPath, tab.FilePath[(oldPath.Length + 1)..]);
                UpdateTabHeader(tab);
                if (tab == _activeTab) UpdateTitle();
            }
        }
    }

    private void OnExplorerFileDeleted(string path)
    {
        var affectedTabs = _tabs.Where(t =>
            t.FilePath != null &&
            (string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase) ||
             t.FilePath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var tab in affectedTabs)
            ForceCloseTab(tab);
    }

    private void ForceCloseTab(TabInfo tab) => RemoveTab(tab);

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

        if (!File.Exists(fullPath))
        {
            ThemedMessageBox.Show(this, $"The file no longer exists:\n{fullPath}", "File Not Found");
            return null;
        }

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
        // Restore workspace if one was open
        if (_settings.LastOpenWorkspacePath is string wsPath && File.Exists(wsPath))
        {
            OpenWorkspaceFromPath(wsPath);
            return;
        }

        // Restore unsaved workspace if one existed
        if (_settings.UnsavedWorkspaceFolders is { Count: > 0 } unsavedFolders)
        {
            _workspaceManager.NewWorkspace("Untitled Workspace");
            foreach (var folder in unsavedFolders)
            {
                if (Directory.Exists(folder))
                    _workspaceManager.AddFolder(folder);
            }

            if (_workspaceManager.CurrentWorkspace!.Folders.Count > 0)
            {
                ExplorerPanel.OpenWorkspace(_workspaceManager.CurrentWorkspace);
                Shell.ShowPanel("file-explorer");
                UpdateWorkspaceMenuState(true);

                // Create a default tab (unsaved workspaces don't persist session to file)
                CreateTab();
                ActivateTab(_tabs[0]);
                return;
            }

            _workspaceManager.CloseWorkspace();
        }

        // If a folder was open, restore its per-folder tabs
        var folderPath = _settings.Editor.Explorer.OpenFolderPath;
        if (folderPath != null && Directory.Exists(folderPath))
        {
            RestoreFolderTabs(folderPath);
            return;
        }

        var restored = _sessionManager.RestoreSession(_settings.Session);

        if (restored.Tabs.Count > 0)
        {
            TabInfo? activeTab = null;

            for (int i = 0; i < restored.Tabs.Count; i++)
            {
                var rt = restored.Tabs[i];
                var tab = CreateTab();

                if (rt.FilePath != null)
                {
                    tab.FilePath = rt.FilePath;
                    tab.FileEncoding = FileHelper.DetectEncoding(rt.FilePath);

                    if (rt.IsDirty)
                    {
                        tab.Editor.SetContent(rt.SavedContent ?? FileHelper.ReadAllText(rt.FilePath, tab.FileEncoding));
                        tab.Editor.MarkDirty();
                    }
                    else
                    {
                        tab.Editor.SetContent(FileHelper.ReadAllText(rt.FilePath, tab.FileEncoding));
                    }

                    tab.LastKnownFileSize = new FileInfo(rt.FilePath).Length;
                    tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(rt.FilePath, tab.LastKnownFileSize);
                    tab.StartWatching();
                }
                else
                {
                    // Untitled tab — restore saved content if available
                    if (rt.SavedContent != null)
                    {
                        tab.Editor.SetContent(rt.SavedContent);
                        tab.Editor.MarkDirty();
                    }
                }

                UpdateTabHeader(tab);

                // Defer caret/scroll restore until after layout so the editor has valid extents
                var restoredTab = rt;
                RoutedEventHandler? onLoaded = null;
                onLoaded = (_, _) =>
                {
                    tab.Editor.Loaded -= onLoaded;
                    tab.Editor.SetCaretPosition(restoredTab.CaretLine, restoredTab.CaretCol);
                    tab.Editor.SetVerticalOffset(restoredTab.ScrollVertical);
                    tab.Editor.SetHorizontalOffset(restoredTab.ScrollHorizontal);
                    tab.Editor.InvalidateVisual();
                };
                tab.Editor.Loaded += onLoaded;

                if (i == restored.ActiveTabIndex)
                    activeTab = tab;
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
        var folderPath = ExplorerPanel.OpenFolderPath;
        if (folderPath != null)
        {
            SaveFolderSession(folderPath);
        }
        else
        {
            SessionSettings.ClearSessionDir();
            _settings.Session = _sessionManager.SaveSession(_tabs, _activeTab);
        }
    }

    private void SaveFolderSession(string folderPath)
    {
        SessionSettings.ClearFolderSessionDir(folderPath);
        _settings.FolderSessions[folderPath] = _sessionManager.SaveSession(_tabs, _activeTab, folderPath);
    }

    private void RestoreFolderTabs(string folderPath)
    {
        if (!_settings.FolderSessions.TryGetValue(folderPath, out var session) || session.Tabs.Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            return;
        }

        var restored = _sessionManager.RestoreSession(session, folderPath);
        if (restored.Tabs.Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            return;
        }

        TabInfo? activeTab = null;
        for (int i = 0; i < restored.Tabs.Count; i++)
        {
            var rt = restored.Tabs[i];
            var tab = CreateTab();

            if (rt.FilePath != null)
            {
                tab.FilePath = rt.FilePath;
                tab.FileEncoding = FileHelper.DetectEncoding(rt.FilePath);

                if (rt.IsDirty)
                {
                    tab.Editor.SetContent(rt.SavedContent ?? FileHelper.ReadAllText(rt.FilePath, tab.FileEncoding));
                    tab.Editor.MarkDirty();
                }
                else
                {
                    tab.Editor.SetContent(FileHelper.ReadAllText(rt.FilePath, tab.FileEncoding));
                }

                tab.LastKnownFileSize = new FileInfo(rt.FilePath).Length;
                tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(rt.FilePath, tab.LastKnownFileSize);
                tab.StartWatching();
            }
            else if (rt.SavedContent != null)
            {
                tab.Editor.SetContent(rt.SavedContent);
                tab.Editor.MarkDirty();
            }

            UpdateTabHeader(tab);

            var restoredTab = rt;
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                tab.Editor.Loaded -= onLoaded;
                tab.Editor.SetCaretPosition(restoredTab.CaretLine, restoredTab.CaretCol);
                tab.Editor.SetVerticalOffset(restoredTab.ScrollVertical);
                tab.Editor.SetHorizontalOffset(restoredTab.ScrollHorizontal);
                tab.Editor.InvalidateVisual();
            };
            tab.Editor.Loaded += onLoaded;

            if (i == restored.ActiveTabIndex)
                activeTab = tab;
        }

        ActivateTab(activeTab ?? _tabs[0]);
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
        // Save workspace session if a workspace is open
        if (_workspaceManager.CurrentWorkspace != null)
        {
            try
            {
                CaptureWorkspaceSession();
                if (_workspaceManager.CurrentWorkspace.FilePath != null)
                {
                    _workspaceManager.SaveWorkspace();
                }
                else
                {
                    // Persist unsaved workspace folders and session for restore on next launch
                    _settings.UnsavedWorkspaceFolders = [.. _workspaceManager.CurrentWorkspace.Folders];
                    _settings.LastOpenWorkspacePath = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Workspace save failed: {ex.Message}");
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
        _settings.Editor.Explorer.ExpandedPaths = ExplorerPanel.GetExpandedPaths();
        _settings.Editor.PanelLayouts = Shell.GetCurrentLayout();
        _settings.Editor.OpenRegions = Shell.GetOpenRegions();
        _settings.Save();

        foreach (var tab in _tabs)
            tab.StopWatching();

        ExplorerPanel.FlushAllStagedDeletes();
    }

    private void UpdateCaretPos()
    {
        if (Editor is not { } editor) return;
        CaretPosText.Text = $"Ln {editor.CaretLine + 1}, Col {editor.CaretCol + 1}";
        CharCountText.Text = $"{editor.CharCount:N0} {(editor.CharCount == 1 ? "Character" : "Characters")}";
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
        if (Editor is not { } editor) return;
        var ext = _activeTab!.FilePath != null ? Path.GetExtension(_activeTab.FilePath).ToLowerInvariant() : "";
        editor.SetGrammar(SyntaxManager.GetDefinition(ext));
        FileTypeText.Text = FileHelper.GetFileTypeName(ext);
        EncodingText.Text = GetEncodingLabel();
        LineEndingText.Text = editor.LineEnding;
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
        try
        {
            tab.LastKnownFileSize = new FileInfo(tab.FilePath!).Length;
            tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(tab.FilePath!, tab.LastKnownFileSize);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Post-save metadata refresh failed: {ex.Message}");
        }
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

    private void OnToggleLeftPanel(object sender, RoutedEventArgs e)
    {
        Shell.ToggleRegion(PanelPlacement.Left);
        SyncViewMenuChecks();
    }

    private void OnToggleRightPanel(object sender, RoutedEventArgs e)
    {
        Shell.ToggleRegion(PanelPlacement.Right);
        SyncViewMenuChecks();
    }

    private void OnToggleTopPanel(object sender, RoutedEventArgs e)
    {
        Shell.ToggleRegion(PanelPlacement.Top);
        SyncViewMenuChecks();
    }

    private void OnToggleBottomPanel(object sender, RoutedEventArgs e)
    {
        Shell.ToggleRegion(PanelPlacement.Bottom);
        SyncViewMenuChecks();
    }

    private void SyncViewMenuChecks()
    {
        MenuViewLeft.IsChecked = Shell.IsRegionVisible(PanelPlacement.Left);
        MenuViewRight.IsChecked = Shell.IsRegionVisible(PanelPlacement.Right);
        MenuViewTop.IsChecked = Shell.IsRegionVisible(PanelPlacement.Top);
        MenuViewBottom.IsChecked = Shell.IsRegionVisible(PanelPlacement.Bottom);
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (Editor is not { } editor) return;
        var snapshot = new SettingsSnapshot(
            editor.TabSize, _settings.Editor.Caret.BlockCaret, _settings.Editor.Caret.BlinkMs,
            editor.FontFamilyName, editor.EditorFontSize, editor.EditorFontWeight,
            editor.LineHeightMultiplier, _settings.Application.ColorTheme, _settings.Editor.Find.BarPosition);
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

        if (ctrl && !shift && e.Key == Key.N) { OnNewTab(this, new RoutedEventArgs()); e.Handled = true; }
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
        else if (ctrl && (Keyboard.Modifiers & ModifierKeys.Alt) != 0 && (e.Key == Key.B || e.SystemKey == Key.B)) { Shell.ToggleRegion(PanelPlacement.Right); SyncViewMenuChecks(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.B) { Shell.ToggleRegion(PanelPlacement.Left); SyncViewMenuChecks(); e.Handled = true; }
        else if (ctrl && (Keyboard.Modifiers & ModifierKeys.Alt) != 0 && (e.Key == Key.J || e.SystemKey == Key.J)) { Shell.ToggleRegion(PanelPlacement.Top); SyncViewMenuChecks(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.J) { Shell.ToggleRegion(PanelPlacement.Bottom); SyncViewMenuChecks(); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    private void StepFontSize(int direction)
    {
        if (Editor is not { } editor) return;
        var sizes = AppSettings.FontSizeOptions;
        int idx = Array.IndexOf(sizes, editor.EditorFontSize);
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
        if (Editor is not { } editor) return;
        var commands = CommandPaletteCommands.Build(new CommandPaletteContext(
            _tabs, _settings, ThemeManager, editor, FindBarControl, () => _settings.Save(),
            new ExplorerActions(ToggleExplorer, OpenFolderInExplorer, CloseFolderInExplorer),
            new WorkspaceActions(
                () => OnNewWorkspace(this, new RoutedEventArgs()),
                () => OnOpenWorkspace(this, new RoutedEventArgs()),
                CloseCurrentWorkspace,
                () => OnAddFolderToWorkspace(this, new RoutedEventArgs())),
            () => OnToggleWordWrap(this, new RoutedEventArgs())));
        CmdPalette.SetCommands(commands);
        CmdPalette.Open();
    }

    // ── Workspace menu handlers ─────────────────────────────────────────────

    private void OnNewWorkspace(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Workspace Files (*.volt-workspace)|*.volt-workspace",
            DefaultExt = ".volt-workspace",
            FileName = "MyWorkspace.volt-workspace"
        };
        if (dlg.ShowDialog() != true) return;

        if (_workspaceManager.CurrentWorkspace != null)
        {
            if (!PromptCloseUnsavedWorkspace()) return;
            CloseCurrentWorkspace();
        }
        else if (ExplorerPanel.OpenFolderPath != null)
            CloseFolderInExplorer();

        CloseAllTabs();

        _workspaceManager.NewWorkspace(Path.GetFileNameWithoutExtension(dlg.FileName));
        _workspaceManager.CurrentWorkspace!.FilePath = dlg.FileName;
        _workspaceManager.SaveWorkspace();

        ExplorerPanel.OpenWorkspace(_workspaceManager.CurrentWorkspace);
        Shell.ShowPanel("file-explorer");
        UpdateWorkspaceMenuState(true);

        _settings.LastOpenWorkspacePath = dlg.FileName;
        _settings.Save();

        CreateTab();
        ActivateTab(_tabs[0]);
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Workspace Files (*.volt-workspace)|*.volt-workspace",
            DefaultExt = ".volt-workspace"
        };
        if (dlg.ShowDialog() != true) return;

        OpenWorkspaceFromPath(dlg.FileName);
    }

    private void OnCloseWorkspace(object sender, RoutedEventArgs e)
    {
        if (!PromptCloseUnsavedWorkspace()) return;
        CloseCurrentWorkspace();
    }

    private void OnAddFolderToWorkspace(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        if (_workspaceManager.CurrentWorkspace == null)
        {
            // Auto-create unsaved workspace from current folder (if any) + new folder
            var currentFolder = ExplorerPanel.OpenFolderPath;

            // Save current folder session before switching
            if (currentFolder != null)
            {
                SaveFolderSession(currentFolder);
                _settings.Editor.Explorer.ExpandedPaths = ExplorerPanel.GetExpandedPaths();
            }

            _workspaceManager.NewWorkspace("Untitled Workspace");
            if (currentFolder != null)
                _workspaceManager.AddFolder(currentFolder);

            _settings.Editor.Explorer.OpenFolderPath = null;
            _settings.LastOpenWorkspacePath = null;
        }

        _workspaceManager.AddFolder(dlg.SelectedPath);

        // Auto-save if workspace has a file path
        if (_workspaceManager.CurrentWorkspace!.FilePath != null)
            _workspaceManager.SaveWorkspace();

        ExplorerPanel.OpenWorkspace(_workspaceManager.CurrentWorkspace);
        Shell.ShowPanel("file-explorer");
        UpdateWorkspaceMenuState(true);
        _settings.Save();

        if (_tabs.Count == 0)
        {
            CreateTab();
            ActivateTab(_tabs[0]);
        }
    }

    // ── Workspace helpers ────────────────────────────────────────────────────

    private void OpenWorkspaceFromPath(string workspacePath)
    {
        if (!File.Exists(workspacePath)) return;

        if (_workspaceManager.CurrentWorkspace != null)
        {
            if (!PromptCloseUnsavedWorkspace()) return;
            CloseCurrentWorkspace();
        }
        else if (ExplorerPanel.OpenFolderPath != null)
            CloseFolderInExplorer();

        if (!PromptSaveDirtyTabs()) return;
        CloseAllTabs();

        var workspace = _workspaceManager.OpenWorkspace(workspacePath);

        ExplorerPanel.OpenWorkspace(workspace);
        if (workspace.Session.ExpandedPaths.Count > 0)
            ExplorerPanel.RestoreExpandedPaths(workspace.Session.ExpandedPaths);
        Shell.ShowPanel("file-explorer");
        UpdateWorkspaceMenuState(true);

        _settings.LastOpenWorkspacePath = workspacePath;
        _settings.Save();

        RestoreWorkspaceSession(workspace);
    }

    private void CloseCurrentWorkspace()
    {
        if (_workspaceManager.CurrentWorkspace == null) return;

        CaptureWorkspaceSession();
        if (_workspaceManager.CurrentWorkspace.FilePath != null)
            _workspaceManager.SaveWorkspace();
        _workspaceManager.CloseWorkspace();

        ExplorerPanel.CloseWorkspace();
        UpdateWorkspaceMenuState(false);

        _settings.LastOpenWorkspacePath = null;
        _settings.UnsavedWorkspaceFolders = null;
        _settings.Save();

        CloseAllTabs();
        CreateTab();
        ActivateTab(_tabs[0]);
    }

    /// <summary>
    /// If the current workspace is unsaved, prompts the user to save or discard it.
    /// Returns false if the user cancelled (caller should abort the operation).
    /// </summary>
    private bool PromptCloseUnsavedWorkspace()
    {
        if (_workspaceManager.CurrentWorkspace == null) return true;
        if (_workspaceManager.CurrentWorkspace.FilePath != null) return true;

        var result = ThemedMessageBox.Show(this,
            "Do you want to save the current workspace before closing it?",
            "Unsaved Workspace", MessageBoxButton.YesNoCancel);

        if (result == MessageBoxResult.Cancel) return false;

        if (result == MessageBoxResult.Yes)
        {
            var saveDlg = new SaveFileDialog
            {
                Filter = "Workspace Files (*.volt-workspace)|*.volt-workspace",
                DefaultExt = ".volt-workspace",
                FileName = "MyWorkspace.volt-workspace"
            };
            if (saveDlg.ShowDialog() != true) return false;

            _workspaceManager.CurrentWorkspace.FilePath = saveDlg.FileName;
            _workspaceManager.CurrentWorkspace.Name = Path.GetFileNameWithoutExtension(saveDlg.FileName);
            CaptureWorkspaceSession();
            _workspaceManager.SaveWorkspace();
        }

        return true;
    }

    private void UpdateWorkspaceMenuState(bool workspaceOpen)
    {
        MenuCloseWorkspace.IsEnabled = workspaceOpen;
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

    private bool PromptSaveDirtyTabs()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (tab.Editor.IsDirty)
            {
                ActivateTab(tab);
                if (!PromptSaveTab(tab)) return false;
            }
        }
        return true;
    }

    // ── Workspace session capture / restore ──────────────────────────────────

    private void CaptureWorkspaceSession()
    {
        if (_workspaceManager.CurrentWorkspace == null) return;

        var sessionTabs = new List<WorkspaceSessionTab>();
        int activeIdx = 0;

        foreach (var t in _tabs)
        {
            if (t == _activeTab)
                activeIdx = sessionTabs.Count;

            sessionTabs.Add(new WorkspaceSessionTab
            {
                FilePath = t.FilePath,
                IsDirty = t.Editor.IsDirty,
                CaretLine = t.Editor.CaretLine,
                CaretCol = t.Editor.CaretCol,
                ScrollX = t.Editor.HorizontalOffset,
                ScrollY = t.Editor.VerticalOffset,
            });
        }

        _workspaceManager.CurrentWorkspace.Session = new WorkspaceSession
        {
            Tabs = sessionTabs,
            ActiveTabIndex = activeIdx,
            ExpandedPaths = ExplorerPanel.GetExpandedPaths()
        };
    }

    private void RestoreWorkspaceSession(Workspace workspace)
    {
        if (workspace.Session.Tabs.Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            return;
        }

        TabInfo? activeTab = null;
        for (int i = 0; i < workspace.Session.Tabs.Count; i++)
        {
            var st = workspace.Session.Tabs[i];
            if (st.FilePath != null && !File.Exists(st.FilePath))
                continue;

            var tab = CreateTab();

            if (st.FilePath != null)
            {
                tab.FilePath = st.FilePath;
                tab.FileEncoding = FileHelper.DetectEncoding(st.FilePath);
                tab.Editor.SetContent(FileHelper.ReadAllText(st.FilePath, tab.FileEncoding));
                tab.LastKnownFileSize = new FileInfo(st.FilePath).Length;
                tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(st.FilePath, tab.LastKnownFileSize);
                tab.StartWatching();
            }

            if (i == workspace.Session.ActiveTabIndex)
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
            for (int i = 0; i < _tabs.Count && i < workspace.Session.Tabs.Count; i++)
            {
                var st = workspace.Session.Tabs[i];
                var tab = _tabs[i];
                tab.Editor.SetCaretPosition(st.CaretLine, st.CaretCol);
                tab.ScrollHost?.ScrollToVerticalOffset(st.ScrollY);
                tab.ScrollHost?.ScrollToHorizontalOffset(st.ScrollX);
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Workspace context menu handlers ──────────────────────────────────────

    private void OnWorkspaceAddFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _workspaceManager.AddFolder(dlg.SelectedPath);
        if (_workspaceManager.CurrentWorkspace?.FilePath != null)
            _workspaceManager.SaveWorkspace();
        ExplorerPanel.RefreshWorkspaceTree();
    }

    private void OnWorkspaceRemoveFolder(string path)
    {
        _workspaceManager.RemoveFolder(path);
        if (_workspaceManager.CurrentWorkspace?.FilePath != null)
            _workspaceManager.SaveWorkspace();
        ExplorerPanel.RefreshWorkspaceTree();
    }
}
