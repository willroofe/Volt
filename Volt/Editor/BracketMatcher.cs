namespace Volt;

/// <summary>
/// Bracket pair data and matching algorithms, extracted from EditorControl.
/// All methods are static — takes buffer + position, returns match results.
/// An optional skip predicate allows callers to exclude positions inside
/// strings or comments from bracket matching.
/// </summary>
public static class BracketMatcher
{
    public const int MaxScanLines = 500;
    private const int MaxScanChars = 50_000;

    public static readonly Dictionary<char, char> Pairs = new()
    {
        { '(', ')' },
        { '{', '}' },
        { '[', ']' },
    };

    public static readonly HashSet<char> ClosingBrackets = new(Pairs.Values);

    public static readonly Dictionary<char, char> ReversePairs =
        Pairs.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static readonly HashSet<char> AutoCloseQuotes = ['\'', '"', '`'];

    /// <param name="skip">Optional predicate: returns true for (line, col) positions to ignore (e.g. inside strings/comments).</param>
    public static (int line, int col, int matchLine, int matchCol)? FindMatch(
        ITextDocument buffer, int caretLine, int caretCol, Func<int, int, bool>? skip = null)
    {
        caretLine = Math.Clamp(caretLine, 0, Math.Max(0, buffer.Count - 1));
        caretCol = Math.Clamp(caretCol, 0, buffer[caretLine].Length);

        int[] colsToCheck = caretCol < buffer[caretLine].Length && caretCol > 0
            ? [caretCol, caretCol - 1]
            : caretCol < buffer[caretLine].Length
                ? [caretCol]
                : caretCol > 0
                    ? [caretCol - 1]
                    : [];

        foreach (int checkCol in colsToCheck)
        {
            if (skip != null && skip(caretLine, checkCol)) continue;
            char ch = buffer[caretLine][checkCol];

            if (Pairs.TryGetValue(ch, out char closer))
            {
                var match = ScanForBracket(buffer, ch, closer, caretLine, checkCol, forward: true, skip);
                if (match != null)
                    return (caretLine, checkCol, match.Value.line, match.Value.col);
            }
            else if (ReversePairs.TryGetValue(ch, out char opener))
            {
                var match = ScanForBracket(buffer, ch, opener, caretLine, checkCol, forward: false, skip);
                if (match != null)
                    return (caretLine, checkCol, match.Value.line, match.Value.col);
            }
        }

        return FindEnclosing(buffer, caretLine, caretCol, skip);
    }

    private static (int line, int col, int matchLine, int matchCol)? FindEnclosing(
        ITextDocument buffer, int caretLine, int caretCol, Func<int, int, bool>? skip)
    {
        // depths[0] = '(', depths[1] = '{', depths[2] = '['
        var depths = new int[3];

        int line = caretLine;
        int col = caretCol - 1;
        int minLine = Math.Max(0, caretLine - MaxScanLines);
        int budget = MaxScanChars;
        string curLine = buffer[line];

        while (line >= minLine)
        {
            while (col < 0 || curLine.Length == 0)
            {
                line--;
                if (line < minLine) return null;
                curLine = buffer[line];
                col = curLine.Length - 1;
            }

            if (skip == null || !skip(line, col))
            {
                char ch = curLine[col];

                if (ClosingBrackets.Contains(ch))
                {
                    var opener = ReversePairs[ch];
                    depths[BracketIndex(opener)]++;
                }
                else if (Pairs.TryGetValue(ch, out char closer))
                {
                    int bi = BracketIndex(ch);
                    depths[bi]--;
                    if (depths[bi] < 0)
                    {
                        var match = ScanForBracket(buffer, ch, closer, line, col, forward: true, skip);
                        if (match != null)
                            return (line, col, match.Value.line, match.Value.col);
                        depths[bi] = 0;
                    }
                }
            }

            col--;
            if (--budget <= 0) return null;
        }
        return null;
    }

    private static int BracketIndex(char ch) => ch switch { '(' => 0, '{' => 1, '[' => 2, _ => -1 };

    /// <summary>
    /// Scans forward from the given position to find the matching closing bracket.
    /// Used by indent guide rendering to find matching braces.
    /// </summary>
    public static (int line, int col)? ScanForward(
        ITextDocument buffer, char opener, char closer, int startLine, int startCol)
        => ScanForBracket(buffer, opener, closer, startLine, startCol, forward: true);

