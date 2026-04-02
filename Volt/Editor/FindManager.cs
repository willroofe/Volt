namespace Volt;

/// <summary>
/// Manages find/replace match state: search, navigation, and match data.
/// Rendering of highlights and caret navigation remain in EditorControl.
/// </summary>
public class FindManager
{
    private List<(int Line, int Col, int Length)> _matches = [];
    private int _currentIndex = -1;

    public int MatchCount => _matches.Count;
    public int CurrentIndex => _currentIndex;
    public IReadOnlyList<(int Line, int Col, int Length)> Matches => _matches;

    public void Search(TextBuffer buffer, string query, bool matchCase, int caretLine, int caretCol)
    {
        _matches.Clear();
        _currentIndex = -1;

        if (string.IsNullOrEmpty(query)) return;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int line = 0; line < buffer.Count; line++)
        {
            int pos = 0;
            while (pos < buffer[line].Length)
            {
                int idx = buffer[line].IndexOf(query, pos, comparison);
                if (idx < 0) break;
                _matches.Add((line, idx, query.Length));
                pos = idx + 1;
            }
        }

        if (_matches.Count > 0)
        {
            _currentIndex = 0;
            for (int i = 0; i < _matches.Count; i++)
            {
                var (ml, mc, _) = _matches[i];
                if (ml > caretLine || (ml == caretLine && mc >= caretCol))
                {
                    _currentIndex = i;
                    break;
                }
            }
        }
    }

    public void Clear(bool trimExcess = false)
    {
        _matches.Clear();
        if (trimExcess) _matches.TrimExcess();
        _currentIndex = -1;
    }

    public void MoveNext()
    {
        if (_matches.Count == 0) return;
        _currentIndex = (_currentIndex + 1) % _matches.Count;
    }

    public void MovePrevious()
    {
        if (_matches.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _matches.Count) % _matches.Count;
    }

    public (int Line, int Col, int Length)? GetCurrentMatch()
    {
        if (_currentIndex < 0 || _currentIndex >= _matches.Count) return null;
        return _matches[_currentIndex];
    }

    /// <summary>
    /// Get the first and last line spanned by all matches (for undo scope in ReplaceAll).
    /// Returns null if no matches.
    /// </summary>
    public (int FirstLine, int LastLine)? GetMatchLineRange()
    {
        if (_matches.Count == 0) return null;
        return (_matches[0].Line, _matches[^1].Line);
    }

    /// <summary>
    /// Replace matches in reverse order (for ReplaceAll).
    /// Returns the match list for reverse iteration by the caller.
    /// </summary>
    public IReadOnlyList<(int Line, int Col, int Length)> GetMatchesReversed()
    {
        return _matches;
    }
}
