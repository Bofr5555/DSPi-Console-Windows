using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DSPiConsole;

/// <summary>
/// The entry point (XAML's generated Main is disabled): one DSPi Console per
/// user session. A second launch hands its activation to the running one,
/// which comes forward (out of the notification area too), and exits. Two
/// copies would fight over the board's USB interface and the hub's port.
/// </summary>
public static class Program
{
    private const string InstanceKey = "DSPiConsole.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        // As the generated Main does.
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (!AllowSecondInstance() && RedirectedToRunningInstance()) return 0;

        Application.Start(init =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    /// <summary>Debug builds may run a second copy for loopback Link tests
    /// (DSPI_ALLOW_SECOND_INSTANCE) and for the debug harnesses.</summary>
    private static bool AllowSecondInstance()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable("DSPI_ALLOW_SECOND_INSTANCE") != null
            || Environment.GetEnvironmentVariable("DSPI_SUBHARM_HARNESS") != null;
#else
        return false;
#endif
    }

    /// <summary>True when another copy is running and now has this launch.</summary>
    private static bool RedirectedToRunningInstance()
    {
        AppInstance key;
        try { key = AppInstance.FindOrRegisterForKey(InstanceKey); }
        catch (Exception) { return false; }   // no registration: run rather than refuse

        if (key.IsCurrent)
        {
            key.Activated += OnActivatedAgain;
            return false;
        }

        // Let the running copy take the foreground: this process has it,
        // having just been launched by the user.
        AllowSetForegroundWindow(key.ProcessId);
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Redirected off this STA thread, waited for here.
        var done = Task.Run(() => key.RedirectActivationToAsync(activation).AsTask());
        try { done.Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { }
        return true;
    }

    /// <summary>Another launch: come forward. Raised on a background thread.</summary>
    private static void OnActivatedAgain(object? sender, AppActivationArguments e)
    {
        // The login entry starting while a copy already runs: leave it as it is.
        if (e.Kind == ExtendedActivationKind.Launch
            && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            && (launch.Arguments ?? "").Contains(Core.Background.LoginItem.Argument, StringComparison.OrdinalIgnoreCase))
            return;
        Core.Link.LinkLog.Write("app: another launch brought this copy forward");
        Services.BackgroundMode.Shared.RestoreFromAnyThread();
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("Microsoft.ui.xaml.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void XamlCheckProcessRequirements();
}