    /// <summary>
    /// Returns the code-context brace balance for a line: +1 per '{' and -1 per '}'
    /// that are not inside strings or comments (as determined by the skip predicate).
    /// </summary>
    public static int CodeBraceBalance(ITextDocument buffer, int line, Func<int, int, bool>? skip = null)
    {
        if (line < 0 || line >= buffer.Count) return 0;
        string text = buffer[line];
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (skip != null && skip(line, i)) continue;
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;
        }
        return depth;
    }

    /// <summary>
    /// Returns true if the line has more '{' than '}' in code context
    /// (ignoring braces inside strings and comments).
    /// </summary>
    public static bool IsBlockOpen(ITextDocument buffer, int line, Func<int, int, bool>? skip = null)
        => CodeBraceBalance(buffer, line, skip) > 0;

    /// <summary>
    /// Returns true if the line has more '}' than '{' in code context.
    /// </summary>
    public static bool IsBlockClose(ITextDocument buffer, int line, Func<int, int, bool>? skip = null)
        => CodeBraceBalance(buffer, line, skip) < 0;

    /// <summary>
    /// Finds the last unmatched code-context '{' on the given line, then scans forward
    /// to find its matching '}'. Returns the (line, col) of the closing '}'.
    /// The "last unmatched" brace is the one whose matching '}' is NOT on the same line —
    /// i.e. the brace that opens the block continuing to subsequent lines.
    /// </summary>
    public static (int line, int col)? FindBlockCloser(ITextDocument buffer, int openLine, Func<int, int, bool>? skip = null)
    {
        if (openLine < 0 || openLine >= buffer.Count) return null;
        string text = buffer[openLine];
        // Scan from right to left to find the last '{' that is unmatched on this line.
        // Track depth: '}' increments (unmatched closers), '{' decrements.
        // The first '{' we see when depth is 0 is the last unmatched opener.
        int depth = 0;
        int braceCol = -1;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (skip != null && skip(openLine, i)) continue;
            if (text[i] == '}') depth++;
            else if (text[i] == '{')
            {
                if (depth == 0) { braceCol = i; break; }
                depth--;
            }
        }
        if (braceCol < 0) return null;
        return ScanForBracket(buffer, '{', '}', openLine, braceCol, forward: true, skip);
    }

    /// <summary>
    /// Scans backward from the given position to find an enclosing unmatched '{' in code context.
    /// Returns the line number of the opener, or null if not found.
    /// Used by fold-at-caret and indent guide backward scanning.
    /// </summary>
    public static int? FindEnclosingOpenBrace(
        ITextDocument buffer, int fromLine, int fromCol, Func<int, int, bool>? skip = null)
    {
        int depth = 0;
        int line = fromLine;
        int col = fromCol - 1;
        int minLine = Math.Max(0, fromLine - MaxScanLines);
        int budget = MaxScanChars;
        string curLine = buffer[line];

        while (line >= minLine)
        {
            while (col < 0 || curLine.Length == 0)
            {
                line--;
                if (line < minLine) return null;
                curLine = buffer[line];
                col = curLine.Length - 1;
            }

            if (skip == null || !skip(line, col))
            {
                char ch = curLine[col];
                if (ch == '}') depth++;
                else if (ch == '{')
                {
                    if (depth == 0) return line;
                    depth--;
                }
            }

            col--;
            if (--budget <= 0) return null;
        }
        return null;
    }

    private static (int line, int col)? ScanForBracket(
        ITextDocument buffer, char bracket, char target, int startLine, int startCol, bool forward,
        Func<int, int, bool>? skip = null)
    {
        int depth = 1;
        int line = startLine;
        int col = startCol;
        int maxLine = Math.Min(buffer.Count - 1, startLine + MaxScanLines);
        int minLine = Math.Max(0, startLine - MaxScanLines);
        int budget = MaxScanChars;
        string curLine = buffer[line];

        while (line >= minLine && line <= maxLine)
        {
            if (forward)
            {
                col++;
                while (col >= curLine.Length)
                {
                    line++;
                    if (line > maxLine) return null;
                    curLine = buffer[line];
                    col = 0;
                }
            }
            else
            {
                col--;
                while (col < 0 || curLine.Length == 0)
                {
                    line--;
                    if (line < minLine) return null;
                    curLine = buffer[line];
                    col = curLine.Length - 1;
                }
            }

            if (skip != null && skip(line, col)) continue;

            char ch = curLine[col];
            if (ch == bracket) depth++;
            else if (ch == target) depth--;

            if (depth == 0) return (line, col);
            if (--budget <= 0) return null;
        }
        return null;
    }
}
