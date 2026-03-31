using System.Windows;
using System.Windows.Threading;

namespace TextEdit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Initialize();
        SyntaxManager.Initialize();
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Application.ColorTheme);
        // Pre-warm monospace font cache at idle priority so it's ready
        // before the user opens settings or the command palette
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => EditorControl.GetMonospaceFonts());
        base.OnStartup(e);
    }
}
