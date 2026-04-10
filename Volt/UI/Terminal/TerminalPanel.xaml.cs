using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Volt;

public partial class TerminalPanel : UserControl, IPanel
{
    private readonly List<TerminalSession> _sessions = new();
    private TerminalSession? _active;

    public string PanelId => "terminal";
    public string Title => _active?.Title ?? "Terminal";
    public string? IconGlyph => "\uE756"; // Segoe MDL2 CommandPrompt
    public new UIElement Content => this;
    public event Action? TitleChanged;

    public TerminalPanel()
    {
        InitializeComponent();
    }

    public void NewSession(string? cwd = null)
    {
        // TODO(Task 35): pull TerminalShellPath, TerminalShellArgs, TerminalScrollbackLines from AppSettings
        var shell = ResolveDefaultShell();
        string? args = null;
        int scrollback = 10_000;
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
            ThemedMessageBox.Show(Application.Current.MainWindow, $"Could not start terminal:\n{ex.Message}\n\nShell: {shell}", "Terminal");
            return;
        }

        s.TitleChanged += () => { TitleChanged?.Invoke(); Dispatcher.BeginInvoke(new Action(RebuildTabs)); };
        s.Exited += _ => Dispatcher.BeginInvoke(new Action(() => CloseSession(s)));
        _sessions.Add(s);
        SetActive(s);
        if (Application.Current.MainWindow is MainWindow mw)
            mw.RegisterTerminalAllowlist(s.View);
        RebuildTabs();
    }

    public void CloseActiveSession()
    {
        if (_active != null) CloseSession(_active);
    }

    private void CloseSession(TerminalSession s)
    {
        try { s.Dispose(); } catch { }
        _sessions.Remove(s);
        if (_active == s) _active = _sessions.Count > 0 ? _sessions[^1] : null;
        SetActive(_active);
        RebuildTabs();
    }

    private void SetActive(TerminalSession? s)
    {
        _active = s;
        ActiveContent.Content = s?.View;
        if (s != null) s.View.Focus();
        TitleChanged?.Invoke();
    }

    private void RebuildTabs()
    {
        var items = new List<object>();
        foreach (var s in _sessions)
        {
            var btn = new Button { Content = s.Title, MinWidth = 80, Margin = new Thickness(2, 0, 2, 0) };
            var captured = s;
            btn.Click += (_, _) => SetActive(captured);
            btn.MouseRightButtonUp += (_, _) => CloseSession(captured);
            items.Add(btn);
        }
        TabsList.ItemsSource = items;
    }

    private void OnAddClicked(object sender, RoutedEventArgs e) => NewSession();

    private static string ResolveDefaultShell()
    {
        foreach (var name in new[] { "pwsh.exe", "powershell.exe", "cmd.exe" })
        {
            var path = FindInPath(name);
            if (path != null) return path;
        }
        return "cmd.exe";
    }

    private static string? FindInPath(string exe)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
        foreach (var p in paths)
        {
            try
            {
                var candidate = Path.Combine(p, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
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
