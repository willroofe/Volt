namespace Volt;

/// <summary>
/// Manages undo/redo with region-based entries.
/// Each entry stores only the affected line range (before and after),
/// not a full copy of the entire buffer.
/// </summary>
public class UndoManager
{
    private const int MaxEntries = 200;

    public abstract record UndoEntryBase(
        int CaretLineBefore, int CaretColBefore,
        int CaretLineAfter, int CaretColAfter);

    public record UndoEntry(
        int StartLine,
        List<string> Before,
        List<string> After,
        int CaretLineBefore, int CaretColBefore,
        int CaretLineAfter, int CaretColAfter)
        : UndoEntryBase(CaretLineBefore, CaretColBefore, CaretLineAfter, CaretColAfter);

    /// <summary>
    /// Compact undo entry for multi-line indent/unindent.
    /// Stores only the number of spaces added/removed per line instead of full line copies.
    /// </summary>
    public record IndentEntry(
        int StartLine, int LineCount,
        int[] SpacesPerLine, bool IsIndent,
        int CaretLineBefore, int CaretColBefore,
        int CaretLineAfter, int CaretColAfter)
        : UndoEntryBase(CaretLineBefore, CaretColBefore, CaretLineAfter, CaretColAfter);

    private readonly LinkedList<UndoEntryBase> _undoStack = new();
    private readonly List<UndoEntryBase> _redoStack = [];

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public int UndoCount => _undoStack.Count;

    /// <summary>
    /// Push an undo entry. Clears the redo stack.
    /// Returns true if the oldest entry was evicted due to the size cap.
    /// </summary>
    public bool Push(UndoEntryBase entry)
    {
        _undoStack.AddLast(entry);
        bool evicted = _undoStack.Count > MaxEntries;
        if (evicted)
            _undoStack.RemoveFirst();
        _redoStack.Clear();
        return evicted;
    }

    /// <summary>
    /// Pop the last undo entry, move it to the redo stack.
    /// </summary>
    public UndoEntryBase? Undo()
    {
        if (_undoStack.Count == 0) return null;
        var entry = _undoStack.Last!.Value;
        _undoStack.RemoveLast();
        _redoStack.Add(entry);
        return entry;
    }

    /// <summary>
    /// Pop the last redo entry, move it back to the undo stack.
    /// </summary>
    public UndoEntryBase? Redo()
    {
        if (_redoStack.Count == 0) return null;
        var entry = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.AddLast(entry);
        return entry;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _redoStack.TrimExcess();
    }
}
