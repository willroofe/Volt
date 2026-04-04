namespace Volt;

/// <summary>
/// Bracket pair data and matching algorithms, extracted from EditorControl.
/// All methods are static — takes buffer + position, returns match results.
/// </summary>
public static class BracketMatcher
{
    public const int MaxScanLines = 500;

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

    public static (int line, int col, int matchLine, int matchCol)? FindMatch(
        TextBuffer buffer, int caretLine, int caretCol)
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
            char ch = buffer[caretLine][checkCol];

            if (Pairs.TryGetValue(ch, out char closer))
            {
                var match = ScanForBracket(buffer, ch, closer, caretLine, checkCol, forward: true);
                if (match != null)
                    return (caretLine, checkCol, match.Value.line, match.Value.col);
            }
            else if (ReversePairs.TryGetValue(ch, out char opener))
            {
                var match = ScanForBracket(buffer, ch, opener, caretLine, checkCol, forward: false);
                if (match != null)
                    return (caretLine, checkCol, match.Value.line, match.Value.col);
            }
        }

        return FindEnclosing(buffer, caretLine, caretCol);
    }

    private static (int line, int col, int matchLine, int matchCol)? FindEnclosing(
        TextBuffer buffer, int caretLine, int caretCol)
    {
        // depths[0] = '(', depths[1] = '{', depths[2] = '['
        var depths = new int[3];

        int line = caretLine;
        int col = caretCol - 1;
        int minLine = Math.Max(0, caretLine - MaxScanLines);

        while (line >= minLine)
        {
            while (col < 0 || buffer[line].Length == 0)
            {
                line--;
                if (line < minLine) return null;
                col = buffer[line].Length - 1;
            }

            char ch = buffer[line][col];

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
                    var match = ScanForBracket(buffer, ch, closer, line, col, forward: true);
                    if (match != null)
                        return (line, col, match.Value.line, match.Value.col);
                    depths[bi] = 0;
                }
            }

            col--;
        }
        return null;
    }

    private static int BracketIndex(char ch) => ch switch { '(' => 0, '{' => 1, '[' => 2, _ => -1 };

    private static (int line, int col)? ScanForBracket(
        TextBuffer buffer, char bracket, char target, int startLine, int startCol, bool forward)
    {
        int depth = 1;
        int line = startLine;
        int col = startCol;
        int maxLine = Math.Min(buffer.Count - 1, startLine + MaxScanLines);
        int minLine = Math.Max(0, startLine - MaxScanLines);

        while (line >= minLine && line <= maxLine)
        {
            if (forward)
            {
                col++;
                while (col >= buffer[line].Length)
                {
                    line++;
                    if (line > maxLine) return null;
                    col = 0;
                }
            }
            else
            {
                col--;
                while (col < 0 || buffer[line].Length == 0)
                {
                    line--;
                    if (line < minLine) return null;
                    col = buffer[line].Length - 1;
                }
            }

            char ch = buffer[line][col];
            if (ch == bracket) depth++;
            else if (ch == target) depth--;

            if (depth == 0) return (line, col);
        }
        return null;
    }
}
