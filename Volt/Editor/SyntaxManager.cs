using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Volt;

public record SyntaxToken(int Start, int Length, string Scope);

/// <summary>Tracks whether a line ends inside an unclosed string, block comment, heredoc, regex, or embedded region.</summary>
/// <param name="BlockCommentIndex">Index into grammar's BlockComments list, or -1 if not in a block comment.</param>
public record LineState(char? OpenQuote, int BlockCommentIndex = -1,
    string? HeredocDelimiter = null, bool HeredocInterpolate = true,
    char? OpenRegexDelimiter = null, int RegexClosesNeeded = 0,
    bool HeredocIndented = false,
    int EmbeddedRegionIndex = -1, LineState? EmbeddedState = null);

public class SyntaxManager
{
    private const string PerlRegexModifiers = "msixpodualngcer";
    public readonly LineState DefaultState = new(null);
    private readonly string GrammarsDir = AppPaths.GrammarsDir;

    private readonly List<SyntaxDefinition> _grammars = [];
    private Dictionary<string, SyntaxDefinition> _extensionMap = new(StringComparer.OrdinalIgnoreCase);
    [ThreadStatic] private static bool[]? _claimedBuf;
    [ThreadStatic] private static List<(int Priority, int Start, int Length, string Scope)>? _candidatesBuf;

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EnsureDefaultGrammars();
        LoadGrammars();
    }

    private static readonly Dictionary<string, string> _extensionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [".volt-workspace"] = ".json",
    };

    public SyntaxDefinition? GetDefinition(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        var ext = extension.ToLowerInvariant();
        if (!_extensionMap.TryGetValue(ext, out var grammar) && _extensionAliases.TryGetValue(ext, out var alias))
            _extensionMap.TryGetValue(alias, out grammar);
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

        // Handle full-line continuation states (heredoc)
        var result = TryTokenizeHeredocContinuation(line, grammar, inState, ref outState);
        if (result != null) return result;

        result = TryTokenizeEmbeddedContinuation(line, grammar, inState, ref outState);
        if (result != null) return result;

        _claimedBuf ??= new bool[Math.Max(line.Length, 256)];
        if (_claimedBuf!.Length < line.Length)
            _claimedBuf = new bool[Math.Max(line.Length, 256)];
        else
            Array.Clear(_claimedBuf, 0, line.Length);
        var claimed = _claimedBuf;
        var tokens = new List<SyntaxToken>(8);
        int ruleStart = 0;

        // Handle block comment continuation (may end mid-line, leaving rest for normal tokenization)
        ruleStart = ContinueBlockComment(line, grammar, inState, tokens, claimed, ref outState);
        if (ruleStart < 0) return tokens; // entire line is inside a block comment

        // Handle partial-line continuations (regex close, string close)
        int regexEnd = ContinueOpenRegex(line, inState, tokens, claimed, ref outState);
        if (regexEnd > ruleStart) ruleStart = regexEnd;
        ruleStart = ContinueOpenString(line, inState, tokens, claimed, ruleStart, ref outState);
        if (outState.OpenQuote != null && ruleStart == 0)
        {
            // Entire line still inside a string — return early
            if (grammar.Interpolation != null)
                tokens = ExpandInterpolation(tokens, line, grammar.Interpolation);
            return tokens;
        }

        // Check if this line starts a block comment
        if (DetectBlockCommentStart(line, grammar, tokens, claimed, ref outState))
            return tokens;

        // Apply grammar rules and claim tokens
        ApplyGrammarRules(line, grammar, ruleStart, tokens, claimed);

        // Post-rule detection: heredocs, unclaimed regexes, embedded regions, unclosed strings
        DetectHeredocMarker(line, grammar, tokens, claimed, ref outState);
        DetectRegexPatterns(line, tokens, claimed, ref outState);

        if (grammar.Interpolation != null)
            tokens = ExpandInterpolation(tokens, line, grammar.Interpolation);
        tokens = ExpandEmbeddedRegions(tokens, line, grammar, ref outState);
        DetectUnclosedStringAtEOL(line, tokens, claimed, ref outState);
        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        return tokens;
    }

    private List<SyntaxToken>? TryTokenizeEmbeddedContinuation(string line, SyntaxDefinition grammar,
        LineState inState, ref LineState outState)
    {
        var regions = grammar.EmbeddedRegions;
        if (inState.EmbeddedRegionIndex < 0 || regions == null || inState.EmbeddedRegionIndex >= regions.Count)
            return null;

        var region = regions[inState.EmbeddedRegionIndex];
        var embeddedGrammar = GetDefinition(region.Extension);
        if (embeddedGrammar == null || region.EndRegex == null)
            return [];

        var endMatch = region.EndRegex.Match(line);
        if (!endMatch.Success)
        {
            var tokens = TokenizeEmbeddedSegment(line, embeddedGrammar,
                inState.EmbeddedState ?? DefaultState,
                out var embeddedOutState, 0);
            outState = inState with { EmbeddedState = embeddedOutState };
            return tokens;
        }

        string embeddedText = line[..endMatch.Index];
        var result = TokenizeEmbeddedSegment(embeddedText, embeddedGrammar,
            inState.EmbeddedState ?? DefaultState, out _, 0);

        string tail = line[endMatch.Index..];
        if (tail.Length > 0)
        {
            var tailTokens = Tokenize(tail, grammar, DefaultState, out outState);
            result.AddRange(ShiftTokens(tailTokens, endMatch.Index));
        }
        else
        {
            outState = DefaultState;
        }

        return result;
    }

    /// <summary>
    /// Handles block comment continuation. Returns the position where normal tokenization
    /// should start, or -1 if the entire line is inside a block comment.
    /// </summary>
    private int ContinueBlockComment(string line, SyntaxDefinition grammar, LineState inState,
        List<SyntaxToken> tokens, bool[] claimed, ref LineState outState)
    {
        var bcs = grammar.BlockComments;
        if (inState.BlockCommentIndex < 0 || bcs == null || inState.BlockCommentIndex >= bcs.Count)
            return 0;

        var bc = bcs[inState.BlockCommentIndex];
        if (bc.EndRegex != null)
        {
            var endMatch = bc.EndRegex.Match(line);
            if (endMatch.Success)
            {
                outState = DefaultState;
                int end = endMatch.Index + endMatch.Length;
                if (end > 0)
                {
                    tokens.Add(new SyntaxToken(0, end, bc.Scope));
                    for (int i = 0; i < end && i < line.Length; i++) claimed[i] = true;
                }
                return end; // rest of line gets normal tokenization
            }
        }

        // No end found — entire line is still in block comment
        outState = inState;
        if (line.Length > 0)
            tokens.Add(new SyntaxToken(0, line.Length, bc.Scope));
        return -1;
    }

    /// <summary>
    /// Checks if this line starts a new block comment. If so, marks the rest of the line
    /// as comment and sets the block comment state. Returns true if the line was consumed.
    /// </summary>
    private static bool DetectBlockCommentStart(string line, SyntaxDefinition grammar,
        List<SyntaxToken> tokens, bool[] claimed, ref LineState outState)
    {
        var bcs = grammar.BlockComments;
        if (bcs == null) return false;

        for (int i = 0; i < bcs.Count; i++)
        {
            if (bcs[i].StartRegex is not { } startRx) continue;
            var startMatch = startRx.Match(line);
            if (!startMatch.Success) continue;

            // Skip if the match overlaps already-claimed characters (e.g. =cut ending a POD block)
            bool overlap = false;
            for (int j = startMatch.Index; j < startMatch.Index + startMatch.Length; j++)
                if (claimed[j]) { overlap = true; break; }
            if (overlap) continue;

            outState = new LineState(null, i);
            if (line.Length > 0)
            {
                tokens.Add(new SyntaxToken(0, line.Length, bcs[i].Scope));
                for (int j = 0; j < line.Length; j++) claimed[j] = true;
            }
            return true;
        }
        return false;
    }

    private List<SyntaxToken>? TryTokenizeHeredocContinuation(string line, SyntaxDefinition grammar, LineState inState, ref LineState outState)
    {
        if (inState.HeredocDelimiter == null) return null;

        bool isEnd = inState.HeredocIndented
            ? line.TrimStart() == inState.HeredocDelimiter
            : line == inState.HeredocDelimiter;
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
        _candidatesBuf ??= new List<(int Priority, int Start, int Length, string Scope)>();
        _candidatesBuf.Clear();
        var candidates = _candidatesBuf;
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
            catch (RegexMatchTimeoutException) { }
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
        {
            bool indented = hMatch.Value.Contains('~');
            outState = new LineState(null, -1, delimiter, interpolate, HeredocIndented: indented);
        }
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
        if (outState != DefaultState) return;
        if (outState.HeredocDelimiter != null || outState.OpenRegexDelimiter != null) return;

        outState = DetectUnclosedString(line, claimed);
        if (outState.OpenQuote == null) return;

        int openPos = FindOpeningQuote(line, claimed, outState.OpenQuote.Value);
        if (openPos >= 0)
            tokens.Add(new SyntaxToken(openPos, line.Length - openPos, "string"));
    }

    private List<SyntaxToken> ExpandEmbeddedRegions(List<SyntaxToken> tokens, string line,
        SyntaxDefinition grammar, ref LineState outState)
    {
        var regions = grammar.EmbeddedRegions;
        if (regions == null || outState != DefaultState) return tokens;

        for (int i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            if (region.StartRegex == null || region.EndRegex == null) continue;

            var startMatch = region.StartRegex.Match(line);
            if (!startMatch.Success) continue;

            int contentStart = startMatch.Index + startMatch.Length;
            var embeddedGrammar = GetDefinition(region.Extension);
            if (embeddedGrammar == null) continue;

            var endMatch = region.EndRegex.Match(line, contentStart);
            int contentEnd = endMatch.Success ? endMatch.Index : line.Length;
            string embeddedText = line[contentStart..contentEnd];
            var embeddedTokens = TokenizeEmbeddedSegment(embeddedText, embeddedGrammar, DefaultState,
                out var embeddedOutState, contentStart);

            tokens.RemoveAll(t => t.Start >= contentStart && t.Start < contentEnd);
            tokens.AddRange(embeddedTokens);

            if (!endMatch.Success)
                outState = new LineState(null, EmbeddedRegionIndex: i, EmbeddedState: embeddedOutState);

            return tokens;
        }

        return tokens;
    }

    private List<SyntaxToken> TokenizeEmbeddedSegment(string text, SyntaxDefinition embeddedGrammar,
        LineState inState, out LineState outState, int offset)
        => ShiftTokens(Tokenize(text, embeddedGrammar, inState, out outState), offset);

    private static List<SyntaxToken> ShiftTokens(List<SyntaxToken> tokens, int offset)
    {
        if (offset == 0) return tokens;

        var shifted = new List<SyntaxToken>(tokens.Count);
        foreach (var token in tokens)
            shifted.Add(token with { Start = token.Start + offset });
        return shifted;
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
            if (token.Scope != "operator" || token.Length != 2 || token.Start + 2 > line.Length) continue;
            char c0 = line[token.Start], c1 = line[token.Start + 1];
            if (c1 != '~' || (c0 != '=' && c0 != '!')) continue;

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
        bool hasString = false;
        foreach (var t in tokens)
            if (t.Scope == "string" && t.Length >= 2) { hasString = true; break; }
        if (!hasString) return tokens;

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
            int innerLen = innerEnd - innerStart;
            if (innerLen <= 0)
            {
                result.Add(token);
                continue;
            }

            // Collect all sub-tokens (variables and escapes) sorted by position.
            // Uses Match loop with startat on the original line to avoid substring allocation.
            var subTokens = new List<(int Start, int Length, string Scope)>();

            if (interp.VariableRegex != null)
            {
                var m = interp.VariableRegex.Match(line, innerStart, innerLen);
                while (m.Success)
                {
                    subTokens.Add((m.Index, m.Length, "variable"));
                    m = m.NextMatch();
                }
            }

            if (interp.EscapeRegex != null)
            {
                var m = interp.EscapeRegex.Match(line, innerStart, innerLen);
                while (m.Success)
                {
                    subTokens.Add((m.Index, m.Length, "escape"));
                    m = m.NextMatch();
                }
            }

            if (subTokens.Count == 0)
            {
                result.Add(token);
                continue;
            }

            subTokens.Sort((a, b) => a.Start.CompareTo(b.Start));

            // Split the string token around sub-tokens, skipping overlaps (first wins)
            int pos = token.Start;
            int lastSubEnd = 0;
            foreach (var (start, length, scope) in subTokens)
            {
                if (start < lastSubEnd) continue; // overlap — skip
                if (start > pos)
                    result.Add(new SyntaxToken(pos, start - pos, "string"));
                result.Add(new SyntaxToken(start, length, scope));
                pos = start + length;
                lastSubEnd = pos;
            }
            // Remaining string portion
            int tokenEnd = token.Start + token.Length;
            if (pos < tokenEnd)
                result.Add(new SyntaxToken(pos, tokenEnd - pos, "string"));
        }
        return result;
    }

    /// <summary>Returns all loaded grammar names sorted alphabetically.</summary>
    public List<string> GetAvailableLanguages()
        => _grammars.Select(g => g.Name).Distinct().OrderBy(n => n).ToList();

    /// <summary>Finds a grammar by its display name (case-insensitive).</summary>
    public SyntaxDefinition? GetDefinitionByName(string name)
        => _grammars.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

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
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
