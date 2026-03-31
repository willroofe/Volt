using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace TextEdit;

public record SyntaxToken(int Start, int Length, string Scope);

/// <summary>Tracks whether a line ends inside an unclosed string, block comment, or heredoc.</summary>
/// <param name="BlockCommentIndex">Index into grammar's BlockComments list, or -1 if not in a block comment.</param>
public record LineState(char? OpenQuote, int BlockCommentIndex = -1,
    string? HeredocDelimiter = null, bool HeredocInterpolate = true);

public class SyntaxManager
{
    public readonly LineState DefaultState = new(null);
    private readonly string GrammarsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextEdit", "Grammars");

    private readonly List<SyntaxDefinition> _grammars = [];
    private SyntaxDefinition? _activeGrammar;

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EnsureDefaultGrammars();
        LoadGrammars();
    }

    public SyntaxDefinition? ActiveGrammar => _activeGrammar;

    public void SetLanguageByExtension(string? extension)
    {
        _activeGrammar = null;
        if (string.IsNullOrEmpty(extension)) return;

        var ext = extension.ToLowerInvariant();
        foreach (var grammar in _grammars)
        {
            if (grammar.Extensions.Contains(ext))
            {
                _activeGrammar = grammar;
                return;
            }
        }
    }

    public string ActiveLanguageName => _activeGrammar?.Name ?? "Plain Text";

    /// <summary>Tokenize a single line (no multi-line state).</summary>
    public List<SyntaxToken> Tokenize(string line)
    {
        return Tokenize(line, DefaultState, out _);
    }

    /// <summary>Tokenize a line with multi-line continuation support.</summary>
    public List<SyntaxToken> Tokenize(string line, LineState inState, out LineState outState)
    {
        outState = DefaultState;
        if (_activeGrammar == null) return [];

        var bcs = _activeGrammar.BlockComments;

        // Handle block comment continuation (e.g. POD, __DATA__)
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

        // Handle heredoc continuation
        if (inState.HeredocDelimiter != null)
        {
            bool isEnd = line.TrimStart() == inState.HeredocDelimiter;
            outState = isEnd ? DefaultState : inState;
            if (line.Length > 0)
            {
                var hdTokens = new List<SyntaxToken> { new(0, line.Length, "string") };
                if (inState.HeredocInterpolate && _activeGrammar.Interpolation != null)
                    hdTokens = ExpandInterpolation(hdTokens, line, _activeGrammar.Interpolation);
                return hdTokens;
            }
            return [];
        }

        var claimed = new bool[line.Length];
        var tokens = new List<SyntaxToken>();
        int ruleStart = 0; // where to start applying regex rules

        // If continuing a string from the previous line, find the closing quote
        if (inState.OpenQuote is char openQ)
        {
            int closePos = FindClosingQuote(line, 0, openQ);
            if (closePos < 0)
            {
                // Entire line is still inside the string
                outState = inState;
                if (line.Length > 0)
                    tokens.Add(new SyntaxToken(0, line.Length, "string"));
                if (_activeGrammar.Interpolation != null)
                    tokens = ExpandInterpolation(tokens, line, _activeGrammar.Interpolation);
                return tokens;
            }
            // String closes at closePos (inclusive of the quote character)
            int len = closePos + 1;
            tokens.Add(new SyntaxToken(0, len, "string"));
            for (int i = 0; i < len; i++) claimed[i] = true;
            ruleStart = len;
        }

        // Collect all candidate matches with rule priority
        var candidates = new List<(int Priority, int Start, int Length, string Scope)>();
        for (int r = 0; r < _activeGrammar.Rules.Count; r++)
        {
            var rule = _activeGrammar.Rules[r];
            if (rule.CompiledRegex == null) continue;

            MatchCollection matches;
            try { matches = rule.CompiledRegex.Matches(line); }
            catch (RegexMatchTimeoutException) { continue; }

            try
            {
                foreach (Match match in matches)
                {
                    if (match.Length == 0 || match.Index < ruleStart) continue;
                    candidates.Add((r, match.Index, match.Length, rule.Scope));
                }
            }
            catch (RegexMatchTimeoutException) { }
        }

        // Sort by position first, then by rule priority (earlier rule wins ties)
        candidates.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Priority.CompareTo(b.Priority));

        // Greedily claim left-to-right: earliest position wins, rule priority breaks ties
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

        // Detect heredoc markers (e.g. <<EOF, <<~'END')
        var hd = _activeGrammar.Heredoc;
        if (hd?.CompiledRegex != null)
        {
            var hMatch = hd.CompiledRegex.Match(line);
            if (hMatch.Success)
            {
                bool hdOverlap = false;
                for (int i = hMatch.Index; i < hMatch.Index + hMatch.Length; i++)
                    if (claimed[i]) { hdOverlap = true; break; }

                if (!hdOverlap)
                {
                    for (int i = hMatch.Index; i < hMatch.Index + hMatch.Length; i++)
                        claimed[i] = true;
                    tokens.Add(new SyntaxToken(hMatch.Index, hMatch.Length, "string"));

                    // Extract delimiter from first matching capture group
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
            }
        }

        // Detect if the line ends with an unclosed string (skip if heredoc already set)
        if (outState.HeredocDelimiter == null)
        {
            outState = DetectUnclosedString(line, tokens);

            // If we detected an unclosed string, extend a string token to end of line
            if (outState.OpenQuote != null)
            {
                // Find the partial string token (the one whose opening quote starts the
                // unclosed string) and extend it to the end of the line.
                // The partial match won't have been captured by the regex (it requires a
                // closing quote), so scan for the opening quote in unclaimed positions.
                int openPos = FindOpeningQuote(line, claimed, outState.OpenQuote.Value);
                if (openPos >= 0)
                {
                    // Claim from openPos to end of line as string
                    int len = line.Length - openPos;
                    tokens.Add(new SyntaxToken(openPos, len, "string"));
                }
            }
        }

        if (_activeGrammar.Interpolation != null)
            tokens = ExpandInterpolation(tokens, line, _activeGrammar.Interpolation);
        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        return tokens;
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
    private static LineState DetectUnclosedString(string line, List<SyntaxToken> tokens)
    {
        // Walk through the line tracking quote state, skipping over tokenized regions
        // (strings that are fully closed, comments, etc.)
        var tokenized = new bool[line.Length];
        foreach (var t in tokens)
            for (int i = t.Start; i < t.Start + t.Length && i < line.Length; i++)
                tokenized[i] = true;

        char? openQuote = null;
        for (int i = 0; i < line.Length; i++)
        {
            if (tokenized[i]) continue; // skip positions already handled by regex
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
        var activeExt = _activeGrammar?.Extensions.FirstOrDefault();
        _grammars.Clear();
        LoadGrammars();
        if (activeExt != null) SetLanguageByExtension(activeExt);
    }

    private void LoadGrammars()
    {
        if (!Directory.Exists(GrammarsDir)) return;
        foreach (var file in Directory.GetFiles(GrammarsDir, "*.json"))
        {
            var def = SyntaxDefinition.LoadFromFile(file);
            if (def != null) _grammars.Add(def);
        }
    }

    private void EnsureDefaultGrammars()
    {
        Directory.CreateDirectory(GrammarsDir);

        // Always overwrite built-in grammars so embedded fixes take effect
        var perlPath = Path.Combine(GrammarsDir, "perl.json");
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TextEdit.Resources.Grammars.perl.json");
        if (stream == null) return;
        using var reader = new StreamReader(stream);
        File.WriteAllText(perlPath, reader.ReadToEnd());
    }
}
