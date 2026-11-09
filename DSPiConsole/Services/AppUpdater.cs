using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Core.Updates;
using DSPiConsole.Models;
using Microsoft.UI.Dispatching;

namespace DSPiConsole.Services;

public enum UpdateInstallState
{
    Idle,
    Downloading,
    Verifying,
    /// <summary>Staged; the app is quitting so the launcher can install it.</summary>
    Ready,
    /// <summary>Staged, but the quit was cancelled: it installs at the next quit.</summary>
    WaitingForQuit,
    Failed,
}

/// <summary>
/// Tells the user when a newer DSPi Console has been published on GitHub, and
/// installs it when the release carries a signed zip and this copy can be
/// replaced; otherwise it points at the release page. The app downloads,
/// verifies and stages the release; the launcher stub swaps it in once the app
/// has exited (see <see cref="UpdateLayout"/>). Port of the macOS Console's
/// AppUpdateChecker and AppUpdateInstaller.
/// </summary>
public sealed class AppUpdater : ObservableObject
{
    public static AppUpdater Shared { get; } = new();

    /// <summary>Automatic checks repeat this often while the app stays open.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    /// <summary>Lets launch settle (What's New, the device connecting) first.</summary>
    private static readonly TimeSpan LaunchDelay = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Http = CreateClient();

    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _installCts;
    private bool _isChecking;
    private AppRelease? _available;
    private UpdateInstallState _installState;
    private double _progress;
    private string? _failure;
    private string? _checkError;

    private AppUpdater()
    {
        Layout = new UpdateLayout(AppContext.BaseDirectory);
    }

    public UpdateLayout Layout { get; }

    /// <summary>The running app's version. Console and firmware share one
    /// version scheme, so the expected firmware is also the app's own version.</summary>
    public FirmwareVersion? CurrentVersion => AppInfo.ExpectedFirmware;
    public string CurrentVersionText => CurrentVersion?.ToString() ?? AppInfo.Version;

    public bool IsChecking { get => _isChecking; private set => SetProperty(ref _isChecking, value); }
    /// <summary>The newest release above the running version, from the last check.</summary>
    public AppRelease? Available { get => _available; private set => SetProperty(ref _available, value); }
    public UpdateInstallState InstallState { get => _installState; private set => SetProperty(ref _installState, value); }
    /// <summary>Download progress, 0 to 1.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    /// <summary>Why the last install failed.</summary>
    public string? Failure { get => _failure; private set => SetProperty(ref _failure, value); }
    /// <summary>Why the last check failed, until the next one succeeds.</summary>
    public string? CheckError { get => _checkError; private set => SetProperty(ref _checkError, value); }
    public DateTime? LastChecked => AppSettings.Instance.UpdateLastChecked;

    /// <summary>An install is under way or staged: no other check or install
    /// starts until it is done (as on the Mac, a staged update counts).</summary>
    public bool IsInstalling => InstallState is UpdateInstallState.Downloading or UpdateInstallState.Verifying
        or UpdateInstallState.Ready or UpdateInstallState.WaitingForQuit;

    /// <summary>Whether this copy can be replaced: run from a release folder
    /// the user can write to.</summary>
    public bool CanReplace => Layout.CanReplace();

    /// <summary>Whether <paramref name="release"/> can be installed in place.</summary>
    public bool CanInstall(AppRelease release) => UpdateFeed.SignedZip(release) != null && CanReplace;

    /// <summary>An automatic check found a release to offer.</summary>
    public event Action<AppRelease>? UpdateFound;
    /// <summary>A release is staged: the app should quit so it can be installed.</summary>
    public event Action? ReadyToQuit;

    /// <summary>Whether the launcher should start the app again after installing.
    /// Cleared when the user cancels the quit, so a later ordinary quit installs
    /// without reopening the app.</summary>
    private bool _relaunch;

    // ── Checking ──

    /// <summary>Starts the launch check and the daily repeat, which each run
    /// only while the user allows them.</summary>
    public void Start(DispatcherQueue ui)
    {
        // A download left by a run that ended partway; never one another copy
        // of the app is in the middle of (it holds the lock).
        if (!IsInstalling)
        {
            using var held = Layout.TryLock();
            if (held != null) Layout.CleanWork();
        }
        _timer = ui.CreateTimer();
        _timer.Interval = LaunchDelay;
        _timer.Tick += async (t, _) =>
        {
            t.Interval = Interval;
            await CheckAutomaticallyAsync();
        };
        _timer.Start();
    }

