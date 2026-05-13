using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Volt;

internal static class DiagnosticsPerformanceTrace
{
    private static readonly AsyncLocal<DiagnosticsTraceRun?> CurrentRun = new();
    private static readonly object WriteLock = new();

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("VOLT_DIAGNOSTICS_TRACE"), "1",
            StringComparison.OrdinalIgnoreCase);

    public static DiagnosticsTraceRun? Begin(
        string languageName,
        long sourceVersion,
        int lineCount,
        long charCount)
    {
        return IsEnabled
            ? new DiagnosticsTraceRun(languageName, sourceVersion, lineCount, charCount, CurrentRun.Value)
            : null;
    }

    public static void RecordSourceRead(TimeSpan elapsed, int charCount)
    {
        CurrentRun.Value?.RecordSourceRead(elapsed, charCount);
    }

    internal static void SetCurrent(DiagnosticsTraceRun? run) => CurrentRun.Value = run;

    internal static void Write(DiagnosticsTraceRun run)
    {
        try
        {
            string path = ResolveTracePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            lock (WriteLock)
            {
                File.AppendAllText(path, run.FormatLine() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            // Diagnostics tracing is opt-in instrumentation and must not affect editing.
        }
    }

    private static string ResolveTracePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("VOLT_DIAGNOSTICS_TRACE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        return Path.Combine(root, "Volt", "logs", "diagnostics.log");
    }
}

internal sealed class DiagnosticsTraceRun : IDisposable
{
    private readonly DiagnosticsTraceRun? _previous;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _sourceReadTicks;
    private int _segmentCount;
    private int _diagnosticCount;
    private bool _hasMoreDiagnostics;
    private string _state = "unknown";
    private bool _finished;

    private long? _progress10Ticks;
    private long? _progress50Ticks;
    private long? _progress100Ticks;

    public DiagnosticsTraceRun(
        string languageName,
        long sourceVersion,
        int lineCount,
        long charCount,
        DiagnosticsTraceRun? previous)
    {
        LanguageName = languageName;
        SourceVersion = sourceVersion;
        LineCount = lineCount;
        CharCount = charCount;
        _previous = previous;
        DiagnosticsPerformanceTrace.SetCurrent(this);
    }

    public string LanguageName { get; }
    public long SourceVersion { get; }
    public int LineCount { get; }
    public long CharCount { get; }

    public void RecordProgress(LanguageDiagnosticsProgress progress)
    {
        if (progress.TotalCharacters <= 0)
            return;

        long ticks = _stopwatch.ElapsedTicks;
        long processed = Math.Clamp(progress.CharactersProcessed, 0, progress.TotalCharacters);
        if (_progress10Ticks == null && processed * 100 >= progress.TotalCharacters * 10)
            _progress10Ticks = ticks;
        if (_progress50Ticks == null && processed * 100 >= progress.TotalCharacters * 50)
            _progress50Ticks = ticks;
        if (_progress100Ticks == null && processed >= progress.TotalCharacters)
            _progress100Ticks = ticks;
    }

    public void RecordSourceRead(TimeSpan elapsed, int charCount)
    {
        if (charCount <= 0)
            return;

        _segmentCount++;
        _sourceReadTicks += elapsed.Ticks;
    }

    public void Finish(string state, int diagnosticCount, bool hasMoreDiagnostics)
    {
        if (_finished)
            return;

        _finished = true;
        _state = state;
        _diagnosticCount = diagnosticCount;
        _hasMoreDiagnostics = hasMoreDiagnostics;
        _stopwatch.Stop();
        DiagnosticsPerformanceTrace.Write(this);
        DiagnosticsPerformanceTrace.SetCurrent(_previous);
    }

    public string FormatLine()
    {
        double elapsedMs = TicksToMilliseconds(_stopwatch.ElapsedTicks);
        double sourceReadMs = TimeSpan.FromTicks(_sourceReadTicks).TotalMilliseconds;
        double throughput = elapsedMs <= 0
            ? 0
            : CharCount / 1024d / 1024d / (elapsedMs / 1000d);

        return string.Join('\t',
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            $"language={LanguageName}",
            $"generation={SourceVersion}",
            $"lines={LineCount}",
            $"chars={CharCount}",
            $"state={_state}",
            $"elapsedMs={elapsedMs:F1}",
            $"diagnostics={_diagnosticCount}",
            $"hasMore={_hasMoreDiagnostics}",
            $"progress10Ms={FormatTicks(_progress10Ticks)}",
            $"progress50Ms={FormatTicks(_progress50Ticks)}",
            $"progress100Ms={FormatTicks(_progress100Ticks)}",
            $"segments={_segmentCount}",
            $"sourceReadMs={sourceReadMs:F1}",
            $"throughputMiBps={throughput:F1}");
    }

    public void Dispose()
    {
        if (!_finished)
            Finish("disposed", 0, hasMoreDiagnostics: false);
    }

    private static string FormatTicks(long? ticks) =>
        ticks.HasValue ? TicksToMilliseconds(ticks.Value).ToString("F1", CultureInfo.InvariantCulture) : "";

    private static double TicksToMilliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency;
}
