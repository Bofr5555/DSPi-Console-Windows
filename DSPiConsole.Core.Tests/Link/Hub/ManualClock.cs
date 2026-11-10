namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>A clock that only moves when a test says so, for the hub's
/// schedulers and caches.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _lock = new();
    private readonly DateTimeOffset _start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _start + TimeSpan.FromTicks(_ticks);
    }

    public override long GetTimestamp()
    {
        lock (_lock) return _ticks;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        lock (_lock) _ticks += by.Ticks;
    }
}
