using System.Windows;

namespace TextEdit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.ColorTheme);
        base.OnStartup(e);
    }
}
