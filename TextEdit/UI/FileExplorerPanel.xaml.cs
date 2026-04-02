using System.IO;
using System.Windows.Controls;
using System.Windows.Input;

namespace TextEdit;

public partial class FileExplorerPanel : UserControl
{
    public event Action<string>? FileOpenRequested;

    private string? _openFolderPath;

    public FileExplorerPanel()
    {
        InitializeComponent();
        FolderTree.MouseDoubleClick += OnTreeDoubleClick;
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        _openFolderPath = path;
        HeaderText.Text = Path.GetFileName(path);
        FolderTree.ItemsSource = FileTreeItem.LoadRoot(path);
    }

    public void CloseFolder()
    {
        _openFolderPath = null;
        HeaderText.Text = "Explorer";
        FolderTree.ItemsSource = null;
    }

    public string? OpenFolderPath => _openFolderPath;

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderTree.SelectedItem is FileTreeItem item && !item.IsDirectory)
        {
            FileOpenRequested?.Invoke(item.FullPath);
            e.Handled = true;
        }
    }
}
