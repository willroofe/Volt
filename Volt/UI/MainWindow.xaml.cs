using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Volt;

public partial class MainWindow
{
    private readonly List<TabInfo> _tabs = [];
    private TabInfo? _activeTab;
    private readonly AppSettings _settings;
    private readonly WorkspaceManager _workspaceManager = new();
    private readonly SessionManager _sessionManager = new();

    private readonly TabHeaderFactory _tabHeaderFactory = new();
    private readonly FileExplorerPanel _explorerPanel = new();
    private readonly KeyBindingManager _keyBindingManager = new();

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
        _tabHeaderFactory.FixedWidth = _settings.Editor.FixedWidthTabs;

        _tabHeaderFactory.TabActivated += tab => ActivateTab(tab);
        _tabHeaderFactory.TabClosed += tab => CloseTab(tab);
        _tabHeaderFactory.TabReordered += CommitTabReorder;

        // Create a placeholder tab only if there's no session to restore.
        // Full session restore is deferred to ContentRendered.
        if (!HasSessionToRestore())
        {
            var initialTab = CreateTab();
            ActivateTab(initialTab);
        }

        _keyBindingManager.Load(_settings.KeyBindings);
        ApplySettings();
        UpdateMenuGestureText();
        UpdateTabOverflowBrushes();
        RestoreWindowPosition();

        // Register explorer panel with shell
        Shell.RegisterPanel(_explorerPanel, PanelPlacement.Left, 250);
        RestorePanelLayout();
        SyncViewMenuChecks();

        if (_explorerPanel.OpenFolderPath == null &&
            _settings.Editor.Explorer.OpenFolderPath is string folderPath && Directory.Exists(folderPath))
        {
            _explorerPanel.OpenFolder(folderPath);
            if (_settings.Editor.Explorer.ExpandedPaths.Count > 0)
                _explorerPanel.RestoreExpandedPaths(_settings.Editor.Explorer.ExpandedPaths);
        }

        CmdPalette.Closed += (_, _) => { if (Editor is { } ed) Keyboard.Focus(ed); };
        FindBarControl.Closed += (_, _) => { if (Editor is { } ed) Keyboard.Focus(ed); };
        TabScrollViewer.ScrollChanged += (_, _) => UpdateTabOverflowIndicators();
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
        Activated += (_, _) => CheckAllTabsForExternalChanges();
        ThemeManager.ThemeChanged += (_, _) => { ApplyDwmTheme(); UpdateTabOverflowBrushes(); };
        _explorerPanel.FileOpenRequested += OnExplorerFileOpen;
        _explorerPanel.SetWorkspaceManager(_workspaceManager);
        _explorerPanel.AddFolderRequested += OnWorkspaceAddFolder;
        _explorerPanel.RemoveFolderRequested += OnWorkspaceRemoveFolder;
        _explorerPanel.CloseWorkspaceRequested += CloseCurrentWorkspace;
        _explorerPanel.FileRenamed += OnExplorerFileRenamed;
        _explorerPanel.FileDeleted += OnExplorerFileDeleted;
        Shell.PanelLayoutChanged += OnPanelLayoutChanged;
        SourceInitialized += (_, _) =>
        {
            ApplyDwmTheme();
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        };

