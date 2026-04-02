using System.Text.Json.Serialization;

namespace TextEdit;

public class Project
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Untitled Project";

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonPropertyName("virtualFolders")]
    public List<string> VirtualFolders { get; set; } = [];

    [JsonPropertyName("folders")]
    public List<ProjectFolder> Folders { get; set; } = [];

    [JsonPropertyName("session")]
    public ProjectSession Session { get; set; } = new();
}

public class ProjectFolder
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("virtualParent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VirtualParent { get; set; }
}

public class ProjectSession
{
    [JsonPropertyName("tabs")]
    public List<ProjectSessionTab> Tabs { get; set; } = [];

    [JsonPropertyName("activeTabIndex")]
    public int ActiveTabIndex { get; set; }
}

public class ProjectSessionTab
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
