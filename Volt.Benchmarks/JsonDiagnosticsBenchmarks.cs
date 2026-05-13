using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using Volt;

namespace Volt.Benchmarks;

[MemoryDiagnoser]
public class JsonDiagnosticsBenchmarks
{
    private JsonLanguageService _service = null!;
    private TextBuffer.LineSnapshot _snapshot = null!;

    [GlobalSetup]
    public void Setup()
    {
        _service = new JsonLanguageService();
        string path = ResolveLargeJsonPath();
        var buffer = new TextBuffer();
        if (File.Exists(path))
        {
            WarmFileCache(path);
            buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));
        }
        else
        {
            buffer.SetContent("""{ "message": "set VOLT_JSON_DIAGNOSTICS_BENCH_PATH to benchmark a large JSON file" }""",
                tabSize: 4);
        }

        _snapshot = buffer.SnapshotLines(0, buffer.Count);
    }

    [Benchmark(Description = "JSON diagnostics full scan")]
    public int AnalyzeDiagnosticsFullScan()
    {
        using DiagnosticsTraceRun? trace = DiagnosticsPerformanceTrace.Begin(
            "JSON",
            sourceVersion: 1,
            _snapshot.LineCount,
            _snapshot.CharCountWithoutLineEndings);
        IProgress<LanguageDiagnosticsProgress>? progress = trace == null ? null : new TraceProgress(trace);
        LanguageDiagnosticsSnapshot snapshot = _service.AnalyzeDiagnostics(
            _snapshot,
            sourceVersion: 1,
            progress,
            CancellationToken.None);
        trace?.Finish("completed", snapshot.Diagnostics.Count, snapshot.HasMoreDiagnostics);
        return snapshot.Diagnostics.Count + (snapshot.HasMoreDiagnostics ? 1 : 0);
    }

    private static string ResolveLargeJsonPath()
    {
        string envPath = Environment.GetEnvironmentVariable("VOLT_JSON_DIAGNOSTICS_BENCH_PATH") ?? "";
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        string currentDirectoryPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
            "test-files", "test-1gb.json"));
        if (File.Exists(currentDirectoryPath))
            return currentDirectoryPath;

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "test-files", "test-1gb.json"));
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

    private sealed class TraceProgress : IProgress<LanguageDiagnosticsProgress>
    {
        private readonly DiagnosticsTraceRun _trace;

        public TraceProgress(DiagnosticsTraceRun trace)
        {
            _trace = trace;
        }

        public void Report(LanguageDiagnosticsProgress value) => _trace.RecordProgress(value);
    }
}
