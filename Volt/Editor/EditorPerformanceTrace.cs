using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace Volt;

internal sealed class EditorPerformanceTrace
{
    public static EditorPerformanceTrace Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Stopwatch _interval = Stopwatch.StartNew();
    private readonly string _logPath;

    private int _frameRequests;
    private int _frameRequestCoalesces;
    private int _frames;
    private int _onRenderCalls;
    private int _textRebuilds;
    private int _gutterRebuilds;
    private int _decorationRebuilds;
    private int _caretRebuilds;
    private int _scrollOffsetUpdates;
    private int _dragMoves;
    private int _dragCoalesces;

    private double _frameMs;
    private double _onRenderMs;
    private double _textMs;
    private double _gutterMs;
    private double _decorationMs;
    private double _caretMs;

    private EditorPerformanceTrace()
    {
        Enabled = Environment.GetEnvironmentVariable("VOLT_EDITOR_PERF") == "1";
        _logPath = Path.Combine(Path.GetTempPath(), $"Volt-editor-perf-{Environment.ProcessId}.log");

        if (Enabled)
        {
            Log($"Volt editor perf enabled. WPF render tier={RenderCapability.Tier >> 16}. Log={_logPath}");
        }
    }

    public bool Enabled { get; }

    public static double ElapsedMilliseconds(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    public void RecordFrameRequest(bool coalesced)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            _frameRequests++;
            if (coalesced) _frameRequestCoalesces++;
            FlushIfDue();
        }
    }

    public void RecordScrollOffsetUpdate()
    {
        if (!Enabled) return;
        lock (_gate)
        {
            _scrollOffsetUpdates++;
            FlushIfDue();
        }
    }

    public void RecordDragMove(bool coalesced)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            _dragMoves++;
            if (coalesced) _dragCoalesces++;
            FlushIfDue();
        }
    }

    public void RecordOnRender(double elapsedMs)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            _onRenderCalls++;
            _onRenderMs += elapsedMs;
            FlushIfDue();
        }
    }

    public void RecordFrame(
        double elapsedMs,
        bool rebuiltText,
        bool rebuiltGutter,
        bool rebuiltDecorations,
        bool rebuiltCaret,
        double textMs,
        double gutterMs,
        double decorationMs,
        double caretMs)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            _frames++;
            _frameMs += elapsedMs;
            if (rebuiltText)
            {
                _textRebuilds++;
                _textMs += textMs;
            }
            if (rebuiltGutter)
            {
                _gutterRebuilds++;
                _gutterMs += gutterMs;
            }
            if (rebuiltDecorations)
            {
                _decorationRebuilds++;
                _decorationMs += decorationMs;
            }
            if (rebuiltCaret)
            {
                _caretRebuilds++;
                _caretMs += caretMs;
            }
            FlushIfDue();
        }
    }

    private void FlushIfDue()
    {
        if (_interval.ElapsedMilliseconds < 1000) return;

        string message =
            $"frames={_frames} avgFrame={Average(_frameMs, _frames):F2}ms " +
            $"onRender={_onRenderCalls} avgOnRender={Average(_onRenderMs, _onRenderCalls):F2}ms " +
            $"text={_textRebuilds} avgText={Average(_textMs, _textRebuilds):F2}ms " +
            $"gutter={_gutterRebuilds} avgGutter={Average(_gutterMs, _gutterRebuilds):F2}ms " +
            $"decor={_decorationRebuilds} avgDecor={Average(_decorationMs, _decorationRebuilds):F2}ms " +
            $"caret={_caretRebuilds} avgCaret={Average(_caretMs, _caretRebuilds):F2}ms " +
            $"scrollOffsets={_scrollOffsetUpdates} frameRequests={_frameRequests} " +
            $"requestCoalesces={_frameRequestCoalesces} dragMoves={_dragMoves} dragCoalesces={_dragCoalesces}";

        Reset();
        Log(message);
    }

    private void Reset()
    {
        _interval.Restart();
        _frameRequests = 0;
        _frameRequestCoalesces = 0;
        _frames = 0;
        _onRenderCalls = 0;
        _textRebuilds = 0;
        _gutterRebuilds = 0;
        _decorationRebuilds = 0;
        _caretRebuilds = 0;
        _scrollOffsetUpdates = 0;
        _dragMoves = 0;
        _dragCoalesces = 0;
        _frameMs = 0;
        _onRenderMs = 0;
        _textMs = 0;
        _gutterMs = 0;
        _decorationMs = 0;
        _caretMs = 0;
    }

    private void Log(string message)
    {
        string line = $"{DateTimeOffset.Now:O} {message}";
        Trace.WriteLine(line);
        try
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static double Average(double totalMs, int count) => count == 0 ? 0 : totalMs / count;
}
