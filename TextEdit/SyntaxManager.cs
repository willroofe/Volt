using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace TextEdit;

public record SyntaxToken(int Start, int Length, string Scope);

/// <summary>Tracks whether a line ends inside an unclosed string.</summary>
public record LineState(char? OpenQuote);

public static class SyntaxManager
{
    public static readonly LineState DefaultState = new(null);
    private static readonly string GrammarsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextEdit", "Grammars");

    private static readonly List<SyntaxDefinition> _grammars = [];
    private static SyntaxDefinition? _activeGrammar;

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EnsureDefaultGrammars();
        LoadGrammars();
    }

    public static SyntaxDefinition? ActiveGrammar => _activeGrammar;

    public static void SetLanguageByExtension(string? extension)
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

    public static string ActiveLanguageName => _activeGrammar?.Name ?? "Plain Text";

    /// <summary>Tokenize a single line (no multi-line state).</summary>
    public static List<SyntaxToken> Tokenize(string line)
    {
        return Tokenize(line, DefaultState, out _);
    }

    /// <summary>Tokenize a line with multi-line continuation support.</summary>
    public static List<SyntaxToken> Tokenize(string line, LineState inState, out LineState outState)
    {
        outState = DefaultState;
        if (_activeGrammar == null) return [];

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
                tokens = ExpandInterpolation(tokens, line);
                return tokens;
            }
            // String closes at closePos (inclusive of the quote character)
            int len = closePos + 1;
            tokens.Add(new SyntaxToken(0, len, "string"));
            for (int i = 0; i < len; i++) claimed[i] = true;
            ruleStart = len;
        }

        // Apply regex rules to the (remaining) line
        foreach (var rule in _activeGrammar.Rules)
        {
            if (rule.CompiledRegex == null) continue;

            MatchCollection matches;
            try { matches = rule.CompiledRegex.Matches(line); }
            catch (RegexMatchTimeoutException) { continue; }

            try
            {
                foreach (Match match in matches)
                {
                    if (match.Length == 0) continue;
                    if (match.Index < ruleStart) continue;

                    bool overlap = false;
                    for (int i = match.Index; i < match.Index + match.Length; i++)
                    {
                        if (claimed[i]) { overlap = true; break; }
                    }
                    if (overlap) continue;

                    for (int i = match.Index; i < match.Index + match.Length; i++)
                        claimed[i] = true;

                    tokens.Add(new SyntaxToken(match.Index, match.Length, rule.Scope));
                }
            }
            catch (RegexMatchTimeoutException) { }
        }

        // Detect if the line ends with an unclosed string
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

        tokens = ExpandInterpolation(tokens, line);
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
                if (c == '\'' || c == '"') openQuote = c;
            }
            else if (c == openQuote)
            {
                openQuote = null;
            }
        }
        return new LineState(openQuote);
    }

    private static readonly Regex InterpolationRegex = new(
        @"(?<!\\)[\$@]\{?[\w:]+\}?", RegexOptions.Compiled);

    private static readonly Regex EscapeRegex = new(
        @"\\(?:[abefnrt\\'""]|0[0-7]*|x[0-9a-fA-F]{1,2}|u[0-9a-fA-F]{4}|N\{[^}]+\}|.)", RegexOptions.Compiled);

    private static List<SyntaxToken> ExpandInterpolation(List<SyntaxToken> tokens, string line)
    {
        var result = new List<SyntaxToken>(tokens.Count);
        foreach (var token in tokens)
        {
            if (token.Scope != "string" || token.Length < 2)
            {
                result.Add(token);
                continue;
            }

            // Only expand double-quoted strings (starts with ")
            char quote = line[token.Start];
            if (quote != '"')
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

            foreach (Match m in InterpolationRegex.Matches(inner))
                subTokens.Add((innerStart + m.Index, m.Length, "variable"));

            foreach (Match m in EscapeRegex.Matches(inner))
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

    public static void ReloadGrammars()
    {
        var activeExt = _activeGrammar?.Extensions.FirstOrDefault();
        _grammars.Clear();
        LoadGrammars();
        if (activeExt != null) SetLanguageByExtension(activeExt);
    }

    private static void LoadGrammars()
    {
        if (!Directory.Exists(GrammarsDir)) return;
        foreach (var file in Directory.GetFiles(GrammarsDir, "*.json"))
        {
            var def = SyntaxDefinition.LoadFromFile(file);
            if (def != null) _grammars.Add(def);
        }
    }

    private static void EnsureDefaultGrammars()
    {
        Directory.CreateDirectory(GrammarsDir);

        var perlPath = Path.Combine(GrammarsDir, "perl.json");
        if (!File.Exists(perlPath))
        {
            File.WriteAllText(perlPath, DefaultPerlGrammar);
        }
    }

    private static readonly string DefaultPerlGrammar = """
        {
          "name": "Perl",
          "extensions": [".pl", ".pm", ".t", ".cgi"],
          "rules": [
            {
              "pattern": "(?:\"(?:\\\\.|[^\"])*\"|'(?:\\\\.|[^'])*')",
              "scope": "string"
            },
            {
              "pattern": "#.*$",
              "scope": "comment"
            },
            {
              "pattern": "(?<![\\w$@%])\\b(?:use|no|require|package|sub|my|our|local|return|if|elsif|else|unless|while|until|for|foreach|do|last|next|redo|goto|die|warn|print|say|chomp|chop|push|pop|shift|unshift|splice|split|join|sort|reverse|map|grep|defined|undef|exists|delete|keys|values|each|length|scalar|ref|bless|tie|untie|open|close|read|write|seek|tell|eof|binmode|eval|BEGIN|END|INIT|CHECK|UNITCHECK|DESTROY|AUTOLOAD|__FILE__|__LINE__|__PACKAGE__|__SUB__|__DATA__|__END__)\\b",
              "scope": "keyword"
            },
            {
              "pattern": "(?<=\\bsub\\s)\\w+",
              "scope": "function"
            },
            {
              "pattern": "\\b\\w+(?=\\s*\\()",
              "scope": "function"
            },
            {
              "pattern": "&\\w[\\w:]*",
              "scope": "function"
            },
            {
              "pattern": "\\w+(?=\\s*=>)",
              "scope": "hashkey"
            },
            {
              "pattern": "(?<=\\{)\\w+(?=\\})",
              "scope": "hashkey"
            },
            {
              "pattern": "[\\$@%]\\{?[\\w:]+\\}?",
              "scope": "variable"
            },
            {
              "pattern": "(?:\\b|(?<=\\s))(?:=>|->|=~|!~|&&|\\|\\||//|\\.\\.|\\.\\.\\.)",
              "scope": "operator"
            },
            {
              "pattern": "(?<!\\.)\\b(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|0[0-7_]+|[0-9][0-9_]*(?:\\.[0-9_]+)?(?:[eE][+-]?[0-9_]+)?)\\b",
              "scope": "number"
            },
            {
              "pattern": "(?:qw|qq|q)\\s*[({\\[/].*?[)}\\]/]",
              "scope": "string"
            },
            {
              "pattern": "(?:m|s|tr|y)\\s*/(?:\\\\/|[^/])*/(?:\\\\/|[^/])*/[gimsxce]*",
              "scope": "regex"
            },
            {
              "pattern": "/(?:\\\\/|[^/\\n])+/[gimsxce]*",
              "scope": "regex"
            }
          ]
        }
        """;
}
