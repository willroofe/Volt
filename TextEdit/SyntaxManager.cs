using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace TextEdit;

public record SyntaxToken(int Start, int Length, string Scope);

public static class SyntaxManager
{
    private static readonly string GrammarsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextEdit", "Grammars");

    private static readonly List<SyntaxDefinition> _grammars = [];
    private static SyntaxDefinition? _activeGrammar;

    static SyntaxManager()
    {
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

    public static List<SyntaxToken> Tokenize(string line)
    {
        if (_activeGrammar == null) return [];

        // Track which character positions are already claimed
        var claimed = new bool[line.Length];
        var tokens = new List<SyntaxToken>();

        // Rules are applied in order — first match wins for each position
        foreach (var rule in _activeGrammar.Rules)
        {
            if (rule.CompiledRegex == null) continue;

            foreach (System.Text.RegularExpressions.Match match in rule.CompiledRegex.Matches(line))
            {
                if (match.Length == 0) continue;

                // Check if any part of this match is already claimed
                bool overlap = false;
                for (int i = match.Index; i < match.Index + match.Length; i++)
                {
                    if (claimed[i]) { overlap = true; break; }
                }
                if (overlap) continue;

                // Claim these positions
                for (int i = match.Index; i < match.Index + match.Length; i++)
                    claimed[i] = true;

                tokens.Add(new SyntaxToken(match.Index, match.Length, rule.Scope));
            }
        }

        // Sub-tokenize double-quoted strings for interpolated variables
        tokens = ExpandInterpolation(tokens, line);

        // Sort by position for rendering
        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        return tokens;
    }

    private static readonly Regex InterpolationRegex = new(
        @"(?<!\\)[\$@]\{?[\w:]+\}?", RegexOptions.Compiled);

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

            // Only interpolate double-quoted strings (starts with ")
            char quote = line[token.Start];
            if (quote != '"')
            {
                result.Add(token);
                continue;
            }

            // Find interpolated variables within the string (skip the quotes)
            int innerStart = token.Start + 1;
            int innerEnd = token.Start + token.Length - 1;
            string inner = line[innerStart..innerEnd];

            var matches = InterpolationRegex.Matches(inner);
            if (matches.Count == 0)
            {
                result.Add(token);
                continue;
            }

            // Split the string token around interpolated variables
            int pos = token.Start;
            foreach (Match m in matches)
            {
                int varStart = innerStart + m.Index;
                // String portion before this variable
                if (varStart > pos)
                    result.Add(new SyntaxToken(pos, varStart - pos, "string"));
                // The interpolated variable
                result.Add(new SyntaxToken(varStart, m.Length, "variable"));
                pos = varStart + m.Length;
            }
            // Remaining string portion after last variable
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
          "extensions": [".pl", ".pm", ".t"],
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
