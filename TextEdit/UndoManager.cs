namespace TextEdit;

/// <summary>
/// Manages undo/redo with full-snapshot-based entries.
/// Each entry stores a complete copy of the text buffer and caret position.
/// </summary>
public class UndoManager
{
    private const int MaxEntries = 200;

    public record UndoEntry(List<string> Snapshot, int CaretLine, int CaretCol);

    private readonly List<UndoEntry> _undoStack = [];
    private readonly List<UndoEntry> _redoStack = [];

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// Push the current state onto the undo stack (call before making changes).
    /// Clears the redo stack.
    /// </summary>
    public void Push(List<string> snapshot, int caretLine, int caretCol)
    {
        _undoStack.Add(new UndoEntry(snapshot, caretLine, caretCol));
        if (_undoStack.Count > MaxEntries)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    /// <summary>
    /// Undo: saves current state to redo stack, returns the previous state.
    /// Returns null if nothing to undo.
    /// </summary>
    public UndoEntry? Undo(List<string> currentSnapshot, int caretLine, int caretCol)
    {
        if (_undoStack.Count == 0) return null;
        _redoStack.Add(new UndoEntry(currentSnapshot, caretLine, caretCol));
        var entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        return entry;
    }

    /// <summary>
    /// Redo: saves current state to undo stack, returns the next state.
    /// Returns null if nothing to redo.
    /// </summary>
    public UndoEntry? Redo(List<string> currentSnapshot, int caretLine, int caretCol)
    {
        if (_redoStack.Count == 0) return null;
        _undoStack.Add(new UndoEntry(currentSnapshot, caretLine, caretCol));
        var entry = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        return entry;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
