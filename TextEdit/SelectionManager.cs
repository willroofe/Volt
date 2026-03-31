namespace TextEdit;

/// <summary>
/// Manages text selection state: anchor position, selection operations,
/// and selected text extraction/deletion.
/// </summary>
public class SelectionManager
{
    public int AnchorLine { get; set; }
    public int AnchorCol { get; set; }
    public bool HasSelection { get; set; }

    public void Clear() => HasSelection = false;

    public void Start(int caretLine, int caretCol)
    {
        if (HasSelection) return;
        AnchorLine = caretLine;
        AnchorCol = caretCol;
        HasSelection = true;
    }

    public void SetAnchor(int line, int col)
    {
        AnchorLine = line;
        AnchorCol = col;
    }

    public (int startLine, int startCol, int endLine, int endCol) GetOrdered(int caretLine, int caretCol)
    {
        if (AnchorLine < caretLine || (AnchorLine == caretLine && AnchorCol < caretCol))
            return (AnchorLine, AnchorCol, caretLine, caretCol);
        return (caretLine, caretCol, AnchorLine, AnchorCol);
    }

    public string GetSelectedText(TextBuffer buffer, int caretLine, int caretCol)
    {
        if (!HasSelection) return "";
        var (sl, sc, el, ec) = GetOrdered(caretLine, caretCol);
        if (sl == el)
            return buffer[sl].Substring(sc, ec - sc);

        var parts = new List<string>();
        parts.Add(buffer[sl][sc..]);
        for (int i = sl + 1; i < el; i++)
            parts.Add(buffer[i]);
        parts.Add(buffer[el][..ec]);
        return string.Join(Environment.NewLine, parts);
    }

    /// <summary>
    /// Delete the selected text from the buffer.
    /// Returns the new caret position (line, col).
    /// </summary>
    public (int line, int col) DeleteSelection(TextBuffer buffer, int caretLine, int caretCol)
    {
        if (!HasSelection) return (caretLine, caretCol);
        var (sl, sc, el, ec) = GetOrdered(caretLine, caretCol);
        if (sl == el)
        {
            buffer.NotifyLineChanging(sl);
            buffer[sl] = buffer[sl][..sc] + buffer[sl][ec..];
        }
        else
        {
            buffer[sl] = buffer[sl][..sc] + buffer[el][ec..];
            buffer.RemoveRange(sl + 1, el - sl);
        }
        HasSelection = false;
        return (sl, sc);
    }
}