    private async Task CheckAutomaticallyAsync()
    {
        if (!AppSettings.Instance.CheckForUpdatesAutomatically || IsInstalling) return;
        AppRelease? release;
        try { release = await CheckAsync(); }
        catch (Exception) { return; }
        if (release != null && release.TagName != AppSettings.Instance.UpdateSkippedTag)
            UpdateFound?.Invoke(release);
    }

    private Task<AppRelease?>? _check;

    /// <summary>Fetches the release list and records the result. Returns the
    /// release to offer, or null when the running version is the newest.
    /// Throws when the check itself failed. A call while a check is running
    /// shares that check's answer.</summary>
    public Task<AppRelease?> CheckAsync()
    {
        if (_check is { IsCompleted: false } running) return running;
        return _check = RunCheckAsync();
    }

    private async Task<AppRelease?> RunCheckAsync()
    {
        if (CurrentVersion is not { } current) return null;
        IsChecking = true;
        try
        {
            var feed = AppSettings.Instance.UpdateFeedOverride is { Length: > 0 } custom ? custom : UpdateFeed.ReleasesUrl;
            using var request = new HttpRequestMessage(HttpMethod.Get, feed);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "GitHub refused the request (it allows 60 an hour). Try again later."
                    : $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            var releases = UpdateFeed.Parse(await response.Content.ReadAsStringAsync());

            Available = UpdateFeed.NewestUpdate(releases, current);
            AppSettings.Instance.UpdateLastChecked = DateTime.Now;
            AppSettings.Instance.Save();
            OnPropertyChanged(nameof(LastChecked));
            CheckError = null;
            return Available;
        }
        catch (Exception e)
        {
            CheckError = Describe(e);
            throw;
        }
        finally
        {
            IsChecking = false;
        }
    }

    public void Skip(AppRelease release)
    {
        AppSettings.Instance.UpdateSkippedTag = release.TagName;
        AppSettings.Instance.Save();
    }

    public static Task OpenReleasePageAsync(AppRelease release) =>
        release.Page is { } page ? Windows.System.Launcher.LaunchUriAsync(page).AsTask() : Task.CompletedTask;

    // ── Installing ──

