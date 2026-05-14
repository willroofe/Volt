using System.Text.Json;
using Volt;
using Xunit;

namespace Volt.Tests;

public class VoltProfilerTests
{
    [Fact]
    public void CreateTraceJsonForTests_WritesChromeTraceEventDocument()
    {
        var traceEvent = new VoltProfiler.TraceEvent(
            "Editor.OnRender",
            "Volt",
            "X",
            TimestampMicroseconds: 10,
            DurationMicroseconds: 5,
            ProcessId: 1,
            ThreadId: 2,
            new Dictionary<string, object?> { ["file"] = "test-5mb.json" });

        string json = VoltProfiler.CreateTraceJsonForTests([traceEvent]);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement events = document.RootElement.GetProperty("traceEvents");
        JsonElement first = events[0];
        Assert.Equal("Editor.OnRender", first.GetProperty("name").GetString());
        Assert.Equal("X", first.GetProperty("ph").GetString());
        Assert.Equal(5, first.GetProperty("dur").GetDouble());
        Assert.Equal("test-5mb.json", first.GetProperty("args").GetProperty("file").GetString());
    }
}
