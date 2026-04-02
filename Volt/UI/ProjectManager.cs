using System.IO;
using System.Text;
using System.Text.Json;

namespace Volt;

public class ProjectManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public Project? CurrentProject { get; private set; }

    public Project NewProject(string name = "Untitled Project")
    {
        var project = new Project { Name = name };
        CurrentProject = project;
        return project;
    }

    public Project OpenProject(string vprojPath)
    {
        var json = File.ReadAllText(vprojPath, Encoding.UTF8);
        var project = JsonSerializer.Deserialize<Project>(json, JsonOptions)
                      ?? new Project();
        project.FilePath = vprojPath;
        if (string.IsNullOrWhiteSpace(project.Name))
            project.Name = Path.GetFileNameWithoutExtension(vprojPath);
        CurrentProject = project;
        return project;
    }

    public void SaveProject()
    {
        if (CurrentProject?.FilePath == null) return;
        var json = JsonSerializer.Serialize(CurrentProject, JsonOptions);
        FileHelper.AtomicWriteText(CurrentProject.FilePath, json, Encoding.UTF8);
    }

    public void CloseProject()
    {
        CurrentProject = null;
    }

    public void AddFolder(string path)
    {
        if (CurrentProject == null) return;
        if (CurrentProject.Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;
        CurrentProject.Folders.Add(new ProjectFolder { Path = path });
    }

    public void RemoveFolder(string path)
    {
        if (CurrentProject == null) return;
        CurrentProject.Folders.RemoveAll(f =>
            string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public void CreateVirtualFolder(string name)
    {
        if (CurrentProject == null) return;
        if (CurrentProject.VirtualFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;
        CurrentProject.VirtualFolders.Add(name);
    }

    public void RemoveVirtualFolder(string name)
    {
        if (CurrentProject == null) return;
        CurrentProject.VirtualFolders.Remove(name);
        foreach (var folder in CurrentProject.Folders)
        {
            if (string.Equals(folder.VirtualParent, name, StringComparison.OrdinalIgnoreCase))
                folder.VirtualParent = null;
        }
    }

    public void RenameVirtualFolder(string oldName, string newName)
    {
        if (CurrentProject == null) return;
        var idx = CurrentProject.VirtualFolders.FindIndex(
            v => string.Equals(v, oldName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        CurrentProject.VirtualFolders[idx] = newName;
        foreach (var folder in CurrentProject.Folders)
        {
            if (string.Equals(folder.VirtualParent, oldName, StringComparison.OrdinalIgnoreCase))
                folder.VirtualParent = newName;
        }
    }

    public void AssignToVirtualFolder(string folderPath, string? virtualFolderName)
    {
        if (CurrentProject == null) return;
        var folder = CurrentProject.Folders.FirstOrDefault(f =>
            string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        if (folder != null)
            folder.VirtualParent = virtualFolderName;
    }
}
