using System.Reflection;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace Volt;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = GetCurrentVersion();
        var buildDate = GetBuildDate();
        VersionText.Text = buildDate != null
            ? $"Version {version} ({buildDate:yyyy-MM-dd})"
            : $"Version {version}";
        CopyrightText.Text = $"\u00a9 {DateTime.Now.Year} William Roofe";
    }

    private static string GetCurrentVersion()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource("https://github.com/willroofe/Volt", null, false));
            if (mgr.IsInstalled && mgr.CurrentVersion != null)
                return mgr.CurrentVersion.ToString();
        }
        catch { }

        return "dev";
    }

    private static DateTime? GetBuildDate()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var path = assembly.Location;
            if (!string.IsNullOrEmpty(path))
                return System.IO.File.GetLastWriteTime(path);
        }
        catch { }

        return null;
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        SetUpdateStatus("Checking for updates\u2026", indeterminate: true);

        try
        {
            var mgr = new UpdateManager(new GithubSource("https://github.com/willroofe/Volt", null, false));

            if (!mgr.IsInstalled)
            {
                SetUpdateStatus("Update checking is only available in the installed version.");
                UpdateButton.IsEnabled = true;
                return;
            }

            var update = await mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                SetUpdateStatus("You're running the latest version.");
                UpdateButton.IsEnabled = true;
                return;
            }

            var result = ThemedMessageBox.Show(this,
                $"Version {update.TargetFullRelease.Version} is available. Install and restart?",
                "Update Available",
                MessageBoxButton.YesNo);

            if (result != MessageBoxResult.Yes)
            {
                HideUpdateStatus();
                UpdateButton.IsEnabled = true;
                return;
            }

            SetUpdateStatus("Downloading update\u2026", progress: 0);
            await mgr.DownloadUpdatesAsync(update, p =>
                Dispatcher.Invoke(() => SetUpdateStatus($"Downloading update\u2026 {p}%", progress: p)));

            SetUpdateStatus("Installing\u2026", indeterminate: true);
            mgr.ApplyUpdatesAndRestart(update);
        }
        catch (Exception)
        {
            SetUpdateStatus("Could not check for updates. Please try again later.");
            UpdateButton.IsEnabled = true;
        }
    }

    private void SetUpdateStatus(string text, bool indeterminate = false, int? progress = null)
    {
        UpdateStatusText.Text = text;
        UpdateStatusText.Visibility = Visibility.Visible;

        if (indeterminate)
        {
            UpdateProgress.IsIndeterminate = true;
            UpdateProgress.Visibility = Visibility.Visible;
        }
        else if (progress.HasValue)
        {
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = progress.Value;
            UpdateProgress.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateProgress.Visibility = Visibility.Hidden;
        }
    }

    private void HideUpdateStatus()
    {
        UpdateStatusText.Visibility = Visibility.Hidden;
        UpdateProgress.Visibility = Visibility.Hidden;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
