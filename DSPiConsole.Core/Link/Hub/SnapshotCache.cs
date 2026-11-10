namespace DSPiConsole.Core.Link.Hub;

/// <summary>A cached device snapshot as <c>device.snapshot</c> returns it
/// (spec 7.5).</summary>
/// <param name="WireVersion">Byte 0 of the blob's header.</param>
/// <param name="Bulk">The REQ_GET_ALL_PARAMS blob; a copy the caller owns.</param>
/// <param name="Status">The last REQ_GET_STATUS wValue 9 response, if any.</param>
/// <param name="UpdatedAt">When the blob was last read whole or patched.</param>
/// <param name="Age">How stale the blob was when this snapshot was taken, for
/// the reply's <c>age_ms</c>.</param>
public sealed record DeviceSnapshot(int WireVersion, byte[] Bulk, byte[]? Status, DateTimeOffset UpdatedAt, TimeSpan Age);

/// <summary>
/// One coherent copy of a device's bulk-parameter blob (and its last status
/// frame), so a client's first screen costs one JSON reply instead of a bulk
/// read plus dozens of GETs (spec 7.5). The hub refreshes the blob after a
/// BULK_INVALIDATED and patches it in place from every PARAM_CHANGED it
/// relays. Port of the macOS Console's SnapshotCache.
/// </summary>
/// <remarks>
/// Patching needs no field table: a v2 PARAM_CHANGED addresses its value by
/// (offset, size) into WireBulkParams, the same struct the blob is (the
/// Windows notify decoder in DspDevice and NotifyDecoder read the offsets the
/// same way), so the value bytes are copied straight over the blob.
/// </remarks>
public sealed class SnapshotCache
{
    /// <summary>v2 PARAM_CHANGED: version, event, flags, seq, offset u16,
    /// size u16, source, 3 reserved, then the value bytes.</summary>
    private const int ParamHeaderLength = 12;
    private const byte NotifyVersion = 0x02;
    private const byte EventParamChanged = 0x02;

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private byte[]? _bulk;
    private byte[]? _status;
    private DateTimeOffset _updatedAt = DateTimeOffset.MinValue;
    private long _generation;

    /// <summary>Bumps on <see cref="Invalidate"/> only: a read that spans one
    /// can't be repaired by replaying patches.</summary>
    private long _invalidations;
    /// <summary>The PARAM_CHANGED patches seen lately, numbered, so a bulk read
    /// that changes landed during can be brought up to date instead of thrown
    /// away (under steady edits every read would be, and the hub would read
    /// the whole blob over and over).</summary>
    private readonly List<(long Seq, int Offset, byte[] Value)> _patches = new();
    private long _patchSeq;
    private const int PatchLogLimit = 512;

    /// <summary>Where a bulk read started: the invalidation count and the last
    /// patch already seen.</summary>
    public readonly record struct ReadToken(long Invalidations, long PatchSeq);

    public SnapshotCache(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Bumps on every <see cref="SetBulk"/>, <see cref="Invalidate"/> and
    /// PARAM_CHANGED seen. Read it before starting a bulk read and pass it to
    /// <see cref="SetBulk"/>, so a blob read while the device was changing
    /// (BULK_INVALIDATED or PARAM_CHANGED arriving mid-read, which a chunked
    /// 0xA2 read on Windows makes likely) is not cached as current.
    /// </summary>
    public long Generation
    {
        get { lock (_lock) return _generation; }
    }

    public bool HasBulk
    {
        get { lock (_lock) return _bulk != null; }
    }

    /// <summary>
    /// Replaces the whole blob after a fresh REQ_GET_ALL_PARAMS. With
    /// <paramref name="readStartedAt"/> (the <see cref="Generation"/> read just
    /// before the bulk read was issued) the blob is only stored when nothing
    /// changed in between; otherwise it is discarded and false returned. The
    /// blob that was read is still fine to answer the snapshot request that
    /// asked for it; it just isn't kept.
    /// </summary>
    public bool SetBulk(byte[] bulk, long? readStartedAt = null)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        lock (_lock)
        {
            if (readStartedAt is { } started && started != _generation) return false;
            if (bulk.Length == 0) return false;
            _bulk = (byte[])bulk.Clone();
            _updatedAt = _clock.GetUtcNow();
            _generation++;
            return true;
        }
    }

