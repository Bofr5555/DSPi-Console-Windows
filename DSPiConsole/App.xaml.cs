using Microsoft.UI.Xaml;
using Microsoft.UI.Composition.SystemBackdrops;
using WinRT;

namespace DSPiConsole;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("DSPI_SUBHARM_HARNESS") != null)
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dspi-harness-exceptions.log");
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                try { System.IO.File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {e.Exception}\n{Environment.StackTrace}\n\n"); } catch { }
            };
            _window = new Debugging.SubharmHarness();
            _window.Activate();
            return;
        }
#endif
        // Started past the launcher with an update staged: the launcher
        // installs it and starts the new version.
        if (Services.AppUpdater.HandOffPendingUpdate())
        {
            Environment.Exit(0);
            return;
        }
        var main = new MainWindow();
        _window = main;
        // "Start in the notification area only": a gateway that opens at
        // login goes straight to its icon.
        if (Services.BackgroundMode.StartsHidden) Services.BackgroundMode.Shared.StartHidden(main);
        else main.Activate();
#if DEBUG
        if (Environment.GetEnvironmentVariable("DSPI_EDITOR_HARNESS") != null)
            new Debugging.GraphEditorHarness().Activate();
#endif
    }
}