        // Defer session restore until the window is painted on screen
        ContentRendered += OnFirstContentRendered;
    }

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        RestoreSession();

        // Silent background update check after startup settles
        _ = AppUpdateManager.CheckForUpdatesAsync(this);
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
        _explorerPanel.SelectFile(tab.FilePath);

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

    private const string SpinnerTag = "LoadingSpinner";

    private static void ShowTabSpinner(TabInfo tab)
    {
        if (tab.HeaderElement?.Child is not DockPanel panel) return;

        var glyph = new TextBlock
        {
            Text = "\uE117",  // Refresh/sync glyph
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
            Opacity = 0.7
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFgMuted);

        // Wrap in a fixed-size container with clipping to prevent rotation artifacts
        var container = new Border
        {
            Width = 14,
            Height = 14,
            ClipToBounds = true,
            Margin = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = glyph,
            Tag = SpinnerTag
        };

        DockPanel.SetDock(container, Dock.Right);
        // Insert after close button (index 0) but before the TextBlock (fill),
        // so it appears between the filename and the close button.
        panel.Children.Insert(1, container);

        var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        ((RotateTransform)glyph.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private static void HideTabSpinner(TabInfo tab)
    {
        if (tab.HeaderElement?.Child is not DockPanel panel) return;
        for (int i = panel.Children.Count - 1; i >= 0; i--)
        {
            if (panel.Children[i] is FrameworkElement { Tag: SpinnerTag })
            {
                panel.Children.RemoveAt(i);
                break;
            }
        }
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
        FindBarControl.SeedWithSelection = _settings.Editor.Find.SeedWithSelection;
        CmdPalette.SetPosition(_settings.Application.CommandPalettePosition);
        MenuWordWrap.IsChecked = _settings.Editor.WordWrap;
        _tabHeaderFactory.FixedWidth = _settings.Editor.FixedWidthTabs;
        foreach (var tab in _tabs)
            _tabHeaderFactory.ApplyFixedWidth(tab.HeaderElement);
    }

    private void ApplySettingsToEditor(EditorControl editor)
    {
        editor.TabSize = _settings.Editor.TabSize;
        editor.WordWrapAtWords = _settings.Editor.WordWrapAtWords;
        editor.WordWrapIndent = _settings.Editor.WordWrapIndent;
        editor.WordWrap = _settings.Editor.WordWrap;
        editor.IndentGuides = _settings.Editor.IndentGuides;
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
        _settings.ScheduleSave();
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

        _settings.AddRecentItem(dlg.SelectedPath, RecentItemKind.Folder);
        SwitchToFolder(dlg.SelectedPath);
    }

    private void SwitchToFolder(string newFolderPath)
    {
        // Save current folder's tabs before switching
        var currentFolder = _explorerPanel.OpenFolderPath;
        if (currentFolder != null)
        {
            SaveFolderSession(currentFolder);
            _settings.Editor.Explorer.ExpandedPaths = _explorerPanel.GetExpandedPaths();
        }

        CloseAllTabs();

        _explorerPanel.OpenFolder(newFolderPath);
        _settings.Editor.Explorer.OpenFolderPath = newFolderPath;
        Shell.ShowPanel("file-explorer");

        // Restore tabs for the new folder
        RestoreFolderTabs(newFolderPath);
        _settings.Save();
    }

    private void CloseFolderInExplorer()
    {
        var folderPath = _explorerPanel.OpenFolderPath;
        if (folderPath != null)
            SaveFolderSession(folderPath);

        CloseAllTabs();
        _explorerPanel.CloseFolder();
        Shell.HidePanel("file-explorer");
        _settings.Editor.Explorer.OpenFolderPath = null;
        _settings.Editor.Explorer.ExpandedPaths.Clear();
        _settings.Save();

        // Create a fresh empty tab
        var tab = CreateTab();
        ActivateTab(tab);
    }

    private async void OnExplorerFileOpen(string path)
    {
        var tab = await OpenFileInTabAsync(path, reuseUntitled: true, activate: true);
        if (tab != null)
            FindBarControl.RefreshSearch();
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

    /// <summary>Checks whether any session data exists that RestoreSession will act on.</summary>
    private bool HasSessionToRestore()
    {
        if (_settings.LastOpenWorkspacePath is string wsPath && File.Exists(wsPath))
            return true;
        if (_settings.UnsavedWorkspaceFolders is { Count: > 0 })
            return true;
        var folderPath = _settings.Editor.Explorer.OpenFolderPath;
        if (folderPath != null && Directory.Exists(folderPath))
            return true;
        if (_settings.Session.Tabs.Count > 0)
            return true;
        return false;
    }

    /// <summary>
    /// Opens a file in a tab asynchronously — file I/O and content parsing run on a
    /// background thread so the UI stays responsive for large files.
    /// </summary>
    private async Task<TabInfo?> OpenFileInTabAsync(string path, bool reuseUntitled, bool activate = false)
    {
        var fullPath = Path.GetFullPath(path);

        // Switch to existing tab if already open
        var existing = _tabs.FirstOrDefault(t =>
            t.FilePath != null && string.Equals(Path.GetFullPath(t.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (activate) ActivateTab(existing);
            return existing;
        }

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
        tab.IsLoading = true;
        UpdateTabHeader(tab);
        ShowTabSpinner(tab);
        if (activate) ActivateTab(tab);

        // Offload file I/O + content parsing to a background thread
        int tabSize = tab.Editor.TabSize;
        var (encoding, prepared, fileSize, tailBytes) = await Task.Run(() =>
        {
            var enc = FileHelper.DetectEncoding(path);
            var text = FileHelper.ReadAllText(path, enc);
            var prep = TextBuffer.PrepareContent(text, tabSize);
            var size = new FileInfo(path).Length;
            var tail = FileHelper.ReadTailVerifyBytes(path, size);
            return (enc, prep, size, tail);
        });

        tab.IsLoading = false;
        HideTabSpinner(tab);

        // Tab may have been closed while we were loading
        if (!_tabs.Contains(tab)) return null;

        tab.FileEncoding = encoding;
        tab.Editor.SetPreparedContent(prepared);
        tab.LastKnownFileSize = fileSize;
        tab.TailVerifyBytes = tailBytes;
        tab.StartWatching();
        UpdateTabHeader(tab);
        return tab;
    }

    /// <summary>
    /// Opens a file in a tab synchronously. Used by session restore where async
    /// isn't needed (files load before the window is shown).
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
                _explorerPanel.OpenWorkspace(_workspaceManager.CurrentWorkspace);
                if (_settings.UnsavedWorkspaceSession is { } ws)
                {
                    if (ws.ExpandedPaths.Count > 0)
                        _explorerPanel.RestoreExpandedPaths(ws.ExpandedPaths);
                    _workspaceManager.CurrentWorkspace.Session = ws;
                }
                Shell.ShowPanel("file-explorer");
                UpdateWorkspaceMenuState(true);

                if (_settings.UnsavedWorkspaceSession is { Tabs.Count: > 0 })
                {
                    RestoreWorkspaceSession(_workspaceManager.CurrentWorkspace);
                }
                else
                {
                    CreateTab();
                    ActivateTab(_tabs[0]);
                }
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
        RestoreTabsFromSession(restored);

        // Clean up session files after restore
        SessionSettings.ClearSessionDir();
    }

    private void SaveSession()
    {
        var folderPath = _explorerPanel.OpenFolderPath;
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
        RestoreTabsFromSession(restored);
    }

    private void RestoreTabsFromSession(RestoredSession restored)
    {
        if (restored.Tabs.Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            return;
        }

        TabInfo? activeTab = null;
        var asyncLoads = new List<(TabInfo tab, RestoredTab rt)>();

        for (int i = 0; i < restored.Tabs.Count; i++)
        {
            var rt = restored.Tabs[i];
            var tab = CreateTab();

            if (rt.FilePath != null)
            {
                tab.FilePath = rt.FilePath;

                if (rt.IsDirty && rt.SavedContent != null)
                {
                    // Dirty tab with saved content — load inline (it's already in memory)
                    tab.Editor.SetContent(rt.SavedContent);
                    tab.Editor.MarkDirty();
                    tab.FileEncoding = FileHelper.DetectEncoding(rt.FilePath);
                    tab.LastKnownFileSize = new FileInfo(rt.FilePath).Length;
                    tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(rt.FilePath, tab.LastKnownFileSize);
                    tab.StartWatching();
                }
                else
                {
                    // File needs reading from disk — defer to async
                    tab.IsLoading = true;
                    ShowTabSpinner(tab);
                    asyncLoads.Add((tab, rt));
                }
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
                if (!tab.IsLoading)
                {
                    tab.Editor.SetCaretPosition(restoredTab.CaretLine, restoredTab.CaretCol);
                    tab.Editor.SetVerticalOffset(restoredTab.ScrollVertical);
                    tab.Editor.SetHorizontalOffset(restoredTab.ScrollHorizontal);
                    tab.Editor.InvalidateVisual();
                }
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

        // Kick off async file loads (window is already visible at this point)
        foreach (var (t, r) in asyncLoads)
            _ = LoadTabContentAsync(t, r);
    }

    private async Task LoadTabContentAsync(TabInfo tab, RestoredTab rt)
    {
        var path = rt.FilePath!;
        int tabSize = tab.Editor.TabSize;
        bool isDirty = rt.IsDirty;

        var (encoding, prepared, fileSize, tailBytes) = await Task.Run(() =>
        {
            var enc = FileHelper.DetectEncoding(path);
            var text = FileHelper.ReadAllText(path, enc);
            var prep = TextBuffer.PrepareContent(text, tabSize);
            var size = new FileInfo(path).Length;
            var tail = FileHelper.ReadTailVerifyBytes(path, size);
            return (enc, prep, size, tail);
        });

        if (!_tabs.Contains(tab)) return;

        tab.IsLoading = false;
        HideTabSpinner(tab);
        tab.FileEncoding = encoding;
        tab.Editor.SetPreparedContent(prepared);
        if (isDirty) tab.Editor.MarkDirty();
        tab.LastKnownFileSize = fileSize;
        tab.TailVerifyBytes = tailBytes;
        tab.StartWatching();
        UpdateTabHeader(tab);

        // Restore caret/scroll position now that content is loaded
        tab.Editor.SetCaretPosition(rt.CaretLine, rt.CaretCol);
        tab.Editor.SetVerticalOffset(rt.ScrollVertical);
        tab.Editor.SetHorizontalOffset(rt.ScrollHorizontal);
        tab.Editor.InvalidateVisual();
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
                    _settings.UnsavedWorkspaceSession = _workspaceManager.CurrentWorkspace.Session;
                    _settings.LastOpenWorkspacePath = null;
                }
            }
            catch (Exception) { }
        }

        // Try to persist session; if it fails, fall back to prompting for dirty tabs
        bool sessionSaved = false;
        try
        {
            SaveSession();
            sessionSaved = true;
        }
        catch (Exception) { }

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
        _settings.Editor.Explorer.ExpandedPaths = _explorerPanel.GetExpandedPaths();
        _settings.Editor.PanelLayouts = Shell.GetCurrentLayout();
        _settings.Editor.OpenRegions = Shell.GetOpenRegions();
        _settings.Save();

        foreach (var tab in _tabs)
            tab.StopWatching();

        _explorerPanel.FlushAllStagedDeletes();
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
        catch (Exception) { }
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

    private async void OnOpen(object sender, RoutedEventArgs e)
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
            bool isFirst = lastTab == null;
            var tab = await OpenFileInTabAsync(fileName, reuseUntitled: isFirst, activate: isFirst);
            if (tab != null)
            {
                _settings.AddRecentItem(fileName, RecentItemKind.File);
                lastTab = tab;
            }
        }

        if (lastTab != null)
        {
            _settings.Save();
            FindBarControl.RefreshSearch();
        }
    }

    private const int RecentItemsPreviewCount = 10;

    private void OnOpenRecentSubmenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateRecentMenu((MenuItem)sender, showAll: false);
    }

    private void PopulateRecentMenu(MenuItem menu, bool showAll)
    {
        menu.Items.Clear();

        var dropdownStyle = (Style)FindResource("MenuItemDropdownStyle");
        var recentItems = _settings.Application.RecentItems;
        if (recentItems.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false, Style = dropdownStyle });
            return;
        }

        var visibleItems = showAll ? recentItems : recentItems.Take(RecentItemsPreviewCount);
        foreach (var recent in visibleItems)
        {
            var kind = recent.Kind;
            var path = recent.Path;
            var header = kind switch
            {
                RecentItemKind.Folder => Path.GetFileName(path) + " - " + Path.GetDirectoryName(path),
                RecentItemKind.Workspace => Path.GetFileNameWithoutExtension(path) + " - " + Path.GetDirectoryName(path),
                _ => Path.GetFileName(path) + " - " + Path.GetDirectoryName(path)
            };
            var iconGlyph = kind switch
            {
                RecentItemKind.Folder => "\uE838",
                RecentItemKind.Workspace => "\uE821",
                _ => "\uE8A5"
            };
            var item = new MenuItem { Header = header, Style = dropdownStyle };
            item.Icon = new System.Windows.Controls.TextBlock
            {
                Text = iconGlyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            var capturedPath = path;
            var capturedKind = kind;
            item.Click += (_, _) => OpenRecentItem(capturedPath, capturedKind);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        if (!showAll && recentItems.Count > RecentItemsPreviewCount)
        {
            var viewMore = new MenuItem { Header = "View More...", Style = dropdownStyle };
            viewMore.Click += (_, _) => OpenRecentInCommandPalette();
            menu.Items.Add(viewMore);
        }

        var clearItem = new MenuItem { Header = "Clear Recent", Style = dropdownStyle };
        clearItem.Click += (_, _) =>
        {
            _settings.Application.RecentItems.Clear();
            _settings.Save();
        };
        menu.Items.Add(clearItem);
    }

    private void OpenRecentInCommandPalette()
    {
        var options = _settings.Application.RecentItems.Select(recent =>
        {
            var kind = recent.Kind;
            var path = recent.Path;
            var label = kind switch
            {
                RecentItemKind.Folder => Path.GetFileName(path) + " (Folder) - " + Path.GetDirectoryName(path),
                RecentItemKind.Workspace => Path.GetFileNameWithoutExtension(path) + " (Workspace) - " + Path.GetDirectoryName(path),
                _ => Path.GetFileName(path) + " - " + Path.GetDirectoryName(path)
            };
            return new PaletteOption(label,
                ApplyPreview: () => { },
                Commit: () => OpenRecentItem(path, kind),
                Revert: () => { });
        }).ToList();

        Dispatcher.BeginInvoke(() => CmdPalette.OpenWithOptions("Recent: ", options));
    }

    private async void OpenRecentItem(string path, RecentItemKind kind)
    {
        switch (kind)
        {
            case RecentItemKind.File:
                if (!File.Exists(path))
                {
                    ThemedMessageBox.Show(this, $"The file no longer exists:\n{path}", "File Not Found");
                    _settings.Application.RecentItems.RemoveAll(r =>
                        string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase) && r.Kind == kind);
                    _settings.Save();
                    return;
                }
                var tab = await OpenFileInTabAsync(path, reuseUntitled: true, activate: true);
                if (tab != null)
                    FindBarControl.RefreshSearch();
                break;

            case RecentItemKind.Folder:
                if (!Directory.Exists(path))
                {
                    ThemedMessageBox.Show(this, $"The folder no longer exists:\n{path}", "Folder Not Found");
                    _settings.Application.RecentItems.RemoveAll(r =>
                        string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase) && r.Kind == kind);
                    _settings.Save();
                    return;
                }
                if (_workspaceManager.CurrentWorkspace != null)
                {
                    if (!PromptCloseUnsavedWorkspace()) return;
                    CloseCurrentWorkspace();
                }
                SwitchToFolder(path);
                break;

            case RecentItemKind.Workspace:
                OpenWorkspaceFromPath(path);
                break;
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

    private void ToggleFixedWidthTabs()
    {
        _settings.Editor.FixedWidthTabs = !_settings.Editor.FixedWidthTabs;
        _tabHeaderFactory.FixedWidth = _settings.Editor.FixedWidthTabs;
        foreach (var tab in _tabs)
            _tabHeaderFactory.ApplyFixedWidth(tab.HeaderElement);
        _settings.Save();
    }

    private void ToggleWordWrapAtWords()
    {
        _settings.Editor.WordWrapAtWords = !_settings.Editor.WordWrapAtWords;
        foreach (var tab in _tabs)
            tab.Editor.WordWrapAtWords = _settings.Editor.WordWrapAtWords;
        _settings.Save();
    }

    private void ToggleWordWrapIndent()
    {
        _settings.Editor.WordWrapIndent = !_settings.Editor.WordWrapIndent;
        foreach (var tab in _tabs)
            tab.Editor.WordWrapIndent = _settings.Editor.WordWrapIndent;
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
            editor.LineHeightMultiplier, _settings.Application.ColorTheme, _settings.Editor.Find.BarPosition,
            _settings.Editor.Find.SeedWithSelection, _settings.Editor.FixedWidthTabs,
            _settings.Editor.WordWrap, _settings.Editor.WordWrapAtWords, _settings.Editor.WordWrapIndent,
            _settings.Editor.IndentGuides, _settings.Application.CommandPalettePosition,
            _keyBindingManager.GetAllBindings());
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
        _settings.Application.CommandPalettePosition = dlg.CommandPalettePosition;
        _settings.Editor.Find.BarPosition = dlg.FindBarPosition;
        _settings.Editor.Find.SeedWithSelection = dlg.FindSeedWithSelection;
        _settings.Editor.FixedWidthTabs = dlg.FixedWidthTabs;
        _settings.Editor.WordWrap = dlg.WordWrap;
        _settings.Editor.WordWrapAtWords = dlg.WordWrapAtWords;
        _settings.Editor.WordWrapIndent = dlg.WordWrapIndent;
        _settings.Editor.IndentGuides = dlg.IndentGuides;
        _keyBindingManager.SetAll(dlg.KeyBindings);
        _settings.KeyBindings = _keyBindingManager.GetSaveState();
        _settings.Save();
        UpdateMenuGestureText();
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
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (_keyBindingManager.TryGetCommand(key, mods, out var cmd) &&
            KeyBindingManager.IsPreviewBinding(cmd))
        {
            ExecuteCommand(cmd);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (_keyBindingManager.TryGetCommand(key, mods, out var cmd) &&
            !KeyBindingManager.IsPreviewBinding(cmd))
        {
            ExecuteCommand(cmd);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void ExecuteCommand(VoltCommand command)
    {
        switch (command)
        {
            case VoltCommand.NewTab: OnNewTab(this, new RoutedEventArgs()); break;
            case VoltCommand.OpenFile: OnOpen(this, new RoutedEventArgs()); break;
            case VoltCommand.Save: OnSave(this, new RoutedEventArgs()); break;
            case VoltCommand.SaveAs: OnSaveAs(this, new RoutedEventArgs()); break;
            case VoltCommand.CloseTab: if (_activeTab != null) CloseTab(_activeTab); break;
            case VoltCommand.OpenFind: FindBarControl.Open(); break;
            case VoltCommand.ToggleReplace: FindBarControl.ToggleReplace(); break;
            case VoltCommand.CommandPalette: OpenCommandPalette(); break;
            case VoltCommand.OpenFolder: OpenFolderInExplorer(); break;
            case VoltCommand.Settings: OnSettings(this, new RoutedEventArgs()); break;
            case VoltCommand.ZoomIn: StepFontSize(1); break;
            case VoltCommand.ZoomOut: StepFontSize(-1); break;
            case VoltCommand.ToggleLeftPanel: Shell.ToggleRegion(PanelPlacement.Left); SyncViewMenuChecks(); break;
            case VoltCommand.ToggleRightPanel: Shell.ToggleRegion(PanelPlacement.Right); SyncViewMenuChecks(); break;
            case VoltCommand.ToggleTopPanel: Shell.ToggleRegion(PanelPlacement.Top); SyncViewMenuChecks(); break;
            case VoltCommand.ToggleBottomPanel: Shell.ToggleRegion(PanelPlacement.Bottom); SyncViewMenuChecks(); break;
            case VoltCommand.SwitchTabForward: SwitchTab(+1); break;
            case VoltCommand.SwitchTabBackward: SwitchTab(-1); break;
            case VoltCommand.FoldBlock: _activeTab?.Editor.FoldAtCaret(); break;
            case VoltCommand.UnfoldBlock: _activeTab?.Editor.UnfoldAtCaret(); break;
        }
    }

    private void UpdateMenuGestureText()
    {
        MenuSettings.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.Settings);
        MenuSave.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.Save);
        MenuSaveAs.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.SaveAs);
        MenuOpenFile.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.OpenFile);
        MenuOpenFolder.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.OpenFolder);
        MenuViewLeft.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ToggleLeftPanel);
        MenuViewRight.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ToggleRightPanel);
        MenuViewTop.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ToggleTopPanel);
        MenuViewBottom.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ToggleBottomPanel);

        var newTabGesture = _keyBindingManager.GetGestureText(VoltCommand.NewTab);
        NewTabButton.ToolTip = string.IsNullOrEmpty(newTabGesture) ? "New Tab" : $"New Tab ({newTabGesture})";
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
            _tabs, _settings, ThemeManager, editor, FindBarControl, CmdPalette, () => _settings.Save(),
            new ExplorerActions(ToggleExplorer, OpenFolderInExplorer, CloseFolderInExplorer),
            new WorkspaceActions(
                () => OnOpenWorkspace(this, new RoutedEventArgs()),
                CloseCurrentWorkspace,
                () => OnAddFolderToWorkspace(this, new RoutedEventArgs()),
                () => OnSaveWorkspaceAs(this, new RoutedEventArgs())),
            () => OnToggleWordWrap(this, new RoutedEventArgs()),
            ToggleWordWrapAtWords,
            ToggleWordWrapIndent,
            ToggleFixedWidthTabs,
            () => _ = AppUpdateManager.CheckForUpdatesAsync(this, showUpToDate: true),
            OpenRecentInCommandPalette));
        CmdPalette.SetCommands(commands);
        CmdPalette.Open();
    }

    // ── Workspace menu handlers ─────────────────────────────────────────────

    private void OnSaveWorkspaceAs(object sender, RoutedEventArgs e)
    {
        if (_workspaceManager.CurrentWorkspace == null) return;

        var dlg = new SaveFileDialog
        {
            Filter = "Workspace Files (*.volt-workspace)|*.volt-workspace",
            DefaultExt = ".volt-workspace",
            FileName = (_workspaceManager.CurrentWorkspace.Name ?? "MyWorkspace") + ".volt-workspace"
        };
        if (dlg.ShowDialog() != true) return;

        _workspaceManager.CurrentWorkspace.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
        _workspaceManager.CurrentWorkspace.FilePath = dlg.FileName;
        CaptureWorkspaceSession();
        _workspaceManager.SaveWorkspace();

        _settings.LastOpenWorkspacePath = dlg.FileName;
        _settings.UnsavedWorkspaceFolders = null;
        _settings.UnsavedWorkspaceSession = null;
        _settings.Save();
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
            var currentFolder = _explorerPanel.OpenFolderPath;

            // Save current folder session before switching
            if (currentFolder != null)
            {
                SaveFolderSession(currentFolder);
                _settings.Editor.Explorer.ExpandedPaths = _explorerPanel.GetExpandedPaths();
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

        _explorerPanel.OpenWorkspace(_workspaceManager.CurrentWorkspace);
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
        else if (_explorerPanel.OpenFolderPath != null)
            CloseFolderInExplorer();

        if (!PromptSaveDirtyTabs()) return;
        CloseAllTabs();

        var workspace = _workspaceManager.OpenWorkspace(workspacePath);

        _explorerPanel.OpenWorkspace(workspace);
        if (workspace.Session.ExpandedPaths.Count > 0)
            _explorerPanel.RestoreExpandedPaths(workspace.Session.ExpandedPaths);
        Shell.ShowPanel("file-explorer");
        UpdateWorkspaceMenuState(true);

        _settings.AddRecentItem(workspacePath, RecentItemKind.Workspace);
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

        _explorerPanel.CloseWorkspace();
        UpdateWorkspaceMenuState(false);

        _settings.LastOpenWorkspacePath = null;
        _settings.UnsavedWorkspaceFolders = null;
        _settings.UnsavedWorkspaceSession = null;
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
        MenuCloseWorkspace.Visibility = workspaceOpen ? Visibility.Visible : Visibility.Collapsed;
        MenuSaveWorkspaceAs.IsEnabled = workspaceOpen;
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
            ExpandedPaths = _explorerPanel.GetExpandedPaths()
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
        var asyncLoads = new List<(TabInfo tab, WorkspaceSessionTab st)>();

        for (int i = 0; i < workspace.Session.Tabs.Count; i++)
        {
            var st = workspace.Session.Tabs[i];
            if (st.FilePath != null && !File.Exists(st.FilePath))
                continue;

            var tab = CreateTab();

            if (st.FilePath != null)
            {
                tab.FilePath = st.FilePath;
                tab.IsLoading = true;
                ShowTabSpinner(tab);
                asyncLoads.Add((tab, st));
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

        foreach (var (t, s) in asyncLoads)
            _ = LoadWorkspaceTabAsync(t, s);
    }

    private async Task LoadWorkspaceTabAsync(TabInfo tab, WorkspaceSessionTab st)
    {
        var path = st.FilePath!;
        int tabSize = tab.Editor.TabSize;

        var (encoding, prepared, fileSize, tailBytes) = await Task.Run(() =>
        {
            var enc = FileHelper.DetectEncoding(path);
            var text = FileHelper.ReadAllText(path, enc);
            var prep = TextBuffer.PrepareContent(text, tabSize);
            var size = new FileInfo(path).Length;
            var tail = FileHelper.ReadTailVerifyBytes(path, size);
            return (enc, prep, size, tail);
        });

        if (!_tabs.Contains(tab)) return;

        tab.IsLoading = false;
        HideTabSpinner(tab);
        tab.FileEncoding = encoding;
        tab.Editor.SetPreparedContent(prepared);
        tab.LastKnownFileSize = fileSize;
        tab.TailVerifyBytes = tailBytes;
        tab.StartWatching();
        UpdateTabHeader(tab);

        tab.Editor.SetCaretPosition(st.CaretLine, st.CaretCol);
        tab.ScrollHost?.ScrollToVerticalOffset(st.ScrollY);
        tab.ScrollHost?.ScrollToHorizontalOffset(st.ScrollX);
    }

    // ── Workspace context menu handlers ──────────────────────────────────────

    private void OnWorkspaceAddFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _workspaceManager.AddFolder(dlg.SelectedPath);
        if (_workspaceManager.CurrentWorkspace?.FilePath != null)
            _workspaceManager.SaveWorkspace();
        _explorerPanel.RefreshWorkspaceTree();
    }

    private void OnWorkspaceRemoveFolder(string path)
    {
        _workspaceManager.RemoveFolder(path);
        if (_workspaceManager.CurrentWorkspace?.FilePath != null)
            _workspaceManager.SaveWorkspace();
        _explorerPanel.RefreshWorkspaceTree();
    }
}
