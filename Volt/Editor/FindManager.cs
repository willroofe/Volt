using System.Text.RegularExpressions;

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
    public string LastQuery { get; private set; } = "";
    public bool LastMatchCase { get; private set; }
    public bool LastUseRegex { get; private set; }
    public bool LastWholeWord { get; private set; }

    public void Search(ITextDocument buffer, string query, bool matchCase, int caretLine, int caretCol,
        bool useRegex = false, bool wholeWord = false,
        (int startLine, int startCol, int endLine, int endCol)? selectionBounds = null)
    {
        LastQuery = query;
        LastMatchCase = matchCase;
        LastUseRegex = useRegex;
        LastWholeWord = wholeWord;
        _matches.Clear();
        _currentIndex = -1;

        if (string.IsNullOrEmpty(query)) return;

        if (useRegex || wholeWord)
        {
            var pattern = useRegex ? query : Regex.Escape(query);
            if (wholeWord) pattern = @"\b" + pattern + @"\b";
            SearchRegex(buffer, pattern, matchCase);
        }
        else
        {
            SearchLiteral(buffer, query, matchCase);
        }

        if (selectionBounds is var (sl, sc, el, ec))
            FilterToSelection(sl, sc, el, ec);

        if (_matches.Count > 0)
        {
            int lo = 0, hi = _matches.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var (ml, mc, _) = _matches[mid];
                if (ml < caretLine || (ml == caretLine && mc < caretCol))
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }
            _currentIndex = lo < _matches.Count ? lo : 0;
        }
    }

    private void FilterToSelection(int sl, int sc, int el, int ec)
    {
        _matches.RemoveAll(m =>
        {
            int matchEnd = m.Col + m.Length;
            // Match must start at or after selection start
            if (m.Line < sl || (m.Line == sl && m.Col < sc)) return true;
            // Match must end at or before selection end
            if (m.Line > el || (m.Line == el && matchEnd > ec)) return true;
            return false;
        });
    }

    private void SearchLiteral(ITextDocument buffer, string query, bool matchCase)
    {
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
    }

    private void SearchRegex(ITextDocument buffer, string query, bool matchCase)
    {
        Regex regex;
        try
        {
            var options = RegexOptions.None;
            if (!matchCase) options |= RegexOptions.IgnoreCase;
            regex = new Regex(query, options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return; // Invalid regex pattern — show no matches
        }

        for (int line = 0; line < buffer.Count; line++)
        {
            var text = buffer[line];
            var match = regex.Match(text);
            while (match.Success)
            {
                if (match.Length == 0)
                {
                    // Zero-length match (e.g. ^, $, lookahead) — skip to avoid infinite loop
                    match = match.NextMatch();
                    continue;
                }
                _matches.Add((line, match.Index, match.Length));
                match = match.NextMatch();
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

}
