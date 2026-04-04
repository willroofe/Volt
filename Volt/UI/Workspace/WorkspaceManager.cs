using System.IO;
using System.Text;
using System.Text.Json;

namespace Volt;

public class WorkspaceManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public Workspace? CurrentWorkspace { get; private set; }

    public Workspace NewWorkspace(string name = "Untitled Workspace")
    {
        var workspace = new Workspace { Name = name };
        CurrentWorkspace = workspace;
        return workspace;
    }

    public Workspace OpenWorkspace(string workspacePath)
    {
        var json = File.ReadAllText(workspacePath, Encoding.UTF8);
        Workspace workspace;
        try
        {
            workspace = JsonSerializer.Deserialize<Workspace>(json, JsonOptions)
                            ?? new Workspace();
        }
        catch (JsonException ex)
        {
            ThemedMessageBox.Show(System.Windows.Application.Current.MainWindow,
                $"Failed to read workspace file:\n{ex.Message}",
                "Invalid Workspace File");
            workspace = new Workspace();
        }
        workspace.FilePath = workspacePath;
        workspace.Folders ??= [];
        if (string.IsNullOrWhiteSpace(workspace.Name))
            workspace.Name = Path.GetFileNameWithoutExtension(workspacePath);
        CurrentWorkspace = workspace;
        return workspace;
    }

    public void SaveWorkspace()
    {
        if (CurrentWorkspace?.FilePath == null) return;
        var json = JsonSerializer.Serialize(CurrentWorkspace, JsonOptions);
        FileHelper.AtomicWriteText(CurrentWorkspace.FilePath, json, Encoding.UTF8);
    }

    public void CloseWorkspace()
    {
        CurrentWorkspace = null;
    }

    public void AddFolder(string path)
    {
        if (CurrentWorkspace == null) return;
        if (CurrentWorkspace.Folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
            return;
        CurrentWorkspace.Folders.Add(path);
    }

    public void RemoveFolder(string path)
    {
        if (CurrentWorkspace == null) return;
        CurrentWorkspace.Folders.RemoveAll(f =>
            string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
    }
}
