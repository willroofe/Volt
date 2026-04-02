using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Volt;

public class TabInfo
{
    private const int DebounceMsec = 200;

    public string? FilePath { get; set; }
    public Encoding FileEncoding { get; set; } = new UTF8Encoding(false);
    public EditorControl Editor { get; }
    public ScrollViewer ScrollHost { get; }
    public Border HeaderElement { get; set; } = null!;

    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : "Untitled";

    /// <summary>Tracks the last write time when we loaded or saved the file, to detect external changes.</summary>
    public DateTime LastKnownWriteTimeUtc { get; set; }

    /// <summary>Tracks the file size at last load/reload, used to detect append-only changes.</summary>
    public long LastKnownFileSize { get; set; }

    /// <summary>Last bytes of the file at load time, used to verify append-only changes.</summary>
    public byte[]? TailVerifyBytes { get; set; }

    /// <summary>Guards against re-entrant external change handling (MessageBox pumps messages).</summary>
    public bool IsHandlingExternalChange { get; set; }

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounceTimer;

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
        _debounceTimer?.Stop();
        _debounceTimer = null;
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: FileSystemWatcher fires multiple times per write and we may
        // read mid-write if we act immediately.  Restart the timer on each event
        // so we wait until the burst settles before reloading.
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            if (_debounceTimer == null)
            {
                _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMsec) };
                _debounceTimer.Tick += (_, _) =>
                {
                    _debounceTimer.Stop();
                    FileChangedExternally?.Invoke(this);
                };
            }
            _debounceTimer.Stop();
            _debounceTimer.Start();
        });
    }
}
