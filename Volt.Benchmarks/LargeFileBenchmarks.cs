using System.IO;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Threading;
using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

[MemoryDiagnoser]
public class LargeFileBenchmarks
{
    private string? _path;
    private TextBuffer _buffer = null!;
    private TextBuffer.PreparedContent? _prepared;
    private Encoding _encoding = null!;
    private ThemeManager _themeManager = null!;
    private SyntaxManager _syntaxManager = null!;
    private StaBenchmarkThread _staThread = null!;
    private readonly Random _random = new(1234);
    private int[] _sampleLines = [];

    [GlobalSetup]
    public void Setup()
    {
        _path = Environment.GetEnvironmentVariable("VOLT_LARGE_FILE_BENCH_PATH");
        _encoding = new UTF8Encoding(false);
        _staThread = new StaBenchmarkThread();
        _staThread.Invoke(() =>
        {
            _themeManager = new ThemeManager();
            _syntaxManager = new SyntaxManager();
            return 0;
        });

        string? path = _path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _buffer = new TextBuffer();
            _buffer.SetContent("set VOLT_LARGE_FILE_BENCH_PATH to run large-file benchmarks", tabSize: 4);
            _sampleLines = [0];
            return;
        }

        WarmFileCache(path);
        _prepared = TextBuffer.PrepareContentFromFile(path, _encoding, tabSize: 4);
        _buffer = new TextBuffer();
        _buffer.SetPreparedContent(_prepared);
        _sampleLines = Enumerable.Range(0, Math.Min(128, _buffer.Count))
            .Select(_ => _random.Next(0, _buffer.Count))
            .ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _staThread.Dispose();
    }

    [Benchmark(Description = "Large file core UTF-8 prepare/index")]
    public int IndexBuild()
    {
        string? path = _path;
        if (path == null || !File.Exists(path))
            return 0;

        var buffer = new TextBuffer();
        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, _encoding, tabSize: 4));
        return buffer.Count;
    }

    [Benchmark(Description = "Large file progress-enabled prepare/index")]
    public int IndexBuildWithProgress()
    {
        string? path = _path;
        if (path == null || !File.Exists(path))
            return 0;

        var progress = new LoadProgressSink();
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, _encoding, 4, progress));
        return buffer.Count + progress.ReportCount;
    }

    [Benchmark(Description = "Large file apply prepared file-backed content")]
    public int ApplyPreparedContentToEditor()
    {
        if (_prepared == null)
            return 0;

        return _staThread.Invoke(() =>
        {
            var editor = new EditorControl(_themeManager, _syntaxManager);
            editor.SetPreparedContent(_prepared);
            return editor.LineEnding.Length;
        });
    }

    [Benchmark(Description = "Large file full prepare and editor apply")]
    public int PrepareAndApplyToEditor()
    {
        string? path = _path;
        if (path == null || !File.Exists(path))
            return 0;

        TextBuffer.PreparedContent prepared = TextBuffer.PrepareContentFromFile(path, _encoding, tabSize: 4);
        return _staThread.Invoke(() =>
        {
            var editor = new EditorControl(_themeManager, _syntaxManager);
            editor.SetPreparedContent(prepared);
            return editor.LineEnding.Length;
        });
    }

    [Benchmark(Description = "Large file open data pipeline")]
    public long OpenDataPipeline()
    {
        string? path = _path;
        if (path == null || !File.Exists(path))
            return 0;

        Encoding encoding = FileHelper.DetectEncoding(path);
        TextBuffer.PreparedContent prepared = TextBuffer.PrepareContentFromFile(path, encoding, tabSize: 4);
        var info = new FileInfo(path);
        byte[] tail = FileHelper.ReadTailVerifyBytes(path, info.Length);
        return info.Length + prepared.Source.LineCount + tail.Length;
    }

    [Benchmark(Description = "Large file random line reads")]
    public long RandomLineReads()
    {
        long total = 0;
        foreach (int line in _sampleLines)
            total += _buffer[line].Length;
        return total;
    }

    [Benchmark(Description = "Large file small edit overlay")]
    public int SmallEditOverlay()
    {
        var copy = new TextBuffer();
        string? path = _path;
        if (path != null && File.Exists(path))
            copy.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, _encoding, tabSize: 4));
        else
            copy.SetContent("alpha\nbeta\ngamma", tabSize: 4);

        int line = Math.Min(copy.Count - 1, Math.Max(0, copy.Count / 2));
        copy.InsertAt(line, 0, "x");
        return copy[line].Length;
    }

    [Benchmark(Description = "Large file safe save rewrite")]
    public long SafeSaveRewrite()
    {
        string? path = _path;
        if (path == null || !File.Exists(path))
            return 0;

        string tempPath = Path.Combine(Path.GetTempPath(), "Volt.Benchmarks." + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var copy = new TextBuffer();
            copy.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, _encoding, tabSize: 4));
            copy.InsertAt(0, 0, "x");
            copy.SaveToFile(tempPath, _encoding, tabSize: 4);
            return new FileInfo(tempPath).Length;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static void WarmFileCache(string path)
    {
        byte[] buffer = new byte[4 * 1024 * 1024];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 20, FileOptions.SequentialScan);
        while (stream.Read(buffer, 0, buffer.Length) > 0)
        {
        }
    }

    private sealed class LoadProgressSink : IProgress<FileLoadProgress>
    {
        public int ReportCount { get; private set; }
        public double? LastPercent { get; private set; }

        public void Report(FileLoadProgress value)
        {
            ReportCount++;
            LastPercent = value.Percent;
        }
    }

    private sealed class StaBenchmarkThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = [];
        private readonly Thread _thread;

        public StaBenchmarkThread()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Volt.Benchmarks.WPF" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public T Invoke<T>(Func<T> callback)
        {
            T? result = default;
            Exception? exception = null;
            using var complete = new ManualResetEventSlim();

            _queue.Add(() =>
            {
                try
                {
                    result = callback();
                    DrainDispatcher();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                finally
                {
                    complete.Set();
                }
            });

            complete.Wait();
            if (exception != null)
                ExceptionDispatchInfo.Capture(exception).Throw();

            return result!;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        private void Run()
        {
            foreach (Action action in _queue.GetConsumingEnumerable())
                action();
        }

        private static void DrainDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