    /// <summary>Downloads, verifies and stages <paramref name="release"/>, then
    /// raises <see cref="ReadyToQuit"/>. Progress and failure are reported
    /// through <see cref="InstallState"/>.</summary>
    public async Task InstallAsync(AppRelease release)
    {
        if (IsInstalling || release.Version is not { } version || UpdateFeed.SignedZip(release) is not { } assets) return;
        Failure = null;
        if (!CanReplace)
        {
            Fail("DSPi Console can't replace itself where it is installed. Move the release folder somewhere you can write to, such as your Documents folder, then try again.");
            return;
        }

        // Two copies of the app share the release folder: only one may use
        // the download area at a time.
        var held = Layout.TryLock();
        if (held == null)
        {
            Fail("Another copy of DSPi Console is installing an update. Try again once it has finished.");
            return;
        }

        var cts = new CancellationTokenSource();
        _installCts = cts;
        Progress = 0;
        InstallState = UpdateInstallState.Downloading;
        try
        {
            var work = Layout.WorkDirectory;
            Layout.CleanWork();
            Directory.CreateDirectory(work);
            // A fixed name: the release's own asset name comes from the feed.
            var zip = Path.Combine(work, "update.zip");
            var signature = await DownloadTextAsync(assets.Signature.Url, cts.Token);
            var progress = new Progress<double>(p => { if (InstallState == UpdateInstallState.Downloading) Progress = p; });
            await DownloadFileAsync(assets.Zip.Url, zip, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            InstallState = UpdateInstallState.Verifying;
            await Task.Run(() =>
            {
                // Read once: what is unpacked is exactly what was verified.
                var bytes = File.ReadAllBytes(zip);
                if (!UpdateSignature.IsValid(bytes, signature))
                    throw new UpdateException("The download's signature doesn't match, so it was discarded.");
                Layout.Stage(bytes, version);
                Layout.CleanWork();
            });

            _relaunch = true;
            InstallState = UpdateInstallState.Ready;
            ReadyToQuit?.Invoke();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Layout.CleanWork();
            InstallState = UpdateInstallState.Idle;
        }
        catch (Exception e)
        {
            Layout.CleanWork();
            Fail(Describe(e));
        }
        finally
        {
            if (_installCts == cts) _installCts = null;
            cts.Dispose();
            held.Dispose();
        }
    }

    private void Fail(string message)
    {
        Failure = message;
        InstallState = UpdateInstallState.Failed;
    }

    public void CancelInstall()
    {
        if (InstallState == UpdateInstallState.Downloading) _installCts?.Cancel();
    }

    /// <summary>A failure that has been seen and dismissed.</summary>
    public void ClearFailure()
    {
        if (InstallState == UpdateInstallState.Failed) InstallState = UpdateInstallState.Idle;
    }

    /// <summary>The user cancelled the quit an install asked for. The update
    /// stays staged and installs at the next quit, without reopening the app.</summary>
    public void QuitCancelled()
    {
        if (InstallState != UpdateInstallState.Ready) return;
        _relaunch = false;
        InstallState = UpdateInstallState.WaitingForQuit;
    }

    /// <summary>Asks for the quit again, with a relaunch, for an update that
    /// is already staged.</summary>
    public void RetryQuit()
    {
        if (InstallState != UpdateInstallState.WaitingForQuit || !Layout.HasPending) return;
        _relaunch = true;
        InstallState = UpdateInstallState.Ready;
        ReadyToQuit?.Invoke();
    }

    /// <summary>
    /// Called as the app exits. When this session staged an update, starts the
    /// launcher to install it once this process has gone. An update staged by
    /// an earlier session is left for the launcher's next ordinary start.
    /// </summary>
    public void OnAppExiting()
    {
        if (InstallState is not (UpdateInstallState.Ready or UpdateInstallState.WaitingForQuit) || !Layout.HasPending) return;
        StartInstaller(_relaunch);
    }

    private void StartInstaller(bool relaunch)
    {
        var args = $"{UpdateLayout.InstallArgument} {Environment.ProcessId}";
        if (!relaunch) args += " " + UpdateLayout.NoLaunchArgument;
        try
        {
            Process.Start(new ProcessStartInfo(Layout.Stub, args)
            {
                UseShellExecute = false,
                WorkingDirectory = Layout.Root!,
            });
        }
        catch (Exception)
        {
            // The launcher installs it at its next ordinary start instead.
        }
    }

    /// <summary>
    /// At launch, before any window: when this app was started directly
    /// (from app\, past the launcher) and an update is staged, hands over to
    /// the launcher to install it and returns true; the caller exits. A staged
    /// update no newer than this app is dropped instead. Never hands over when
    /// the launcher has just said it could not install (which would loop).
    /// </summary>
    public static bool HandOffPendingUpdate()
    {
        try
        {
            if (Environment.GetCommandLineArgs().Contains(UpdateLayout.NoUpdateArgument, StringComparer.OrdinalIgnoreCase)) return false;
            var updater = Shared;
            if (!updater.Layout.HasPending) return false;
            if (updater.Layout.PendingVersion() is not { } pending || updater.CurrentVersion is not { } current || pending <= current)
            {
                updater.Layout.DiscardPending();
                return false;
            }
            updater.StartInstaller(relaunch: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Downloading ──

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // GitHub's API refuses requests without one.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DSPiConsole", AppInfo.Version is { Length: > 0 } v ? v : "dev"));
        return client;
    }

    private static async Task<string> DownloadTextAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) throw new UpdateException("The download failed.");
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>A download that receives nothing for this long has stalled.
    /// HttpClient's own timeout ends once the headers arrive.</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    private static async Task DownloadFileAsync(string url, string path, IProgress<double> progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new UpdateException("The download failed.");
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stall.CancelAfter(StallLimit);
            try { read = await source.ReadAsync(buffer, stall.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UpdateException("The download stalled. Check the internet connection and try again.");
            }
            if (read == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total is > 0) progress.Report((double)done / total.Value);
        }
        if (total is > 0 && done != total) throw new UpdateException("The download was cut short.");
    }

    private static string Describe(Exception e) => e switch
    {
        UpdateException u => u.Message,
        HttpRequestException => "GitHub couldn't be reached. Check the internet connection and try again.",
        TaskCanceledException => "GitHub took too long to answer. Try again later.",
        IOException or UnauthorizedAccessException => $"The update couldn't be written to disk ({e.Message}).",
        _ => e.Message,
    };
}
