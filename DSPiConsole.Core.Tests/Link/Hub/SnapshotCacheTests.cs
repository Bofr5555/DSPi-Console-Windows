using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>The hub's snapshot cache (spec 7.5): miss until read, patch in
/// place from PARAM_CHANGED, invalidate on anything it can't apply.</summary>
public class SnapshotCacheTests
{
    private static byte[] Blob(byte version = 32, int count = 6136)
    {
        var blob = new byte[count];
        blob[0] = version;
        return blob;
    }

    /// <summary>A v2 PARAM_CHANGED writing <paramref name="value"/> at
    /// <paramref name="offset"/>, source HOST_SET.</summary>
    private static byte[] ParamPacket(int offset, params byte[] value)
    {
        var p = new byte[12 + value.Length];
        p[0] = 0x02; p[1] = 0x02;
        p[4] = (byte)offset; p[5] = (byte)(offset >> 8);
        p[6] = (byte)value.Length; p[7] = (byte)(value.Length >> 8);
        p[8] = 1;
        value.CopyTo(p, 12);
        return p;
    }

    [Fact]
    public void SnapshotMissesUntilBulkSet()
    {
        var cache = new SnapshotCache();
        Assert.Null(cache.Snapshot());
        Assert.True(cache.SetBulk(Blob()));
        Assert.Equal(32, cache.Snapshot()!.WireVersion);
    }

    [Fact]
    public void ParamChangePatchesInPlace()
    {
        var cache = new SnapshotCache();
        cache.SetBulk(Blob());
        // The user volume at 4748 (WireBulkParams V32), as DspDevice decodes it.
        Assert.True(cache.ApplyParamChange(ParamPacket(4748, 0xAA, 0xBB, 0xCC, 0xDD)));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, cache.Snapshot()!.Bulk[4748..4752]);
    }

    [Fact]
    public void ParamChangeIgnoredWhenBlobAbsent()
    {
        var cache = new SnapshotCache();
        Assert.False(cache.ApplyParamChange(ParamPacket(16, 1, 2, 3, 4)));
        Assert.Null(cache.Snapshot());
    }

    [Fact]
    public void OutOfRangePatchInvalidatesRatherThanCorrupting()
    {
        // Deliberately unlike the Mac, which ignores it and keeps serving a blob
        // that missed a change.
        var cache = new SnapshotCache();
        cache.SetBulk(Blob(count: 100));
        Assert.False(cache.ApplyParamChange(ParamPacket(98, 1, 2, 3, 4)));
        Assert.Null(cache.Snapshot());
    }

    [Fact]
    public void TruncatedParamChangeInvalidates()
    {
        var cache = new SnapshotCache();
        cache.SetBulk(Blob());
        var packet = ParamPacket(16, 1, 2, 3, 4)[..14]; // says 4 bytes, carries 2
        Assert.False(cache.ApplyParamChange(packet));
        Assert.False(cache.HasBulk);
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0x00, 0, 0 })]                          // v2 idle
    [InlineData(new byte[] { 0x02, 0x03, 0, 0, 1 })]                       // BULK_INVALIDATED: the hub's to handle
    [InlineData(new byte[] { 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]  // v1
    [InlineData(new byte[] { 0x02 })]
    public void NonParamPacketIgnored(byte[] packet)
    {
        var cache = new SnapshotCache();
        cache.SetBulk(Blob());
        long before = cache.Generation;
        Assert.False(cache.ApplyParamChange(packet));
        Assert.NotNull(cache.Snapshot());
        Assert.Equal(before, cache.Generation);
    }

    [Fact]
    public void InvalidateClearsBlob()
    {
        var cache = new SnapshotCache();
        cache.SetBulk(Blob());
        cache.Invalidate();
        Assert.Null(cache.Snapshot());
        Assert.False(cache.HasBulk);
    }

    [Fact]
    public void StatusCarriedInSnapshot()
    {
        var cache = new SnapshotCache();
        cache.SetBulk(Blob());
        cache.SetStatus([1, 2, 3, 4, 5]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, cache.Snapshot()!.Status);
    }

    [Fact]
    public void UpdatedAtAndAgeFollowTheClock()
    {
        var clock = new ManualClock();
        var cache = new SnapshotCache(clock);
        var t0 = clock.GetUtcNow();
        cache.SetBulk(Blob());
        clock.Advance(TimeSpan.FromMilliseconds(120));
        var snap = cache.Snapshot()!;
        Assert.Equal(t0, snap.UpdatedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(120), snap.Age);

        cache.ApplyParamChange(ParamPacket(16, 9, 9, 9, 9));
        snap = cache.Snapshot()!;
        Assert.Equal(t0 + TimeSpan.FromMilliseconds(120), snap.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, snap.Age);
    }

    [Fact]
    public void SnapshotIsACopy()
    {
        var cache = new SnapshotCache();
        var blob = Blob();
        cache.SetBulk(blob);
        blob[1] = 0x55;                     // the caller's array isn't kept
        cache.Snapshot()!.Bulk[2] = 0x66;   // nor is a snapshot shared
        var snap = cache.Snapshot()!;
        Assert.Equal(0, snap.Bulk[1]);
        Assert.Equal(0, snap.Bulk[2]);
    }

    [Fact]
    public void ABlobReadAcrossAChangeIsNotCached()
    {
        var cache = new SnapshotCache();
        long started = cache.Generation;
        cache.ApplyParamChange(ParamPacket(16, 1, 2, 3, 4)); // lands mid-read
        Assert.False(cache.SetBulk(Blob(), started));
        Assert.Null(cache.Snapshot());

        started = cache.Generation;
        cache.Invalidate();                                 // BULK_INVALIDATED mid-read
        Assert.False(cache.SetBulk(Blob(), started));

        started = cache.Generation;
        Assert.True(cache.SetBulk(Blob(), started));        // a quiet read sticks
        Assert.NotNull(cache.Snapshot());
    }
}
