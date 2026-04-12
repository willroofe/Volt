using System.Text.Json.Serialization;

namespace Volt;

public class Workspace
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Untitled Workspace";

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonPropertyName("folders")]
    public List<string> Folders { get; set; } = [];

    [JsonPropertyName("session")]
    public WorkspaceSession Session { get; set; } = new();
}

public class WorkspaceSession
{
    [JsonPropertyName("tabs")]
    public List<WorkspaceSessionTab> Tabs { get; set; } = [];

    [JsonPropertyName("activeTabIndex")]
    public int ActiveTabIndex { get; set; }

    [JsonPropertyName("expandedPaths")]
    public List<string> ExpandedPaths { get; set; } = [];

    [JsonPropertyName("terminal")]
    public TerminalPreferences? Terminal { get; set; }

    [JsonPropertyName("editorLayout")]
    public EditorLayoutSnapshot? EditorLayout { get; set; }
}

public class WorkspaceSessionTab
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("isDirty")]
    public bool IsDirty { get; set; }

    [JsonPropertyName("caretLine")]
    public int CaretLine { get; set; }

    [JsonPropertyName("caretCol")]
    public int CaretCol { get; set; }

    [JsonPropertyName("scrollX")]
    public double ScrollX { get; set; }

    [JsonPropertyName("scrollY")]
    public double ScrollY { get; set; }
}
