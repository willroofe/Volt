using System.IO;
using System.Windows;
using System.Windows.Threading;
using Velopack;

namespace Volt;

public partial class App : Application
{
    public ThemeManager ThemeManager { get; } = new();
    public SyntaxManager SyntaxManager { get; } = new();
    public AppSettings Settings { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack must run before any WPF code — it handles
        // install/uninstall/update hooks and may exit the process.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

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

        // Pass command-line file path (e.g. from Windows "Open with") to the window
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            window._startupFilePath = e.Args[0];

        MainWindow = window;
        window.Show();

        base.OnStartup(e);
    }
}
