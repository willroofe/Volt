using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TextEdit;

public class SyntaxRule
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

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
            catch
            {
                rule.CompiledRegex = null;
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
        catch
        {
            return null;
        }
    }
}
