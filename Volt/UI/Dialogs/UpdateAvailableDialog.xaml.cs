using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;

namespace Volt;

public partial class UpdateAvailableDialog : Window
{
    private readonly string _releasePageUrl;

    public bool InstallRequested { get; private set; }

    public UpdateAvailableDialog(string version, string? releaseNotesMarkdown, string releasePageUrl)
    {
        InitializeComponent();

        _releasePageUrl = releasePageUrl;
        VersionText.Text = $"Version {version} is available.";
        ReleaseNotesText.Text = FormatReleaseNotes(releaseNotesMarkdown);
    }

    public static bool Show(Window owner, string version, string? releaseNotesMarkdown, string releasePageUrl)
    {
        var dlg = new UpdateAvailableDialog(version, releaseNotesMarkdown, releasePageUrl)
        {
            Owner = owner
        };

        dlg.ShowDialog();
        return dlg.InstallRequested;
    }

    private static string FormatReleaseNotes(string? releaseNotesMarkdown)
    {
        if (string.IsNullOrWhiteSpace(releaseNotesMarkdown))
            return "Release notes are not available for this update.";

        string notes = releaseNotesMarkdown.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        notes = Regex.Replace(notes, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        notes = Regex.Replace(notes, @"\*\*(.+?)\*\*", "$1");
        notes = Regex.Replace(notes, @"__(.+?)__", "$1");
        notes = Regex.Replace(notes, @"`(.+?)`", "$1");
        return notes;
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _releasePageUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            ThemedMessageBox.Show(this, "Could not open the release page.", "Update Available");
        }
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        InstallRequested = true;
        DialogResult = true;
    }

    private void OnNotNow(object sender, RoutedEventArgs e)
    {
        InstallRequested = false;
        DialogResult = false;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        InstallRequested = false;
        DialogResult = false;
    }
}