    /// <summary>Call just before issuing a bulk read; pass the token to
    /// <see cref="SetBulk(byte[], ReadToken)"/> with the blob.</summary>
    public ReadToken BeginRead()
    {
        lock (_lock) return new ReadToken(_invalidations, _patchSeq);
    }

    /// <summary>
    /// Stores a blob read since <paramref name="token"/>, with every patch seen
    /// since replayed onto it in order. Patches carry absolute values, so
    /// replaying one the read already caught is harmless and the last one for
    /// each field wins. False (blob not kept) when an invalidation came in
    /// between, or the patch log no longer reaches back to the read's start.
    /// </summary>
    public bool SetBulk(byte[] bulk, ReadToken token)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        lock (_lock)
        {
            if (bulk.Length == 0 || token.Invalidations != _invalidations) return false;
            var since = _patches.Where(p => p.Seq > token.PatchSeq).ToList();
            if (_patchSeq > token.PatchSeq && (since.Count == 0 || since[0].Seq != token.PatchSeq + 1)) return false;
            var blob = (byte[])bulk.Clone();
            foreach (var (_, offset, value) in since)
            {
                if (offset + value.Length > blob.Length) return false;
                value.CopyTo(blob, offset);
            }
            _bulk = blob;
            _updatedAt = _clock.GetUtcNow();
            _generation++;
            return true;
        }
    }

    /// <summary>Records the latest status frame (REQ_GET_STATUS wValue 9),
    /// which the snapshot reply carries alongside the blob.</summary>
    public void SetStatus(byte[]? status)
    {
        lock (_lock) _status = status == null ? null : (byte[])status.Clone();
    }

    /// <summary>Drops the blob so the next <see cref="Snapshot"/> misses until
    /// the hub re-reads it. Call on BULK_INVALIDATED (the per-field events are
    /// suppressed inside that bracket), on device reattach, and on anything else
    /// that may change the blob without a PARAM_CHANGED.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _bulk = null;
            _updatedAt = DateTimeOffset.MinValue;
            _generation++;
            _invalidations++;
            _patches.Clear();
        }
    }

    /// <summary>
    /// Applies one relayed notification packet (verbatim, version byte first)
    /// to the cached blob. Anything but a v2 PARAM_CHANGED is ignored and false
    /// returned. A PARAM_CHANGED that is patched returns true. One that cannot
    /// be applied (truncated, or its (offset, size) runs past the blob)
    /// invalidates the cache, because keeping a blob that missed a change would
    /// serve stale state as current (spec 7.5 allows marking the cache stale
    /// instead of patching). A PARAM_CHANGED while there is no blob just
    /// returns false.
    /// </summary>
    public bool ApplyParamChange(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2 || packet[0] != NotifyVersion || packet[1] != EventParamChanged) return false;
        lock (_lock)
        {
            _generation++;
            if (packet.Length < ParamHeaderLength)
            {
                InvalidateLocked();
                return false;
            }
            int offset = packet[4] | packet[5] << 8;
            int size = packet[6] | packet[7] << 8;
            if (size == 0) return false;
            if (packet.Length < ParamHeaderLength + size)
            {
                InvalidateLocked();
                return false;
            }
            // Logged even with no blob: a read in flight replays it.
            _patches.Add((++_patchSeq, offset, packet.Slice(ParamHeaderLength, size).ToArray()));
            if (_patches.Count > PatchLogLimit) _patches.RemoveAt(0);
            if (_bulk == null) return false;
            if (offset + size > _bulk.Length)
            {
                InvalidateLocked();
                return false;
            }
            packet.Slice(ParamHeaderLength, size).CopyTo(_bulk.AsSpan(offset, size));
            _updatedAt = _clock.GetUtcNow();
            return true;
        }
    }

    /// <summary>The current snapshot, or null when the blob is absent
    /// (invalidated or never read); the caller then reads the device.</summary>
    public DeviceSnapshot? Snapshot()
    {
        lock (_lock)
        {
            if (_bulk == null) return null;
            var age = _clock.GetUtcNow() - _updatedAt;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            return new DeviceSnapshot(_bulk[0], (byte[])_bulk.Clone(),
                _status == null ? null : (byte[])_status.Clone(), _updatedAt, age);
        }
    }

    private void InvalidateLocked()
    {
        _bulk = null;
        _updatedAt = DateTimeOffset.MinValue;
        _invalidations++;
        _patches.Clear();
    }
}
