using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Volt;

public class SyntaxRule
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonIgnore]
    public Regex? CompiledRegex { get; set; }
}

public class BlockCommentDef
{
    [JsonPropertyName("start")]
    public string Start { get; set; } = "";

    [JsonPropertyName("end")]
    public string End { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "comment";

    [JsonIgnore]
    public Regex? StartRegex { get; set; }

    [JsonIgnore]
    public Regex? EndRegex { get; set; }
}

public class InterpolationDef
{
    /// <summary>Which quote characters trigger interpolation (e.g. "\"" for double-quoted only).</summary>
    [JsonPropertyName("quotes")]
    public string Quotes { get; set; } = "\"";

    [JsonPropertyName("variablePattern")]
    public string VariablePattern { get; set; } = "";

    [JsonPropertyName("escapePattern")]
    public string EscapePattern { get; set; } = "";

    [JsonIgnore]
    public Regex? VariableRegex { get; set; }

    [JsonIgnore]
    public Regex? EscapeRegex { get; set; }
}

public class HeredocDef
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    /// <summary>Capture group (1-based) that indicates single-quoted / no interpolation.</summary>
    [JsonPropertyName("noInterpolationGroup")]
    public int NoInterpolationGroup { get; set; } = 1;

    [JsonIgnore]
    public Regex? CompiledRegex { get; set; }
}

public class SyntaxDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = [];

    [JsonPropertyName("rules")]
    public List<SyntaxRule> Rules { get; set; } = [];

    [JsonPropertyName("blockComments")]
    public List<BlockCommentDef>? BlockComments { get; set; }

    [JsonPropertyName("interpolation")]
    public InterpolationDef? Interpolation { get; set; }

    [JsonPropertyName("heredoc")]
    public HeredocDef? Heredoc { get; set; }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public void Compile()
    {
        foreach (var rule in Rules)
        {
            try
            {
                rule.CompiledRegex = new Regex(rule.Pattern,
                    RegexOptions.Compiled | RegexOptions.Multiline, RegexTimeout);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Invalid regex for scope '{rule.Scope}': {rule.Pattern} — {ex.Message}");
                rule.CompiledRegex = null;
            }
        }

        if (BlockComments != null)
        {
            for (int i = BlockComments.Count - 1; i >= 0; i--)
            {
                try
                {
                    BlockComments[i].StartRegex = new Regex(BlockComments[i].Start,
                        RegexOptions.Compiled, RegexTimeout);
                    if (!string.IsNullOrEmpty(BlockComments[i].End))
                        BlockComments[i].EndRegex = new Regex(BlockComments[i].End,
                            RegexOptions.Compiled, RegexTimeout);
                }
                catch
                {
                    BlockComments.RemoveAt(i);
                }
            }
        }

        if (Interpolation != null)
        {
            try
            {
                if (!string.IsNullOrEmpty(Interpolation.VariablePattern))
                    Interpolation.VariableRegex = new Regex(Interpolation.VariablePattern,
                        RegexOptions.Compiled, RegexTimeout);
                if (!string.IsNullOrEmpty(Interpolation.EscapePattern))
                    Interpolation.EscapeRegex = new Regex(Interpolation.EscapePattern,
                        RegexOptions.Compiled, RegexTimeout);
            }
            catch
            {
                Interpolation = null;
            }
        }

        if (Heredoc != null)
        {
            try
            {
                Heredoc.CompiledRegex = new Regex(Heredoc.Pattern,
                    RegexOptions.Compiled, RegexTimeout);
            }
            catch
            {
                Heredoc = null;
            }
        }
    }

    public static SyntaxDefinition? LoadFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var def = JsonSerializer.Deserialize<SyntaxDefinition>(json);
            def?.Compile();
            return def;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load grammar '{path}': {ex.Message}");
            return null;
        }
    }
}
