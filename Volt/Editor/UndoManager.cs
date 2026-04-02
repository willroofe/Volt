namespace Volt;

/// <summary>
/// Manages undo/redo with region-based entries.
/// Each entry stores only the affected line range (before and after),
/// not a full copy of the entire buffer.
/// </summary>
public class UndoManager
{
    private const int MaxEntries = 200;

    public record UndoEntry(
        int StartLine,
        List<string> Before,
        List<string> After,
        int CaretLineBefore, int CaretColBefore,
        int CaretLineAfter, int CaretColAfter);

    /// <summary>
    /// Compact undo entry for multi-line indent/unindent.
    /// Stores only the number of spaces added/removed per line instead of full line copies.
    /// </summary>
    public record IndentEntry(
        int StartLine, int LineCount,
        int[] SpacesPerLine, bool IsIndent,
        int CaretLineBefore, int CaretColBefore,
        int CaretLineAfter, int CaretColAfter);

    private readonly List<object> _undoStack = [];
    private readonly List<object> _redoStack = [];

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;

    /// <summary>
    /// Push a region-based undo entry. Clears the redo stack.
    /// Returns true if the oldest entry was evicted due to the size cap.
    /// </summary>
    public bool Push(UndoEntry entry) => PushInternal(entry);

    public bool Push(IndentEntry entry) => PushInternal(entry);

    private bool PushInternal(object entry)
    {
        _undoStack.Add(entry);
        bool evicted = _undoStack.Count > MaxEntries;
        if (evicted)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
        return evicted;
    }

    /// <summary>
    /// Pop the last undo entry, move it to the redo stack.
    /// Returns UndoEntry or IndentEntry (caller checks type).
    /// </summary>
    public object? Undo()
    {
        if (_undoStack.Count == 0) return null;
        var entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(entry);
        return entry;
    }

    /// <summary>
    /// Pop the last redo entry, move it back to the undo stack.
    /// Returns UndoEntry or IndentEntry (caller checks type).
    /// </summary>
    public object? Redo()
    {
        if (_redoStack.Count == 0) return null;
        var entry = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(entry);
        return entry;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _undoStack.TrimExcess();
        _redoStack.Clear();
        _redoStack.TrimExcess();
    }
}
