namespace DSPiConsole.Core;

/// <summary>
/// Lets one run of a repeating job in at a time: a poll whose previous read is
/// still outstanding is skipped, not queued. A timer's tick keeps firing while
/// an async handler awaits, so over a slow link (a remote hub, a wedged device)
/// unguarded polls stack up behind each other.
/// </summary>
public sealed class InFlightGate
{
    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _busy, 0);

    /// <summary>Awaits <paramref name="work"/> unless a run is already in flight.</summary>
    public async Task RunAsync(Func<Task> work)
    {
        if (!TryEnter()) return;
        try { await work(); }
        finally { Exit(); }
    }

    /// <summary>Starts <paramref name="work"/> on the thread pool unless a run
    /// is already in flight; returns at once.</summary>
    public void RunInBackground(Action work)
    {
        if (!TryEnter()) return;
        Task.Run(() =>
        {
            try { work(); }
            finally { Exit(); }
        });
    }
}
