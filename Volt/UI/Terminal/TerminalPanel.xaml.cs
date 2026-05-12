using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Volt;

public partial class TerminalPanel : UserControl, IPanel
{
    private readonly List<TerminalSession> _sessions = new();
    private TerminalSession? _active;

    public string PanelId => "terminal";
    public string Title => "Terminal";
    public string? IconGlyph => Codicons.Terminal;
    public new UIElement Content => this;
#pragma warning disable CS0067 // Title is fixed; event required by IPanel for other panels
    public event Action? TitleChanged;
#pragma warning restore CS0067
    public event Action? LastSessionClosed;

    public TerminalPanel()
    {
        InitializeComponent();
    }

    /// <summary>Re-read editor font and caret settings into every open terminal view.</summary>
    public void SyncEditorAppearanceFromSettings()
    {
        foreach (var s in _sessions)
            s.View.SyncFromActiveEditor();
    }

    internal void RefreshShortcutAllowlists()
    {
        if (Application.Current.MainWindow is not MainWindow mainWindow)
            return;

        foreach (var s in _sessions)
            mainWindow.RegisterTerminalAllowlist(s.View);
    }

    public int SessionCount => _sessions.Count;

    /// <summary>Closes all instance tabs and opens <paramref name="count"/> new ones using current shell settings.</summary>
    public void ResetToSessionCount(int count, TerminalPreferences? prefs)
    {
        count = Math.Max(0, count);
        foreach (var s in _sessions.ToList())
            CloseSession(s, notifyLastSessionClosed: false);
        for (int i = 0; i < count; i++)
            NewSession(prefs?.GetWorkingDirectoryForInstance(i));
    }

    /// <summary>Spawn cwd for each instance tab, in order (same length as <see cref="SessionCount"/>).</summary>
    public List<string> GetPersistedStartingDirectoriesPerSession()
    {
        return _sessions.Select(s =>
        {
            var raw = s.WorkingDirectory;
            if (string.IsNullOrEmpty(raw)) return "";
            try { return Path.GetFullPath(raw); } catch { return raw; }
        }).ToList();
    }

    /// <param name="cwd">Starting directory; null uses workspace/file rules.</param>
    /// <param name="shellPreference">When set, starts that shell for this tab only (does not change settings).</param>
    internal void NewSession(string? cwd = null, TerminalShellPreference? shellPreference = null)
    {
        // DIAGNOSTIC: write a VT trace to %TEMP%/volt-terminal-trace.log for this session
        var tracePath = Path.Combine(Path.GetTempPath(), "volt-terminal-trace.log");
        try { File.Delete(tracePath); } catch { }
        VtDispatcher.TraceLogPath = tracePath;

        var app = Application.Current as App;
        var editor = app?.Settings.Editor;

        string shell;
        string? args;
        if (shellPreference is { } pick)
        {
            shell = TerminalShellCatalog.ResolveShellPath(pick);
            var configuredKind = TerminalShellCatalog.ClassifyPath(editor?.TerminalShellPath);
            args = configuredKind == pick ? editor?.TerminalShellArgs : null;
        }
        else
        {
            shell = editor?.TerminalShellPath ?? "";
            if (string.IsNullOrEmpty(shell))
            {
                shell = TerminalShellCatalog.ResolveDefaultShell();
                if (editor != null)
                {
                    editor.TerminalShellPath = shell;
                    app!.Settings.Save();
                }
            }

            args = editor?.TerminalShellArgs;
        }
        int scrollback = editor?.TerminalScrollbackLines ?? 10_000;
        var startDir = cwd ?? ResolveStartingDirectory();

        TerminalSession s;
        try
        {
            s = new TerminalSession(shell, args, startDir, 24, 80, scrollback);
        }
        catch (TerminalUnavailableException ex)
        {
            ThemedMessageBox.Show(Application.Current.MainWindow, $"Terminal unavailable: {ex.Message}", "Terminal");
            return;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ThemedMessageBox.Show(Application.Current.MainWindow,
                $"Could not start terminal:\n{ex.Message}\n\nShell: {shell}\nCwd: {startDir}",
                "Terminal");
            return;
        }

        s.Exited += _ => Dispatcher.BeginInvoke(new Action(() => CloseSession(s)));
        _sessions.Add(s);
        SetActive(s);
        RefreshShortcutAllowlists();
        RebuildTabs();
    }

    /// <summary>Moves keyboard focus to the active terminal view for typing.</summary>
    public void TryFocusActiveSession()
    {
        if (_active?.View is not { } v) return;
        Application.Current.MainWindow?.Activate();
        v.Focus();
        Keyboard.Focus(v);
        v.ResyncCaretAfterFocusAttempt();
    }

