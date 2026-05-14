using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Volt;

internal static class VoltProfiler
{
    private const string EnvironmentVariable = "VOLT_PROFILE";
    private const string PathEnvironmentVariable = "VOLT_PROFILE_PATH";

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly double TimestampScale = 1_000_000.0 / Stopwatch.Frequency;
    private static readonly List<TraceEvent> Events = [];
    private static readonly int ProcessId = Environment.ProcessId;
    private static readonly string TracePath = ResolveTracePath();

    public static bool IsEnabled { get; } = IsTruthy(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static IDisposable Span(string name) =>
        IsEnabled ? new ProfileSpan(name, args: null) : NullSpan.Instance;

    public static IDisposable Span(string name, string argName, object? argValue) =>
        IsEnabled ? new ProfileSpan(name, new Dictionary<string, object?> { [argName] = argValue }) : NullSpan.Instance;

    public static string? Flush()
    {
        if (!IsEnabled)
            return null;

        TraceEvent[] events;
        lock (Gate)
            events = [.. Events];

        string? directory = Path.GetDirectoryName(TracePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(TracePath, CreateTraceJson(events));
        Trace.WriteLine($"Volt profile written to {TracePath}");
        return TracePath;
    }

    internal static string CreateTraceJsonForTests(IEnumerable<TraceEvent> events) =>
        CreateTraceJson(events);

    private static string CreateTraceJson(IEnumerable<TraceEvent> events)
    {
        var trace = new TraceDocument(events);
        return JsonSerializer.Serialize(trace, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static bool IsTruthy(string? value) =>
        value != null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static string ResolveTracePath()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        string fileName = $"volt-profile-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json";
        return Path.Combine(Path.GetTempPath(), "Volt", "Profiles", fileName);
    }

    private sealed class ProfileSpan : IDisposable
    {
        private readonly string _name;
        private readonly Dictionary<string, object?>? _args;
        private readonly long _startTimestamp;
        private readonly int _threadId;
        private bool _disposed;

        public ProfileSpan(string name, Dictionary<string, object?>? args)
        {
            _name = name;
            _args = args;
            _startTimestamp = Stopwatch.GetTimestamp();
            _threadId = Environment.CurrentManagedThreadId;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            long endTimestamp = Stopwatch.GetTimestamp();
            var traceEvent = new TraceEvent(
                _name,
                "Volt",
                "X",
                _startTimestamp * TimestampScale,
                Math.Max(0, endTimestamp - _startTimestamp) * TimestampScale,
                ProcessId,
                _threadId,
                _args);

            lock (Gate)
                Events.Add(traceEvent);
        }
    }

    private sealed class NullSpan : IDisposable
    {
        public static readonly NullSpan Instance = new();

        public void Dispose()
        {
        }
    }

    internal sealed record TraceEvent(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("cat")] string Category,
        [property: JsonPropertyName("ph")] string Phase,
        [property: JsonPropertyName("ts")] double TimestampMicroseconds,
        [property: JsonPropertyName("dur")] double DurationMicroseconds,
        [property: JsonPropertyName("pid")] int ProcessId,
        [property: JsonPropertyName("tid")] int ThreadId,
        [property: JsonPropertyName("args")] Dictionary<string, object?>? Args);

    private sealed record TraceDocument(
        [property: JsonPropertyName("traceEvents")] IEnumerable<TraceEvent> TraceEvents);
}
