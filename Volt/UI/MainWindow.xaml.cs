using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private EditorLayoutNode _editorLayoutRoot = null!;
    private string _focusedLeafId = "";
    private EditorLayoutBuildResult? _layoutBuild;
    private readonly StackPanel _headerScratchStrip = new()
    {
        IsHitTestVisible = false,
        Width = 0,
        Height = 0,
        Visibility = Visibility.Collapsed
    };
    private readonly Border _headerScratchDrop = new() { Width = 2, Visibility = Visibility.Collapsed };
    /// <summary>Most recently closed file paths (LIFO). Cleared when opening a different folder or workspace.</summary>
    private readonly List<string> _closedTabPaths = [];
    private TabInfo? _activeTab;
    private readonly AppSettings _settings;
    private readonly WorkspaceManager _workspaceManager = new();
    private readonly SessionManager _sessionManager = new();

    private readonly TabHeaderFactory _tabHeaderFactory = new();
    private readonly FileExplorerPanel _explorerPanel = new();
    private readonly TerminalPanel _terminalPanel = new();
    private readonly KeyBindingManager _keyBindingManager = new();

    internal TerminalPanel TerminalPanel => _terminalPanel;
    internal string? _startupFilePath;

    internal string? ActiveFilePath => _activeTab?.FilePath;
    internal string? OpenFolderPath => _explorerPanel.OpenFolderPath;
    internal Workspace? ActiveWorkspace => _workspaceManager.CurrentWorkspace;

    internal EditorControl? Editor => _activeTab?.Editor;

    private record FileLoadResult(Encoding Encoding, TextBuffer.PreparedContent Prepared, long FileSize, byte[]? TailBytes);

    private static Task<FileLoadResult> LoadFileDataAsync(string path, int tabSize) => Task.Run(() =>
    {
        var enc = FileHelper.DetectEncoding(path);
        var text = FileHelper.ReadAllText(path, enc);
        var prep = TextBuffer.PrepareContent(text, tabSize);
        var size = new FileInfo(path).Length;
        var tail = FileHelper.ReadTailVerifyBytes(path, size);
        return new FileLoadResult(enc, prep, size, tail);
    });

    private void ApplyFileLoadResult(TabInfo tab, FileLoadResult result)
    {
        tab.IsLoading = false;
        HideTabSpinner(tab);
        tab.FileEncoding = result.Encoding;
        tab.Editor.SetPreparedContent(result.Prepared);
        tab.LastKnownFileSize = result.FileSize;
        tab.TailVerifyBytes = result.TailBytes;
        tab.StartWatching();
        UpdateTabHeader(tab);
    }

    private ThemeManager ThemeManager => App.Current.ThemeManager;
    private SyntaxManager SyntaxManager => App.Current.SyntaxManager;

    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WM_NCHITTEST = 0x0084;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
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

        _editorLayoutRoot = new EditorLeafNode();
        _focusedLeafId = ((EditorLeafNode)_editorLayoutRoot).Id;
        RebuildEditorLayoutUi();

        _settings = App.Current.Settings;
        _tabHeaderFactory.FixedWidth = _settings.Editor.FixedWidthTabs;

        _tabHeaderFactory.TabActivated += tab => ActivateTab(tab);
        _tabHeaderFactory.TabClosed += tab => CloseTab(tab);
        _tabHeaderFactory.TabReordered += CommitTabReorder;
        _tabHeaderFactory.TabMovedToOtherLeaf += CommitTabMoveToLeaf;
        _tabHeaderFactory.TabEditorSplitDrop += CommitTabEditorSplitDrop;
        _tabHeaderFactory.CanTabEditorSplitOnLeaf = CanTabEditorSplitOnLeaf;
        _tabHeaderFactory.ResolveEditorTabStrip = ResolveEditorTabStripForTab;

        _tabHeaderFactory.TabContextCanSplitGroup = _ => true;
        _tabHeaderFactory.TabContextCanJoinSibling = TabContextCanJoinSiblingFromMenu;
        _tabHeaderFactory.TabContextCanJoinAll = TabContextCanJoinAllFromMenu;
        _tabHeaderFactory.TabContextCanToggleOrientation = TabContextCanToggleOrientationFromMenu;
        _tabHeaderFactory.TabContextSplitGroup += tab =>
        {
            ActivateTab(tab);
            EnterEditorSplit();
        };
        _tabHeaderFactory.TabContextJoinSibling += tab =>
        {
            ActivateTab(tab);
            JoinEditorWithSibling();
        };
        _tabHeaderFactory.TabContextJoinAll += tab =>
        {
            ActivateTab(tab);
            JoinEditorFlattenAll();
        };
        _tabHeaderFactory.TabContextToggleOrientation += tab =>
        {
            ActivateTab(tab);
            ToggleParentSplitOrientation();
        };

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
        UpdateEditorSplitMenuState();
        UpdateTabOverflowBrushes();
        RestoreWindowPosition();

        // Register explorer panel with shell
        Shell.RegisterPanel(_explorerPanel, PanelPlacement.Left, 250);
        Shell.RegisterPanel(_terminalPanel, PanelPlacement.Bottom, 240);
        RestorePanelLayout();
        SyncViewMenuChecks();

        if (_explorerPanel.OpenFolderPath == null &&
            _settings.Editor.Explorer.OpenFolderPath is string folderPath && Directory.Exists(folderPath))
        {
            _explorerPanel.OpenFolder(folderPath);
            RestoreFolderExpandedPaths(folderPath);
            MenuCloseFolder.Visibility = Visibility.Visible;
            UpdateSaveWorkspaceMenuState();
        }

        CmdPalette.Closed += (_, _) => { if (Editor is { } ed) Keyboard.Focus(ed); };
        FindBarControl.Closed += (_, _) => { if (Editor is { } ed) Keyboard.Focus(ed); };
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
        Activated += (_, _) => CheckAllTabsForExternalChanges();
        ThemeManager.ThemeChanged += (_, _) =>
        {
            ApplyDwmTheme();
            UpdateTabOverflowBrushes();
            UpdateAllTabHeaders();
        };
        _explorerPanel.FileOpenRequested += OnExplorerFileOpen;
        _explorerPanel.SetWorkspaceManager(_workspaceManager);
        _explorerPanel.AddFolderRequested += OnWorkspaceAddFolder;
        _explorerPanel.RemoveFolderRequested += OnWorkspaceRemoveFolder;
        _explorerPanel.CloseWorkspaceRequested += CloseCurrentWorkspace;
        _explorerPanel.CloseFolderRequested += CloseFolderInExplorer;
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

        // If a file was passed on the command line (e.g. "Open with"), open it now
        if (_startupFilePath != null)
            _ = OpenFileInTabAsync(_startupFilePath, reuseUntitled: true, activate: true);

        // Silent background update check after startup settles
        _ = AppUpdateManager.CheckForUpdatesAsync(this);
    }

    internal void OpenFileFromIpc(string path)
    {
        if (!File.Exists(path)) return;
        _ = OpenFileInTabAsync(path, reuseUntitled: false, activate: true);
        BringToForeground();
    }

    private void BringToForeground()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        // Windows blocks background processes from stealing focus.
        // Briefly setting Topmost is the most reliable workaround.
        Topmost = true;
        Topmost = false;
        Activate();
        Focus();
    }

    private static void SyncStripChildrenToTabList(Panel? strip, List<TabInfo> tabs)
    {
        if (strip == null) return;
        strip.Children.Clear();
        foreach (var t in tabs)
        {
            var header = t.HeaderElement;
            if (header == null) continue;
            if (header.Parent is Panel oldStrip && !ReferenceEquals(oldStrip, strip))
                oldStrip.Children.Remove(header);
            strip.Children.Add(header);
        }
    }

    private static bool TryHorizontalScrollTabStrip(ScrollViewer sv, int delta)
    {
        if (sv.Visibility != Visibility.Visible || !sv.IsVisible || sv.ActualWidth <= 0 || sv.ActualHeight <= 0)
            return false;
        var pos = Mouse.GetPosition(sv);
        if (pos.Y < 0 || pos.Y > sv.ActualHeight || pos.X < 0 || pos.X > sv.ActualWidth)
            return false;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset + delta);
        return true;
    }

    private void UpdateActiveTabHooks(TabInfo tab)
    {
        if (_activeTab != null)
        {
            _activeTab.Editor.DirtyChanged -= OnActiveDirtyChanged;
            _activeTab.Editor.CaretMoved -= OnActiveCaretMoved;
        }

        _activeTab = tab;

        tab.Editor.DirtyChanged += OnActiveDirtyChanged;
        tab.Editor.CaretMoved += OnActiveCaretMoved;

        FindBarControl.SetEditor(tab.Editor);
        FindBarControl.RefreshSearch();

        ApplySettingsToEditor(tab.Editor);

        UpdateTitle();
        UpdateFileType();
        UpdateCaretPos();
    }

    private void CloseTab(TabInfo tab)
    {
        if (tab.Editor.IsDirty && !PromptSaveTab(tab)) return;
        RemoveTab(tab);
    }

    private void ClearClosedTabHistory() => _closedTabPaths.Clear();

    private static TabInfo? PickReplacementWhenRemovingTab(List<TabInfo> paneTabs, TabInfo removed)
    {
        int i = paneTabs.IndexOf(removed);
        if (i < 0) return paneTabs.FirstOrDefault();
        if (paneTabs.Count <= 1) return null;
        if (i < paneTabs.Count - 1) return paneTabs[i + 1];
        return paneTabs[i - 1];
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
            if (TryHorizontalScrollEditorTabStrips(delta))
                handled = true;

            if (!handled && _activeTab != null)
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
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, ApplyMaximizePadding);
        }
        else
        {
            BorderThickness = new Thickness(0, 0, 1, 1);
        }

        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void ApplyMaximizePadding()
    {
        if (WindowState != WindowState.Maximized) return;
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;

        var hwnd = source.Handle;
        if (!GetWindowRect(hwnd, out var wndRect)) return;
        if (!TryGetMonitorWorkArea(hwnd, out var rcWork, out _)) return;

        int overL = Math.Max(0, rcWork.Left - wndRect.Left);
        int overT = Math.Max(0, rcWork.Top - wndRect.Top);
        int overR = Math.Max(0, wndRect.Right - rcWork.Right);
        int overB = Math.Max(0, wndRect.Bottom - rcWork.Bottom);

        if (overL == 0 && overT == 0 && overR == 0 && overB == 0)
        {
            BorderThickness = new Thickness(0);
            return;
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        var tl = transform.Transform(new Point(overL, overT));
        var br = transform.Transform(new Point(overR, overB));

        BorderThickness = new Thickness(tl.X, tl.Y, br.X, br.Y);
    }

    private void ApplySettings()
    {
        foreach (var tab in AllTabsOrdered())
            ApplySettingsToEditor(tab.Editor);
        _terminalPanel.SyncEditorAppearanceFromSettings();
        FindBarControl.SetPosition(_settings.Editor.Find.BarPosition);
        FindBarControl.SeedWithSelection = _settings.Editor.Find.SeedWithSelection;
        CmdPalette.SetPosition(_settings.Application.CommandPalettePosition);
        MenuWordWrap.IsChecked = _settings.Editor.WordWrap;
        _tabHeaderFactory.FixedWidth = _settings.Editor.FixedWidthTabs;
        foreach (var tab in AllTabsOrdered())
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

    /// <summary>
    /// Registers the set of Volt-global shortcuts that should bubble past
    /// a focused terminal view so global actions keep working.
    /// Called by TerminalPanel when a new session is created.
    /// </summary>
    internal void RegisterTerminalAllowlist(TerminalView view)
    {
        // Command palette: Ctrl+Shift+P
        view.AddAllowlistedShortcut(Key.P, ModifierKeys.Control | ModifierKeys.Shift);
        // Switch editor tabs: Ctrl+Tab / Ctrl+Shift+Tab
        view.AddAllowlistedShortcut(Key.Tab, ModifierKeys.Control);
        view.AddAllowlistedShortcut(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
        view.AddAllowlistedShortcut(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
        // Settings: Ctrl+Alt+S (mapped to VoltCommand.Settings)
        view.AddAllowlistedShortcut(Key.S, ModifierKeys.Control | ModifierKeys.Alt);
        // Toggle explorer: Ctrl+B
        view.AddAllowlistedShortcut(Key.B, ModifierKeys.Control);
        // Toggle terminal panel itself: Ctrl+`
        view.AddAllowlistedShortcut(Key.OemTilde, ModifierKeys.Control);
    }

    private void FocusExplorer()
    {
        if (_explorerPanel.IsSearchFocused)
        {
            _activeTab?.Editor.Focus();
            return;
        }
        Shell.ShowPanel("file-explorer");
        _explorerPanel.FocusSearch();
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
        if (panelId.Equals("terminal", StringComparison.OrdinalIgnoreCase))
            _terminalPanel.NudgeAfterLayoutChange();
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
        // Save current folder's tabs and expanded paths before switching
        var currentFolder = _explorerPanel.OpenFolderPath;
        if (currentFolder != null)
        {
            SaveFolderSession(currentFolder);
            SaveFolderExpandedPaths(currentFolder);
        }

        ClearClosedTabHistory();
        CloseAllTabs();

        _explorerPanel.OpenFolder(newFolderPath);
        _settings.Editor.Explorer.OpenFolderPath = newFolderPath;
        Shell.ShowPanel("file-explorer");
        MenuCloseFolder.Visibility = Visibility.Visible;
        UpdateSaveWorkspaceMenuState();

        // Restore expanded paths and tabs for the new folder
        RestoreFolderExpandedPaths(newFolderPath);
        RestoreFolderTabs(newFolderPath);
        _settings.Save();
    }

    private void CloseFolderInExplorer()
    {
        var folderPath = _explorerPanel.OpenFolderPath;
        if (folderPath != null)
        {
            SaveFolderSession(folderPath);
            SaveFolderExpandedPaths(folderPath);
        }

        _settings.Session ??= new SessionSettings();
        _settings.Session.Terminal = CaptureTerminalPreferences();

        CloseAllTabs();
        _explorerPanel.CloseFolder();
        MenuCloseFolder.Visibility = Visibility.Collapsed;
        UpdateSaveWorkspaceMenuState();
        _settings.Editor.Explorer.OpenFolderPath = null;
        _settings.Editor.Explorer.ExpandedPaths.Clear();
        _settings.Save();

        // Create a fresh empty tab
        var tab = CreateTab();
        ActivateTab(tab);
        ScheduleRestoreTerminalGlobal(_settings.Session.Terminal);
    }

    private async void OnExplorerFileOpen(string path)
    {
        var tab = await OpenFileInTabAsync(path, reuseUntitled: true, activate: true);
        if (tab != null)
            FindBarControl.RefreshSearch();
    }

    private void OnExplorerFileRenamed(string oldPath, string newPath)
    {
        foreach (var tab in AllTabsOrdered())
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
        var affectedTabs = AllTabsOrdered().Where(t =>
            t.FilePath != null &&
            (string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase) ||
             t.FilePath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var tab in affectedTabs)
        {
            if (tab.Editor.IsDirty)
            {
                // Detach from deleted file so unsaved content isn't lost
                tab.StopWatching();
                tab.FilePath = null;
                UpdateTabHeader(tab);
                if (tab == _activeTab)
                {
                    UpdateTitle();
                    UpdateFileType();
                }
            }
            else
            {
                RemoveTab(tab, recordClosedTabHistory: false);
            }
        }
    }

    private async Task RestoreClosedTabAsync()
    {
        while (_closedTabPaths.Count > 0)
        {
            var path = _closedTabPaths[^1];
            _closedTabPaths.RemoveAt(_closedTabPaths.Count - 1);
            if (!File.Exists(path))
                continue;
            if (!CheckFileSize(path))
                continue;
            var tab = await OpenFileInTabAsync(path, reuseUntitled: true, activate: true);
            if (tab != null)
            {
                FindBarControl.RefreshSearch();
                return;
            }
        }
    }

    private void OnReopenClosedTab(object sender, RoutedEventArgs e) => _ = RestoreClosedTabAsync();

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
        var existing = AllTabsOrdered().FirstOrDefault(t =>
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

        var result = await LoadFileDataAsync(path, tab.Editor.TabSize);

        // Tab may have been closed while we were loading
        if (!TabExistsInAnyPane(tab)) return null;

        ApplyFileLoadResult(tab, result);
        return tab;
    }

    /// <summary>
    /// Opens a file in a tab synchronously. Used by session restore where async
    /// isn't needed (files load before the window is shown).
    /// </summary>
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
                    ActivateTab(AllTabsOrdered()[0]);
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
        var termPrefs = _settings.Session.Terminal;
        ScheduleRestoreTerminalGlobal(termPrefs);

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
            var session = _sessionManager.SaveSession(AllTabsOrdered(), _activeTab,
                editorLayout: BuildEditorLayoutSnapshotForSave());
            session.Terminal = CaptureTerminalPreferences();
            _settings.Session = session;
        }
    }

    private void SaveFolderSession(string folderPath)
    {
        SessionSettings.ClearFolderSessionDir(folderPath);
        var session = _sessionManager.SaveSession(AllTabsOrdered(), _activeTab, folderPath,
            BuildEditorLayoutSnapshotForSave());
        session.Terminal = CaptureTerminalPreferences();
        _settings.FolderSessions[folderPath] = session;
    }

    private TerminalPreferences CaptureTerminalPreferences()
    {
        return new TerminalPreferences
        {
            ShellPath = _settings.Editor.TerminalShellPath,
            ShellArgs = _settings.Editor.TerminalShellArgs,
            ScrollbackLines = _settings.Editor.TerminalScrollbackLines,
            OpenSessionCount = _terminalPanel.SessionCount,
            InitialWorkingDirectories = _terminalPanel.GetPersistedStartingDirectoriesPerSession()
        };
    }

    private void ApplyTerminalPreferences(TerminalPreferences? p)
    {
        if (p == null) return;
        _settings.Editor.TerminalShellPath = p.ShellPath;
        _settings.Editor.TerminalShellArgs = p.ShellArgs;
        if (p.ScrollbackLines > 0)
            _settings.Editor.TerminalScrollbackLines = p.ScrollbackLines;
    }

    private void ScheduleRestoreTerminalGlobal(TerminalPreferences? specific)
    {
        Dispatcher.BeginInvoke(new Action(() => RestoreTerminalForGlobalSession(specific)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ScheduleRestoreTerminalFolder(TerminalPreferences? specific)
    {
        Dispatcher.BeginInvoke(new Action(() => RestoreTerminalForFolder(specific)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ScheduleRestoreTerminalWorkspace(TerminalPreferences? specific)
    {
        Dispatcher.BeginInvoke(new Action(() => RestoreTerminalForWorkspace(specific)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Restore after global (no folder) editor session — may fall back to <see cref="AppSettings.Session"/> terminal snapshot.</summary>
    private void RestoreTerminalForGlobalSession(TerminalPreferences? specific)
    {
        var eff = specific ?? _settings.Session?.Terminal;
        ApplyTerminalPreferences(eff);
        int n = eff != null ? Math.Max(0, eff.OpenSessionCount) : 1;
        _terminalPanel.ResetToSessionCount(n, eff);
    }

    /// <summary>Restore when opening a folder. If there is no per-folder terminal block, do not inherit global/workspace instance count.</summary>
    private void RestoreTerminalForFolder(TerminalPreferences? specific)
    {
        if (specific != null)
        {
            ApplyTerminalPreferences(specific);
            _terminalPanel.ResetToSessionCount(Math.Max(0, specific.OpenSessionCount), specific);
        }
        else
        {
            _terminalPanel.ResetToSessionCount(1, null);
        }
    }

    /// <summary>Restore when opening a workspace file — may fall back to global session terminal for older workspace files.</summary>
    private void RestoreTerminalForWorkspace(TerminalPreferences? specific)
    {
        var eff = specific ?? _settings.Session?.Terminal;
        ApplyTerminalPreferences(eff);
        int n = eff != null ? Math.Max(0, eff.OpenSessionCount) : 1;
        _terminalPanel.ResetToSessionCount(n, eff);
    }

    private void SaveFolderExpandedPaths(string folderPath)
    {
        var paths = _explorerPanel.GetExpandedPaths();
        _settings.FolderExpandedPaths[folderPath] = paths;
        _settings.Editor.Explorer.ExpandedPaths = paths;
    }

    private void RestoreFolderExpandedPaths(string folderPath)
    {
        if (_settings.FolderExpandedPaths.TryGetValue(folderPath, out var paths) && paths.Count > 0)
            _explorerPanel.RestoreExpandedPaths(paths);
        else if (_settings.Editor.Explorer.ExpandedPaths.Count > 0)
            _explorerPanel.RestoreExpandedPaths(_settings.Editor.Explorer.ExpandedPaths);
    }

    private void RestoreFolderTabs(string folderPath)
    {
        if (!_settings.FolderSessions.TryGetValue(folderPath, out var session) || session.Tabs.Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            ScheduleRestoreTerminalFolder(session?.Terminal);
            return;
        }

        var restored = _sessionManager.RestoreSession(session, folderPath);
        RestoreTabsFromSession(restored);
        ScheduleRestoreTerminalFolder(session.Terminal);
    }

    private void RestoreTabsFromSession(RestoredSession restored)
    {
        if (restored.Tabs.Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            return;
        }

        EnsureSingleLeafLayoutForRestore();

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

        ApplyRestoredEditorLayoutIfAny(restored.EditorLayout);

        if (AllTabsOrdered().Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
        }
        else
        {
            ActivateTab(activeTab ?? AllTabsOrdered()[0]);
        }

        // Kick off async file loads (window is already visible at this point)
        foreach (var (t, r) in asyncLoads)
            _ = LoadTabContentAsync(t, r);
    }

    private async Task LoadTabContentAsync(TabInfo tab, RestoredTab rt)
    {
        var result = await LoadFileDataAsync(rt.FilePath!, tab.Editor.TabSize);
        if (!TabExistsInAnyPane(tab)) return;

        ApplyFileLoadResult(tab, result);
        if (rt.IsDirty) tab.Editor.MarkDirty();

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
            foreach (var tab in AllTabsOrdered().ToList())
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
        // Save expanded paths both globally (for backward compat) and per-folder
        _settings.Editor.Explorer.ExpandedPaths = _explorerPanel.GetExpandedPaths();
        var openFolder = _explorerPanel.OpenFolderPath;
        if (openFolder != null && _workspaceManager.CurrentWorkspace == null)
            SaveFolderExpandedPaths(openFolder);
        _settings.Editor.PanelLayouts = Shell.GetCurrentLayout();
        _settings.Editor.OpenRegions = Shell.GetOpenRegions();
        _settings.Save();

        foreach (var tab in AllTabsOrdered())
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

        if (_activeTab!.LanguageOverride != null)
        {
            // Empty string = explicit "Plain Text" override; non-empty = language name
            var grammar = _activeTab.LanguageOverride.Length > 0
                ? SyntaxManager.GetDefinitionByName(_activeTab.LanguageOverride)
                : null;
            editor.SetGrammar(grammar);
            FileTypeText.Text = grammar?.Name ?? "Plain Text";
        }
        else
        {
            var ext = _activeTab.FilePath != null ? Path.GetExtension(_activeTab.FilePath).ToLowerInvariant() : "";
            editor.SetGrammar(SyntaxManager.GetDefinition(ext));
            FileTypeText.Text = FileHelper.GetFileTypeName(ext);
        }

        EncodingText.Text = GetEncodingLabel();
        LineEndingText.Text = editor.LineEnding;
    }

    private void SetActiveTabLanguage(string? languageName)
    {
        if (_activeTab == null) return;
        _activeTab.LanguageOverride = languageName;
        UpdateFileType();
    }

    private void OnFileTypeClick(object sender, MouseButtonEventArgs e)
    {
        EnsurePaletteCommands();
        CmdPalette.OpenWithCommand("Change Language");
    }

    private void UpdateTitle() => Title = "Volt";

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
        var oldExt = tab.FilePath != null ? Path.GetExtension(tab.FilePath) : "";
        tab.FilePath = dlg.FileName;
        // Clear manual language override when the extension changes so auto-detect kicks in
        if (!string.Equals(oldExt, Path.GetExtension(dlg.FileName), StringComparison.OrdinalIgnoreCase))
            tab.LanguageOverride = null;
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
        if (tab.IsLoading) return;
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
        foreach (var tab in AllTabsOrdered().ToList())
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
            if (tab == _activeTab)
            {
                UpdateTitle();
                UpdateFileType();
            }
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

    private void OnOpenRecentSubmenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateRecentMenu((MenuItem)sender);
    }

    private void PopulateRecentMenu(MenuItem menu)
    {
        menu.Items.Clear();

        var dropdownStyle = (Style)FindResource("MenuItemDropdownStyle");
        var recentItems = _settings.Application.RecentItems;
        if (recentItems.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false, Style = dropdownStyle });
            return;
        }

        foreach (var recent in recentItems)
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

        var viewMore = new MenuItem { Header = "View More...", Style = dropdownStyle };
        viewMore.Click += (_, _) => OpenRecentInCommandPalette();
        menu.Items.Add(viewMore);

        var clearItem = new MenuItem { Header = "Clear Recent", Style = dropdownStyle };
        clearItem.Click += (_, _) =>
        {
            if (ThemedMessageBox.Show(this, "Clear the recent items list?", "Clear Recent",
                    MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            _settings.Application.RecentItems.Clear();
            _settings.Save();
        };
        menu.Items.Add(clearItem);
    }

    private void OpenRecentInCommandPalette()
    {
        var history = _settings.Application.RecentHistory;
        if (history.Count == 0)
        {
            Dispatcher.BeginInvoke(() => CmdPalette.OpenWithOptions("Recent: ", [
                new("(no history)", ApplyPreview: () => { }, Commit: () => { }, Revert: () => { })
            ]));
            return;
        }

        var options = history.Select(recent =>
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

        options.Add(new("Clear History", ApplyPreview: () => { }, Commit: () =>
        {
            if (ThemedMessageBox.Show(this, "Clear the entire recent history?", "Clear History",
                    MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            _settings.Application.RecentHistory.Clear();
            _settings.Save();
        }, Revert: () => { }));

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
                    RemoveFromRecentLists(path, kind);
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
                    RemoveFromRecentLists(path, kind);
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

    private void RemoveFromRecentLists(string path, RecentItemKind kind)
    {
        bool Match(RecentItem r) =>
            string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase) && r.Kind == kind;
        _settings.Application.RecentItems.RemoveAll(Match);
        _settings.Application.RecentHistory.RemoveAll(Match);
        _settings.Save();
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
        foreach (var tab in AllTabsOrdered())
            tab.Editor.WordWrap = _settings.Editor.WordWrap;
        _settings.Save();
    }

    private void ToggleFixedWidthTabs()
    {
        _settings.Editor.FixedWidthTabs = !_settings.Editor.FixedWidthTabs;
        _tabHeaderFactory.FixedWidth = _settings.Editor.FixedWidthTabs;
        foreach (var tab in AllTabsOrdered())
            _tabHeaderFactory.ApplyFixedWidth(tab.HeaderElement);
        _settings.Save();
    }

    private void ToggleWordWrapAtWords()
    {
        _settings.Editor.WordWrapAtWords = !_settings.Editor.WordWrapAtWords;
        foreach (var tab in AllTabsOrdered())
            tab.Editor.WordWrapAtWords = _settings.Editor.WordWrapAtWords;
        _settings.Save();
    }

    private void ToggleWordWrapIndent()
    {
        _settings.Editor.WordWrapIndent = !_settings.Editor.WordWrapIndent;
        foreach (var tab in AllTabsOrdered())
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

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutWindow { Owner = this };
        dlg.ShowDialog();
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
            _keyBindingManager.GetAllBindings(),
            _settings.Editor.TerminalShellPath, _settings.Editor.TerminalShellArgs, _settings.Editor.TerminalScrollbackLines);
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
        _settings.Editor.TerminalShellPath = dlg.TerminalShellPath;
        _settings.Editor.TerminalShellArgs = dlg.TerminalShellArgs;
        _settings.Editor.TerminalScrollbackLines = dlg.TerminalScrollbackLines;
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
            case VoltCommand.ReopenClosedTab: _ = RestoreClosedTabAsync(); break;
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
            case VoltCommand.GoToLine: OpenGoToLine(); break;
            case VoltCommand.FocusExplorer: FocusExplorer(); break;
            case VoltCommand.ToggleTerminal: ToggleTerminalPanel(); break;
            case VoltCommand.ToggleEditorSplit: ToggleEditorSplitFromCommand(); break;
            case VoltCommand.JoinEditorSplit: JoinEditorWithSibling(); break;
            case VoltCommand.JoinEditorFlattenAll: JoinEditorFlattenAll(); break;
            case VoltCommand.SwitchEditorSplitOrientation: ToggleParentSplitOrientation(); break;
            case VoltCommand.FocusOtherEditorPane: FocusNextEditorLeafFromCommand(); break;
        }
    }

    private void ToggleTerminalPanel()
    {
        if (!Shell.IsPanelVisible("terminal"))
            Shell.ShowPanel("terminal");
        if (_terminalPanel.SessionCount == 0)
            _terminalPanel.NewSession();
        else
            Dispatcher.BeginInvoke(new Action(() => _terminalPanel.TryFocusActiveSession()), System.Windows.Threading.DispatcherPriority.Input);
        SyncViewMenuChecks();
    }

    private void OpenGoToLine()
    {
        EnsurePaletteCommands();
        CmdPalette.OpenFreeInput("Go to Line: ", text =>
        {
            if (int.TryParse(text.Trim(), out int line) && line >= 1)
                Editor?.GoToLine(line - 1);
        });
    }

    private void EnsurePaletteCommands()
    {
        if (Editor is not { } editor) return;
        var commands = CommandPaletteCommands.Build(new CommandPaletteContext(
            AllTabsOrdered(), _settings, ThemeManager, editor, FindBarControl, CmdPalette, () => _settings.Save(),
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
            OpenRecentInCommandPalette,
            SetActiveTabLanguage,
            new TerminalActions(
                () => ToggleTerminalPanel(),
                () =>
                {
                    Shell.ShowPanel("terminal");
                    _terminalPanel.NewSession();
                }),
            () => _terminalPanel.SyncEditorAppearanceFromSettings(),
            new EditorSplitActions(
                ToggleEditorSplitFromCommand,
                JoinEditorWithSibling,
                JoinEditorFlattenAll,
                ToggleParentSplitOrientation,
                () => FocusNextEditorLeafFromCommand())));
        CmdPalette.SetCommands(commands);
    }

    private void UpdateMenuGestureText()
    {
        MenuSettings.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.Settings);
        MenuSave.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.Save);
        MenuSaveAs.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.SaveAs);
        MenuOpenFile.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.OpenFile);
        MenuOpenFolder.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.OpenFolder);
        MenuReopenClosedTab.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ReopenClosedTab);
        MenuViewLeft.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ToggleLeftPanel);
        MenuViewRight.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ToggleRightPanel);
        MenuViewTop.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ToggleTopPanel);
        MenuViewBottom.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ToggleBottomPanel);
        MenuSplitEditor.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.ToggleEditorSplit);
        MenuJoinEditor.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.JoinEditorSplit);
        MenuJoinEditorAll.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.JoinEditorFlattenAll);
        MenuSplitOrientation.InputGestureText = _keyBindingManager.GetGestureText(VoltCommand.SwitchEditorSplitOrientation);
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
        foreach (var tab in AllTabsOrdered())
            tab.Editor.EditorFontSize = newSize;
        _settings.Editor.Font.Size = newSize;
        _settings.Save();
        _terminalPanel.SyncEditorAppearanceFromSettings();
    }

    private void OpenCommandPalette()
    {
        EnsurePaletteCommands();
        CmdPalette.Open();
    }

    // ── Workspace menu handlers ─────────────────────────────────────────────

    private void OnSaveWorkspaceAs(object sender, RoutedEventArgs e)
    {
        // If a single folder is open (no workspace), promote it to a workspace first
        if (_workspaceManager.CurrentWorkspace == null)
        {
            var folderPath = _explorerPanel.OpenFolderPath;
            if (folderPath == null) return;

            SaveFolderSession(folderPath);
            SaveFolderExpandedPaths(folderPath);

            _workspaceManager.NewWorkspace(Path.GetFileName(folderPath));
            _workspaceManager.AddFolder(folderPath);
            _explorerPanel.OpenWorkspace(_workspaceManager.CurrentWorkspace!);
            RestoreFolderExpandedPaths(folderPath);

            _settings.Editor.Explorer.OpenFolderPath = null;
            MenuCloseFolder.Visibility = Visibility.Collapsed;
            UpdateWorkspaceMenuState(true);

            // Restore tabs in workspace context
            if (_settings.FolderSessions.TryGetValue(folderPath, out var session))
            {
                _workspaceManager.CurrentWorkspace!.Session = new WorkspaceSession
                {
                    Tabs = session.Tabs.Select(t => new WorkspaceSessionTab
                    {
                        FilePath = t.FilePath,
                        IsDirty = t.IsDirty,
                        CaretLine = t.CaretLine,
                        CaretCol = t.CaretCol,
                        ScrollX = t.ScrollHorizontal,
                        ScrollY = t.ScrollVertical,
                    }).ToList(),
                    ActiveTabIndex = session.ActiveTabIndex,
                    ExpandedPaths = _explorerPanel.GetExpandedPaths(),
                    Terminal = session.Terminal
                };
            }
        }

        var defaultName = _workspaceManager.CurrentWorkspace!.Name ?? "MyWorkspace";
        var dlg = new SaveFileDialog
        {
            Filter = "Workspace Files (*.volt-workspace)|*.volt-workspace",
            DefaultExt = ".volt-workspace",
            FileName = defaultName + ".volt-workspace"
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

    private void OnCloseFolder(object sender, RoutedEventArgs e)
    {
        CloseFolderInExplorer();
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
                SaveFolderExpandedPaths(currentFolder);
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

        if (AllTabsOrdered().Count == 0)
        {
            CreateTab();
            ActivateTab(AllTabsOrdered()[0]);
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
        ClearClosedTabHistory();
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
        _settings.Session ??= new SessionSettings();
        _settings.Session.Terminal = CaptureTerminalPreferences();
        if (_workspaceManager.CurrentWorkspace.FilePath != null)
            _workspaceManager.SaveWorkspace();
        _workspaceManager.CloseWorkspace();

        _explorerPanel.CloseWorkspace();
        UpdateWorkspaceMenuState(false);

        _settings.LastOpenWorkspacePath = null;
        _settings.UnsavedWorkspaceFolders = null;
        _settings.UnsavedWorkspaceSession = null;
        _settings.Save();

        ClearClosedTabHistory();
        CloseAllTabs();
        CreateTab();
        ActivateTab(AllTabsOrdered()[0]);
        ScheduleRestoreTerminalGlobal(_settings.Session?.Terminal);
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
        MenuSaveWorkspaceAs.IsEnabled = workspaceOpen || _explorerPanel.OpenFolderPath != null;
        if (workspaceOpen) MenuCloseFolder.Visibility = Visibility.Collapsed;
    }

    private void UpdateSaveWorkspaceMenuState()
    {
        MenuSaveWorkspaceAs.IsEnabled = _workspaceManager.CurrentWorkspace != null || _explorerPanel.OpenFolderPath != null;
    }

    private void CloseAllTabs() => CloseAllTabsCore();

    private bool PromptSaveDirtyTabs()
    {
        foreach (var tab in AllTabsOrdered().ToList())
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

        foreach (var t in AllTabsOrdered())
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
            ExpandedPaths = _explorerPanel.GetExpandedPaths(),
            Terminal = CaptureTerminalPreferences(),
            EditorLayout = BuildEditorLayoutSnapshotForSave()
        };
    }

    private void RestoreWorkspaceSession(Workspace workspace)
    {
        if (workspace.Session.Tabs.Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            ScheduleRestoreTerminalWorkspace(workspace.Session.Terminal);
            return;
        }

        EnsureSingleLeafLayoutForRestore();

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

        ApplyRestoredEditorLayoutIfAny(workspace.Session.EditorLayout);

        if (AllTabsOrdered().Count == 0)
        {
            var tab = CreateTab();
            ActivateTab(tab);
            return;
        }

        ActivateTab(activeTab ?? AllTabsOrdered()[0]);

        foreach (var (t, s) in asyncLoads)
            _ = LoadWorkspaceTabAsync(t, s);

        ScheduleRestoreTerminalWorkspace(workspace.Session.Terminal);
    }

    private async Task LoadWorkspaceTabAsync(TabInfo tab, WorkspaceSessionTab st)
    {
        var result = await LoadFileDataAsync(st.FilePath!, tab.Editor.TabSize);
        if (!TabExistsInAnyPane(tab)) return;

        ApplyFileLoadResult(tab, result);

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

    private static bool TryGetMonitorWorkArea(IntPtr hwnd, out RECT rcWork, out RECT rcMonitor)
    {
        rcWork = default;
        rcMonitor = default;
        // MonitorFromWindow may resolve to the wrong display at mixed DPI.
        // Center from GetWindowRect stays on the correct display (physical screen coordinates).
        IntPtr hMonitor;
        if (GetWindowRect(hwnd, out var wndRect)
            && wndRect.Right > wndRect.Left
            && wndRect.Bottom > wndRect.Top)
        {
            var cx = (wndRect.Left + wndRect.Right) / 2;
            var cy = (wndRect.Top + wndRect.Bottom) / 2;
            hMonitor = MonitorFromPoint(new POINT { X = cx, Y = cy }, MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        }

        if (hMonitor == IntPtr.Zero) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref mi)) return false;

        rcWork = mi.rcWork;
        rcMonitor = mi.rcMonitor;
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

}
