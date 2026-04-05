using System.Windows;
using Velopack;
using Velopack.Sources;

namespace Volt;

internal static class AppUpdateManager
{
    private static readonly string RepoUrl = "https://github.com/willroofe/Volt";

    /// <summary>
    /// Checks for updates and prompts the user to install if one is available.
    /// Silent when no update is found unless <paramref name="showUpToDate"/> is true.
    /// </summary>
    public static async Task CheckForUpdatesAsync(Window owner, bool showUpToDate = false)
    {
        try
        {
            var mgr = new Velopack.UpdateManager(new GithubSource(RepoUrl, null, false));

            // Not installed via Velopack (e.g. running from IDE) — skip silently
            if (!mgr.IsInstalled)
            {
                if (showUpToDate)
                    ThemedMessageBox.Show(owner, "Update checking is only available in the installed version.", "Check for Updates");
                return;
            }

            var update = await mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                if (showUpToDate)
                    ThemedMessageBox.Show(owner, "You're running the latest version.", "Check for Updates");
                return;
            }

            var result = ThemedMessageBox.Show(owner,
                $"Version {update.TargetFullRelease.Version} is available. Install and restart?",
                "Update Available",
                MessageBoxButton.YesNo);

            if (result != MessageBoxResult.Yes) return;

            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update);
        }
        catch (Exception)
        {
            // Network errors, rate limits, etc. — don't bother the user on background checks
            if (showUpToDate)
                ThemedMessageBox.Show(owner, "Could not check for updates. Please try again later.", "Check for Updates");
        }
    }
}
