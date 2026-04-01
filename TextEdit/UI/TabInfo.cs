using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace TextEdit;

public class TabInfo
{
    public string? FilePath { get; set; }
    public Encoding FileEncoding { get; set; } = new UTF8Encoding(false);
    public EditorControl Editor { get; }
    public ScrollViewer ScrollHost { get; }
    public Border HeaderElement { get; set; } = null!;

    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : "Untitled";

    /// <summary>Tracks the last write time when we loaded or saved the file, to detect external changes.</summary>
    public DateTime LastKnownWriteTimeUtc { get; set; }

    /// <summary>Set to true while we are saving, to suppress our own FileSystemWatcher events.</summary>
    public bool SuppressWatcher { get; set; }

    /// <summary>Guards against re-entrant external change handling (MessageBox pumps messages).</summary>
    public bool IsHandlingExternalChange { get; set; }

    private FileSystemWatcher? _watcher;

    /// <summary>Fired (on the UI thread) when the file is modified externally.</summary>
    public event Action<TabInfo>? FileChangedExternally;

    public TabInfo(ThemeManager themeManager, SyntaxManager syntaxManager)
    {
        Editor = new EditorControl
        {
            ThemeManager = themeManager,
            SyntaxManager = syntaxManager
        };
        ScrollHost = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            CanContentScroll = true,
            Content = Editor,
            Template = (ControlTemplate)Application.Current.FindResource("ThemedScrollViewer")
        };
    }

    public void StartWatching()
    {
        StopWatching();
        if (FilePath == null || !File.Exists(FilePath)) return;

        var dir = Path.GetDirectoryName(FilePath);
        var name = Path.GetFileName(FilePath);
        if (dir == null) return;

        LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
        _watcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnWatcherChanged;
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnWatcherChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (SuppressWatcher) return;
        Application.Current?.Dispatcher?.BeginInvoke(() => FileChangedExternally?.Invoke(this));
    }
}
