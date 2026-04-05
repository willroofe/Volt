using System.Windows;
using System.Windows.Threading;

namespace Volt;

public partial class App : Application
{
    public ThemeManager ThemeManager { get; } = new();
    public SyntaxManager SyntaxManager { get; } = new();
    public AppSettings Settings { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Initialize();
        SyntaxManager.Initialize();
        Settings = AppSettings.Load();
        ThemeManager.Apply(Settings.Application.ColorTheme);
        // Pre-warm monospace font cache at idle priority so it's ready
        // before the user opens settings or the command palette
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => FontManager.GetMonospaceFonts());

        // Create and show window manually so we can defer heavy session
        // restore until after the window is painted on screen.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        base.OnStartup(e);
    }
}
