using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Volt;

public record SyntaxToken(int Start, int Length, string Scope);

/// <summary>Tracks whether a line ends inside an unclosed string, block comment, heredoc, or regex.</summary>
/// <param name="BlockCommentIndex">Index into grammar's BlockComments list, or -1 if not in a block comment.</param>
public record LineState(char? OpenQuote, int BlockCommentIndex = -1,
    string? HeredocDelimiter = null, bool HeredocInterpolate = true,
    char? OpenRegexDelimiter = null, int RegexClosesNeeded = 0);

public class SyntaxManager
{
    private const string PerlRegexModifiers = "msixpodualngcer";
    public readonly LineState DefaultState = new(null);
    private readonly string GrammarsDir = AppPaths.GrammarsDir;

    private readonly List<SyntaxDefinition> _grammars = [];
    private Dictionary<string, SyntaxDefinition> _extensionMap = new(StringComparer.OrdinalIgnoreCase);
    private bool[] _claimedBuf = [];

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EnsureDefaultGrammars();
        LoadGrammars();
    }

    public SyntaxDefinition? GetDefinition(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        var ext = extension.ToLowerInvariant();
        _extensionMap.TryGetValue(ext, out var grammar);
        return grammar;
    }

    /// <summary>Tokenize a single line (no multi-line state).</summary>
    public List<SyntaxToken> Tokenize(string line, SyntaxDefinition? grammar)
    {
        return Tokenize(line, grammar, DefaultState, out _);
    }

    /// <summary>Tokenize a line with multi-line continuation support.</summary>
    public List<SyntaxToken> Tokenize(string line, SyntaxDefinition? grammar, LineState inState, out LineState outState)
    {
        outState = DefaultState;
        if (grammar == null) return [];

        // Handle full-line continuation states (block comment, heredoc, regex)
        var result = TryTokenizeBlockComment(line, grammar, inState, ref outState);
        if (result != null) return result;

        result = TryTokenizeHeredocContinuation(line, grammar, inState, ref outState);
        if (result != null) return result;

        if (_claimedBuf.Length < line.Length)
            _claimedBuf = new bool[Math.Max(line.Length, 256)];
        else
            Array.Clear(_claimedBuf, 0, line.Length);
        var claimed = _claimedBuf;
        var tokens = new List<SyntaxToken>();
        int ruleStart = 0;

        // Handle partial-line continuations (regex close, string close)
        ruleStart = ContinueOpenRegex(line, inState, tokens, claimed, ref outState);
        ruleStart = ContinueOpenString(line, inState, tokens, claimed, ruleStart, ref outState);
        if (outState.OpenQuote != null && ruleStart == 0)
        {
            // Entire line still inside a string — return early
            if (grammar.Interpolation != null)
                tokens = ExpandInterpolation(tokens, line, grammar.Interpolation);
            return tokens;
        }

        // Apply grammar rules and claim tokens
        ApplyGrammarRules(line, grammar, ruleStart, tokens, claimed);

        // Post-rule detection: heredocs, unclaimed regexes, unclosed strings
        DetectHeredocMarker(line, grammar, tokens, claimed, ref outState);
        DetectRegexPatterns(line, tokens, claimed, ref outState);
        DetectUnclosedStringAtEOL(line, tokens, claimed, ref outState);

        if (grammar.Interpolation != null)
            tokens = ExpandInterpolation(tokens, line, grammar.Interpolation);
        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        return tokens;
    }

    private List<SyntaxToken>? TryTokenizeBlockComment(string line, SyntaxDefinition grammar, LineState inState, ref LineState outState)
    {
        var bcs = grammar.BlockComments;

        // Continue existing block comment
        if (inState.BlockCommentIndex >= 0 && bcs != null && inState.BlockCommentIndex < bcs.Count)
        {
            var bc = bcs[inState.BlockCommentIndex];
            bool endsBlock = bc.EndRegex != null && bc.EndRegex.IsMatch(line);
            outState = endsBlock ? DefaultState : inState;
            return line.Length > 0 ? [new SyntaxToken(0, line.Length, bc.Scope)] : [];
        }

        // Check if this line starts a block comment
        if (bcs != null)
        {
            for (int i = 0; i < bcs.Count; i++)
            {
                if (bcs[i].StartRegex is { } startRx && startRx.IsMatch(line))
                {
                    outState = new LineState(null, i);
                    return line.Length > 0 ? [new SyntaxToken(0, line.Length, bcs[i].Scope)] : [];
                }
            }
        }

        return null;
    }

    private List<SyntaxToken>? TryTokenizeHeredocContinuation(string line, SyntaxDefinition grammar, LineState inState, ref LineState outState)
    {
        if (inState.HeredocDelimiter == null) return null;

        bool isEnd = line.TrimStart() == inState.HeredocDelimiter;
        outState = isEnd ? DefaultState : inState;
        if (line.Length > 0)
        {
            var hdTokens = new List<SyntaxToken> { new(0, line.Length, "string") };
            if (inState.HeredocInterpolate && grammar.Interpolation != null)
                hdTokens = ExpandInterpolation(hdTokens, line, grammar.Interpolation);
            return hdTokens;
        }
        return [];
    }

    private int ContinueOpenRegex(string line, LineState inState,
        List<SyntaxToken> tokens, bool[] claimed, ref LineState outState)
    {
        if (inState.OpenRegexDelimiter is not char regexDelim) return 0;

        var (endPos, remaining) = ScanForRegexClose(line, 0, regexDelim, inState.RegexClosesNeeded);
        if (remaining > 0)
        {
            outState = new LineState(null, -1, null, true, regexDelim, remaining);
            if (line.Length > 0)
                tokens.Add(new SyntaxToken(0, line.Length, "regex"));
            for (int i = 0; i < line.Length; i++) claimed[i] = true;
            return line.Length;
        }
        while (endPos < line.Length && PerlRegexModifiers.Contains(line[endPos]))
            endPos++;
        tokens.Add(new SyntaxToken(0, endPos, "regex"));
        for (int i = 0; i < endPos; i++) claimed[i] = true;
        return endPos;
    }

    private int ContinueOpenString(string line, LineState inState,
        List<SyntaxToken> tokens, bool[] claimed, int ruleStart, ref LineState outState)
    {
        if (inState.OpenQuote is not char openQ) return ruleStart;

        int closePos = FindClosingQuote(line, 0, openQ);
        if (closePos < 0)
        {
            outState = inState;
            if (line.Length > 0)
                tokens.Add(new SyntaxToken(0, line.Length, "string"));
            return 0; // signals entire line is a string
        }
        int len = closePos + 1;
        tokens.Add(new SyntaxToken(0, len, "string"));
        for (int i = 0; i < len; i++) claimed[i] = true;
        return len;
    }

    private void ApplyGrammarRules(string line, SyntaxDefinition grammar, int ruleStart,
        List<SyntaxToken> tokens, bool[] claimed)
    {
        // Collect all candidate matches with rule priority.
        // Each rule finds non-overlapping matches (advancing past each match).
        // The greedy claiming pass resolves cross-rule overlaps by position then priority.
        var candidates = new List<(int Priority, int Start, int Length, string Scope)>();
        for (int r = 0; r < grammar.Rules.Count; r++)
        {
            var rule = grammar.Rules[r];
            if (rule.CompiledRegex == null) continue;

            try
            {
                int searchFrom = ruleStart;
                while (searchFrom < line.Length)
                {
                    var match = rule.CompiledRegex.Match(line, searchFrom);
                    if (!match.Success) break;
                    if (match.Length > 0)
                    {
                        candidates.Add((r, match.Index, match.Length, rule.Scope));
                        searchFrom = match.Index + match.Length;
                    }
                    else
                    {
                        searchFrom = match.Index + 1;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                System.Diagnostics.Debug.WriteLine($"Regex timeout in grammar rule {r} (scope '{rule.Scope}')");
            }
        }

        candidates.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Priority.CompareTo(b.Priority));

        foreach (var (_, start, length, scope) in candidates)
        {
            bool overlap = false;
            for (int i = start; i < start + length; i++)
            {
                if (claimed[i]) { overlap = true; break; }
            }
            if (overlap) continue;

            for (int i = start; i < start + length; i++)
                claimed[i] = true;
            tokens.Add(new SyntaxToken(start, length, scope));
        }
    }

    private void DetectHeredocMarker(string line, SyntaxDefinition grammar,
        List<SyntaxToken> tokens, bool[] claimed, ref LineState outState)
    {
        var hd = grammar.Heredoc;
        if (hd?.CompiledRegex == null) return;

        var hMatch = hd.CompiledRegex.Match(line);
        if (!hMatch.Success) return;

        bool hdOverlap = false;
        for (int i = hMatch.Index; i < hMatch.Index + hMatch.Length; i++)
            if (claimed[i]) { hdOverlap = true; break; }
        if (hdOverlap) return;

        for (int i = hMatch.Index; i < hMatch.Index + hMatch.Length; i++)
            claimed[i] = true;
        tokens.Add(new SyntaxToken(hMatch.Index, hMatch.Length, "string"));

        string delimiter = "";
        bool interpolate = true;
        for (int g = 1; g < hMatch.Groups.Count; g++)
        {
            if (hMatch.Groups[g].Success)
            {
                delimiter = hMatch.Groups[g].Value;
                interpolate = g != hd.NoInterpolationGroup;
                break;
            }
        }
        if (!string.IsNullOrEmpty(delimiter))
            outState = new LineState(null, -1, delimiter, interpolate);
    }

    private void DetectRegexPatterns(string line,
        List<SyntaxToken> tokens, bool[] claimed, ref LineState outState)
    {
        if (outState.HeredocDelimiter != null || outState.OpenRegexDelimiter != null) return;

        var regexMatch = DetectUnclaimedRegex(line, tokens, claimed);
        if (regexMatch == null) return;

        var (startPos, endPos, delim, closesRemaining) = regexMatch.Value;
        int end = closesRemaining > 0 ? line.Length : endPos;
        tokens.RemoveAll(t => t.Start >= startPos && t.Start < end);
        tokens.Add(new SyntaxToken(startPos, end - startPos, "regex"));
        for (int i = startPos; i < end; i++) claimed[i] = true;
        if (closesRemaining > 0)
            outState = new LineState(null, -1, null, true, delim, closesRemaining);
    }

    private void DetectUnclosedStringAtEOL(string line,
        List<SyntaxToken> tokens, bool[] claimed, ref LineState outState)
    {
        if (outState.HeredocDelimiter != null || outState.OpenRegexDelimiter != null) return;

        outState = DetectUnclosedString(line, claimed);
        if (outState.OpenQuote == null) return;

        int openPos = FindOpeningQuote(line, claimed, outState.OpenQuote.Value);
        if (openPos >= 0)
            tokens.Add(new SyntaxToken(openPos, line.Length - openPos, "string"));
    }

    /// <summary>Find closing quote in line, respecting backslash escapes.</summary>
    private static int FindClosingQuote(string line, int start, char quote)
    {
        for (int i = start; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == quote) return i;
        }
        return -1;
    }

    /// <summary>Find an unclaimed opening quote that is not closed by end of line.</summary>
    private static int FindOpeningQuote(string line, bool[] claimed, char quote)
    {
        // Scan from end of claimed region for the opening quote
        for (int i = 0; i < line.Length; i++)
        {
            if (claimed[i]) continue;
            if (line[i] == quote) return i;
        }
        return -1;
    }

    /// <summary>After tokenizing, check if an unclosed quote remains at end of line.</summary>
    private static LineState DetectUnclosedString(string line, bool[] claimed)
    {
        char? openQuote = null;
        for (int i = 0; i < line.Length; i++)
        {
            if (claimed[i]) continue;
            char c = line[i];
            if (c == '\\' && openQuote != null) { i++; continue; }
            if (openQuote == null)
            {
                if (c == '"') openQuote = c;
                // Single quote only opens a string if not preceded by a word character
                // (avoids treating apostrophes in "don't", "debugger's" as string delimiters,
                // and Perl's Foo'Bar package separator)
                else if (c == '\'' && (i == 0 || !char.IsLetterOrDigit(line[i - 1])))
                    openQuote = c;
            }
            else if (c == openQuote)
            {
                openQuote = null;
            }
        }
        return new LineState(openQuote);
    }

    /// <summary>Map opening bracket to its closing pair, or return the same char for non-paired delimiters.</summary>
    private static char RegexCloseDelimiter(char open) => open switch
    {
        '{' => '}', '[' => ']', '(' => ')', '<' => '>',
        _ => open
    };

    /// <summary>Scan for regex closing delimiter(s), respecting backslash escapes and nested paired delimiters.</summary>
    private static (int EndPos, int Remaining) ScanForRegexClose(
        string line, int start, char openDelim, int closesNeeded)
    {
        char closeDelim = RegexCloseDelimiter(openDelim);
        bool paired = openDelim != closeDelim;
        int pos = start;
        int remaining = closesNeeded;

        while (remaining > 0)
        {
            int depth = 0;
            while (pos < line.Length)
            {
                if (line[pos] == '\\' && pos + 1 < line.Length) { pos += 2; continue; }
                if (paired && line[pos] == openDelim) { depth++; pos++; continue; }
                if (line[pos] == closeDelim)
                {
                    if (depth > 0) { depth--; pos++; continue; }
                    pos++; remaining--; break;
                }
                pos++;
            }

            if (pos >= line.Length) break;

            // For paired delimiters (e.g. s{pat}{repl}), find the next opening delimiter
            if (remaining > 0 && paired)
            {
                while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
                if (pos >= line.Length || line[pos] != openDelim) break;
                pos++;
            }
        }

        return (pos, remaining);
    }

    /// <summary>
    /// Detect regex patterns not matched by grammar rules (paired delimiters like m{...},
    /// and multi-line regexes). Returns start/end positions, the opening delimiter, and
    /// how many closes remain (0 = fully closed on this line, >0 = multi-line).
    /// </summary>
    private static (int StartPos, int EndPos, char Delimiter, int ClosesRemaining)? DetectUnclaimedRegex(
        string line, List<SyntaxToken> tokens, bool[] claimed)
    {
        // After =~ or !~ operator, bare /regex/ is allowed
        foreach (var token in tokens)
        {
            if (token.Scope != "operator" || token.Start + token.Length > line.Length) continue;
            string op = line.Substring(token.Start, token.Length);
            if (op != "=~" && op != "!~") continue;

            int pos = token.Start + token.Length;
            while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
            if (pos >= line.Length || claimed[pos]) continue;

            var result = TryParseRegex(line, pos, allowBareSlash: true);
            if (result != null) return result;
        }

        // Standalone m, qr, s, tr, y at unclaimed word boundary
        for (int i = 0; i < line.Length; i++)
        {
            if (claimed[i]) continue;
            if (i > 0 && (char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == '_')) continue;

            char c = line[i];
            if (c != 'm' && c != 's' && c != 'y' && c != 't' && c != 'q') continue;

            var result = TryParseRegex(line, i, allowBareSlash: false);
            if (result != null) return result;
        }

        return null;
    }

    /// <summary>Try to parse a regex at the given position. Returns match info for both
    /// closed (paired-delimiter) and unclosed (multi-line) regexes.</summary>
    private static (int StartPos, int EndPos, char Delimiter, int ClosesRemaining)? TryParseRegex(
        string line, int pos, bool allowBareSlash)
    {
        if (pos >= line.Length) return null;

        int startPos = pos;
        char delim;
        int closesNeeded;
        int contentStart;

        char c = line[pos];
        if (c == '/' && allowBareSlash)
        {
            delim = '/'; closesNeeded = 1; contentStart = pos + 1;
        }
        else if ((c == 'm' || c == 's' || c == 'y') && pos + 1 < line.Length
                 && !char.IsLetterOrDigit(line[pos + 1]) && !char.IsWhiteSpace(line[pos + 1]))
        {
            delim = line[pos + 1];
            closesNeeded = c == 'm' ? 1 : 2;
            contentStart = pos + 2;
        }
        else if ((c == 't' || c == 'q') && pos + 2 < line.Length && line[pos + 1] == 'r'
                 && !char.IsLetterOrDigit(line[pos + 2]) && !char.IsWhiteSpace(line[pos + 2]))
        {
            delim = line[pos + 2];
            closesNeeded = c == 't' ? 2 : 1; // tr needs 2 closes, qr needs 1
            contentStart = pos + 3;
        }
        else return null;

        var (endPos, remaining) = ScanForRegexClose(line, contentStart, delim, closesNeeded);

        // For closed regexes, only return if grammar rules couldn't handle it
        // (paired delimiters like {}, [], (), <>)
        if (remaining == 0)
        {
            bool paired = delim != RegexCloseDelimiter(delim);
            if (!paired) return null; // grammar rules handle non-paired single-line regexes

            // Scan for trailing flags
            while (endPos < line.Length && PerlRegexModifiers.Contains(line[endPos]))
                endPos++;
        }

        return (startPos, endPos, delim, remaining);
    }

    private static List<SyntaxToken> ExpandInterpolation(List<SyntaxToken> tokens, string line, InterpolationDef interp)
    {
        var result = new List<SyntaxToken>(tokens.Count);
        foreach (var token in tokens)
        {
            if (token.Scope != "string" || token.Length < 2)
            {
                result.Add(token);
                continue;
            }

            // Only expand strings whose opening quote is in the interpolation set
            char quote = line[token.Start];
            if (!interp.Quotes.Contains(quote))
            {
                result.Add(token);
                continue;
            }

            // Find interpolated variables and escape sequences within the string (skip the quotes)
            int innerStart = token.Start + 1;
            int innerEnd = token.Start + token.Length - 1;
            string inner = line[innerStart..innerEnd];

            // Collect all sub-tokens (variables and escapes) sorted by position
            var subTokens = new List<(int Start, int Length, string Scope)>();

            if (interp.VariableRegex != null)
                foreach (Match m in interp.VariableRegex.Matches(inner))
                    subTokens.Add((innerStart + m.Index, m.Length, "variable"));

            if (interp.EscapeRegex != null)
                foreach (Match m in interp.EscapeRegex.Matches(inner))
                    subTokens.Add((innerStart + m.Index, m.Length, "escape"));

            if (subTokens.Count == 0)
            {
                result.Add(token);
                continue;
            }

            subTokens.Sort((a, b) => a.Start.CompareTo(b.Start));

            // Remove overlapping sub-tokens (first one wins)
            var filtered = new List<(int Start, int Length, string Scope)>();
            int lastEnd = 0;
            foreach (var st in subTokens)
            {
                if (st.Start < lastEnd) continue;
                filtered.Add(st);
                lastEnd = st.Start + st.Length;
            }

            // Split the string token around sub-tokens
            int pos = token.Start;
            foreach (var (start, length, scope) in filtered)
            {
                if (start > pos)
                    result.Add(new SyntaxToken(pos, start - pos, "string"));
                result.Add(new SyntaxToken(start, length, scope));
                pos = start + length;
            }
            // Remaining string portion
            int tokenEnd = token.Start + token.Length;
            if (pos < tokenEnd)
                result.Add(new SyntaxToken(pos, tokenEnd - pos, "string"));
        }
        return result;
    }

    public void ReloadGrammars()
    {
        _grammars.Clear();
        _extensionMap.Clear();
        LoadGrammars();
    }

    private void LoadGrammars()
    {
        if (!Directory.Exists(GrammarsDir)) return;
        foreach (var file in Directory.GetFiles(GrammarsDir, "*.json"))
        {
            var def = SyntaxDefinition.LoadFromFile(file);
            if (def != null) _grammars.Add(def);
        }
        RebuildExtensionMap();
    }

    private void RebuildExtensionMap()
    {
        _extensionMap = new Dictionary<string, SyntaxDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var grammar in _grammars)
            foreach (var ext in grammar.Extensions)
                _extensionMap.TryAdd(ext, grammar);
    }

    private void EnsureDefaultGrammars()
    {
        try
        {
            EmbeddedResourceHelper.ExtractAll("Volt.Resources.Grammars.", GrammarsDir);
        }
        catch (IOException ex) { System.Diagnostics.Debug.WriteLine($"Failed to extract default grammars: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine($"Failed to extract default grammars: {ex.Message}"); }
    }
}