    /// <summary>Workaround after reparenting: nudge scroll + restore focus/caret once layout has settled.</summary>
    public void NudgeAfterLayoutChange()
    {
        if (_sessions.Count == 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var s in _sessions)
            {
                var view = s.View;
                double y = view.VerticalOffset;
                view.LineUp();
                view.SetVerticalOffset(y);
                view.ScrollOwner?.InvalidateScrollInfo();
                view.InvalidateVisual();
            }
            if (Keyboard.FocusedElement is not EditorControl)
                Dispatcher.BeginInvoke(new Action(TryFocusActiveSession), DispatcherPriority.ApplicationIdle);
        }), DispatcherPriority.Loaded);
    }

    public void CloseActiveSession()
    {
        if (_active != null) CloseSession(_active);
    }

    private void CloseSession(TerminalSession s, bool notifyLastSessionClosed = true)
    {
        if (!_sessions.Remove(s)) return;

        try { s.Dispose(); } catch { }
        if (_active == s) _active = _sessions.Count > 0 ? _sessions[^1] : null;
        SetActive(_active);
        RebuildTabs();
        if (_sessions.Count == 0 && notifyLastSessionClosed)
            LastSessionClosed?.Invoke();
    }

    private void SetActive(TerminalSession? s)
    {
        _active = s;
        ActiveContent.Content = s?.ScrollHost;
        ApplyActiveTabStyle();
        // Focus after layout: switching ContentPresenter content synchronously often drops Keyboard.Focus.
        if (s != null)
            Dispatcher.BeginInvoke(new Action(TryFocusActiveSession), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RebuildTabs()
    {
        TabStrip.Children.Clear();
        foreach (var s in _sessions)
            TabStrip.Children.Add(CreateSessionTab(s));
        ApplyActiveTabStyle();
    }

    private void ApplyActiveTabStyle()
    {
        foreach (var child in TabStrip.Children)
        {
            if (child is Border b && b.Tag is TerminalSession sess)
            {
                b.SetResourceReference(Border.BackgroundProperty,
                    sess == _active ? ThemeResourceKeys.TabActive : ThemeResourceKeys.ExplorerHeaderBg);
            }
        }
    }

    private Border CreateSessionTab(TerminalSession s)
    {
        var textBlock = new TextBlock
        {
            Text = s.Title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);

        var closeBtn = new Button { Margin = new Thickness(0, 0, 6, 0) };
        if (Application.Current?.TryFindResource("TabCloseButton") is Style closeStyle)
            closeBtn.Style = closeStyle;
        var captured = s;
        closeBtn.Click += (_, _) => CloseSession(captured);

        var tabPanel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(closeBtn, Dock.Right);
        tabPanel.Children.Add(closeBtn);
        tabPanel.Children.Add(textBlock);

        var header = new Border
        {
            Tag = s,
            Child = tabPanel,
            Height = UIConstants.TabBarHeight,
            MinWidth = 40,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        header.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.TabBorder);
        header.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.ExplorerHeaderBg);

        header.MouseLeftButtonDown += (_, e) =>
        {
            SetActive(captured);
            e.Handled = true;
        };
        header.MouseRightButtonUp += (_, e) =>
        {
            if (header.ContextMenu == null)
            {
                var menu = ContextMenuHelper.Create();
                menu.Items.Add(ContextMenuHelper.Item("Close", Codicons.Close, () => CloseSession(captured)));
                header.ContextMenu = menu;
            }
            header.ContextMenu.IsOpen = true;
            e.Handled = true;
        };

        return header;
    }

    private void OnAddClicked(object sender, RoutedEventArgs e) => NewSession();

    private void OnShellPickerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var menu = ContextMenuHelper.Create();
        menu.Placement = PlacementMode.Bottom;
        menu.PlacementTarget = btn;
        menu.PlacementRectangle = new Rect(0, btn.ActualHeight, btn.ActualWidth, 0);
        foreach (var option in TerminalShellCatalog.Options)
        {
            var preference = option.Preference;
            menu.Items.Add(ContextMenuHelper.Item(
                option.DisplayName,
                Codicons.Terminal,
                () => NewSession(shellPreference: preference)));
        }
        menu.IsOpen = true;
    }

    private string ResolveStartingDirectory()
    {
        if (Application.Current.MainWindow is not MainWindow mw)
            return Environment.GetEnvironmentVariable("USERPROFILE") ?? ".";

        var workspace = mw.ActiveWorkspace;
        var activeFile = mw.ActiveFilePath;
        var openFolder = mw.OpenFolderPath;

        // Rule 1: No workspace, single file open (no folder) → file's directory
        if (workspace == null && openFolder == null && !string.IsNullOrEmpty(activeFile))
        {
            var dir = Path.GetDirectoryName(activeFile);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }

        // Rule 2: No workspace, single folder open → folder root (regardless of active file)
        if (workspace == null && !string.IsNullOrEmpty(openFolder) && Directory.Exists(openFolder))
            return openFolder;

        // Rule 3: Workspace with active tab → file's parent directory
        if (workspace != null && !string.IsNullOrEmpty(activeFile))
        {
            var dir = Path.GetDirectoryName(activeFile);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }

        // Rule 4: Workspace with no active tab → first folder in workspace
        if (workspace != null && workspace.Folders.Count > 0)
        {
            var first = workspace.Folders[0];
            if (Directory.Exists(first)) return first;
        }

        // Rule 5: Fallback
        return Environment.GetEnvironmentVariable("USERPROFILE") ?? ".";
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (VisualParent == null)
        {
            foreach (var s in _sessions)
            {
                try { s.Dispose(); } catch { }
            }
            _sessions.Clear();
            _active = null;
        }
    }
}
