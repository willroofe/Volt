namespace TextEdit;

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

    private readonly List<UndoEntry> _undoStack = [];
    private readonly List<UndoEntry> _redoStack = [];

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;

    /// <summary>
    /// Push a region-based undo entry. Clears the redo stack.
    /// </summary>
    /// <summary>
    /// Push a region-based undo entry. Clears the redo stack.
    /// Returns true if the oldest entry was evicted due to the size cap.
    /// </summary>
    public bool Push(UndoEntry entry)
    {
        _undoStack.Add(entry);
        bool evicted = _undoStack.Count > MaxEntries;
        if (evicted)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
        return evicted;
    }

    /// <summary>
    /// Undo: pops the last entry, moves it to the redo stack, returns it.
    /// The caller applies the reverse (replaces After with Before in the buffer).
    /// </summary>
    public UndoEntry? Undo()
    {
        if (_undoStack.Count == 0) return null;
        var entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(entry);
        return entry;
    }

    /// <summary>
    /// Redo: pops the last redo entry, moves it back to the undo stack, returns it.
    /// The caller applies the forward (replaces Before with After in the buffer).
    /// </summary>
    public UndoEntry? Redo()
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
        _redoStack.Clear();
    }
}
