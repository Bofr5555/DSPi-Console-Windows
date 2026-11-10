using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace DSPiConsole.Core.Link.Hub;

/// <summary>
/// One upgraded connection: a receive loop that always has a receive pending
/// (.NET only processes a pong inside one; L0 spike b), a dispatcher that
/// hands whole messages to the <see cref="ILinkConnection"/> one at a time, a
/// single writer draining a bounded outbound queue, and a watchdog for the
/// spec's deadlines (4, 5).
/// <para>The handler runs on the dispatcher, not the receive loop, so a slow
/// command (a 5 s bulk read) doesn't stop pongs being read; a client that
/// outruns the dispatcher by <see cref="LinkServerTuning.InboundLimit"/> is
/// closed with 1013.</para>
/// </summary>
internal sealed class LinkWebSocketSession
{
    private readonly object _lock = new();
    private readonly object _handlerLock = new();
    private readonly Socket _socket;
    private readonly ActivityStream _stream;
    private readonly WebSocket _ws;
    private readonly LinkServerTuning _tuning;
    private readonly string _peer;
    private readonly Action _onShed;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ILinkConnection? _handler;

    // Outbound, under _lock.
    private readonly Queue<Outgoing> _out = new();
    private readonly SemaphoreSlim _outSignal = new(0);
    private long _outBytes;
    private bool _lagging;
    private readonly SortedSet<byte> _owedResync = new();
    /// <summary>A queued close: nothing more is accepted or delivered.</summary>
    private bool _closeQueued;
    /// <summary>Closing now: the queue is dropped and the writer stops.</summary>
    private bool _closing;
    private long _sendStartedAt;   // timestamp, 0 when no send is in flight
    private long _closeSentAt;     // timestamp, 0 until our close frame is out

    /// <summary>Set once by whichever path sends the close frame.</summary>
    private int _closeFrameClaimed;
    private int _connectionClosedCalled;
    private int _aborted;

    // Inbound.
    private readonly Channel<Inbound> _in = Channel.CreateUnbounded<Inbound>(new UnboundedChannelOptions { SingleReader = true });
    private long _inBytes;

    private enum Kind { Text, Binary, Close }

    private readonly record struct Outgoing(Kind Kind, byte[] Data, int Code)
    {
        public int Length => Data.Length;
    }

    private readonly record struct Inbound(Kind Kind, byte[] Data, bool AuthCheck = false);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public LinkWebSocketSession(Socket socket, ActivityStream stream, LinkServerTuning tuning, string peer, Action onShed)
    {
        _socket = socket;
        _stream = stream;
        _tuning = tuning;
        _peer = peer;
        _onShed = onShed;
        var ping = tuning.PingInterval > TimeSpan.Zero ? tuning.PingInterval : Timeout.InfiniteTimeSpan;
        _ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = LinkService.SubProtocol,
            KeepAliveInterval = ping,
            // A backstop only: the watchdog closes a silent peer with 1001
            // first. The runtime's own timeout aborts without a close code,
            // so it is set past the idle deadline.
            KeepAliveTimeout = ping == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : tuning.IdleDeadline + ping,
        });
    }

    // ── Lifecycle ──

    /// <summary>Runs the connection to the end. <paramref name="factory"/> null
    /// means it was refused (the server is stopping) and only tears down.</summary>
    public async Task RunAsync(LinkConnectionFactory? factory)
    {
        var writer = Task.Run(WriteLoopAsync);
        var watchdog = Task.Run(WatchdogAsync);
        Task dispatcher = Task.CompletedTask;
        if (factory != null && Volatile.Read(ref _aborted) == 0)
        {
            try
            {
                _handler = factory(_peer, Enqueue);
                dispatcher = Task.Run(DispatchLoopAsync);
            }
            catch (Exception e)
            {
                LinkLog.Write($"Link server: the connection handler failed to start: {e.Message}");
                Abort("handler failed");
            }
        }

        await ReceiveLoopAsync().ConfigureAwait(false);
        LinkLog.Write($"Link server: {_peer} disconnected ({(_ws.CloseStatus is { } status ? $"close {(int)status} {_ws.CloseStatusDescription}".TrimEnd() : "no close frame")})");

        // The socket is done (close handshake, failure or abort).
        lock (_lock)
        {
            _closing = true;
            _out.Clear();
            _outBytes = 0;
        }
        _in.Writer.TryComplete();
        await dispatcher.ConfigureAwait(false);
        _cts.Cancel();
        _outSignal.Release();
        try { await writer.ConfigureAwait(false); } catch { }
        try { await watchdog.ConfigureAwait(false); } catch { }
        _ws.Dispose();
        _stream.Dispose();
        _socket.Dispose();

        if (_handler is { } handler && Interlocked.Exchange(ref _connectionClosedCalled, 1) == 0)
        {
            try { lock (_handlerLock) handler.ConnectionClosed(); }
            catch (Exception e) { LinkLog.Write($"Link server: ConnectionClosed threw: {e.Message}"); }
        }
        _finished.TrySetResult();
    }

    /// <summary>Server stop: drop what's queued, send <paramref name="code"/>,
    /// wait briefly for the client's close, then drop the socket.</summary>
    public async Task ShutdownAsync(int code)
    {
        CloseNow(code);
        var limit = _tuning.CloseTimeout + _tuning.CloseTimeout;
        if (await Task.WhenAny(_finished.Task, Task.Delay(limit)).ConfigureAwait(false) != _finished.Task)
            Abort("shutdown timed out");
        await _finished.Task.ConfigureAwait(false);
    }

    /// <summary>Drops the socket without a close handshake: for a client that
    /// isn't reading, a close frame could never be written anyway.</summary>
    public void Abort(string why)
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
        lock (_lock)
        {
            _closing = true;
            _out.Clear();
            _outBytes = 0;
        }
        LinkLog.Write($"Link server: dropped {_peer} ({why})");
        _cts.Cancel();
        _outSignal.Release();
        try { _ws.Abort(); } catch { }
        try { _socket.Dispose(); } catch { }
    }

    /// <summary>Closes now, ahead of anything queued (keepalive failure,
    /// shutdown). The watchdog drops the socket if the client never answers.</summary>
    private void CloseNow(int code)
    {
        lock (_lock)
        {
            if (_closing) return;
            _closing = true;
            _out.Clear();
            _outBytes = 0;
        }
        _outSignal.Release();
        _ = Task.Run(() => SendCloseAsync(code));
    }

    private async Task SendCloseAsync(int code)
    {
        if (Interlocked.Exchange(ref _closeFrameClaimed, 1) != 0) return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(_tuning.CloseTimeout);
            await _ws.CloseOutputAsync((WebSocketCloseStatus)code, null, cts.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _closeSentAt, Stopwatch.GetTimestamp());
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException
                                    or IOException or InvalidOperationException or ArgumentException)
        {
            Abort($"close {code} not sent");
        }
    }

    // ── Outbound ──

    /// <summary>The handler's emit: may be called from any thread.</summary>
    public void Enqueue(LinkOutbound outbound)
    {
        bool overflow = false;
        lock (_lock)
        {
            if (_closing || _closeQueued) return;
            switch (outbound)
            {
                case LinkOutbound.Text t:
                    Add(new Outgoing(Kind.Text, Encoding.UTF8.GetBytes(t.Json), 0));
                    break;
                case LinkOutbound.Binary b:
                    if (_lagging && Shed(b.Data))
                    {
                        _onShed();
                        return;
                    }
                    Add(new Outgoing(Kind.Binary, b.Data, 0));
                    break;
                case LinkOutbound.Close c:
                    _closeQueued = true;
                    Add(new Outgoing(Kind.Close, [], c.Code));
                    break;
                default:
                    return;
            }
            if (_outBytes > _tuning.OutboundLimit) overflow = true;
            else if (_outBytes > _tuning.OutboundHighWater) _lagging = true;
        }
        if (overflow) Abort("outbound queue over its limit");
        else _outSignal.Release();
    }

    private void Add(Outgoing item)
    {
        _out.Enqueue(item);
        _outBytes += item.Length;
    }

    /// <summary>
    /// While the client lags: a POLL is dropped (the next one supersedes it);
    /// a NOTIFY or RESYNC is dropped and its device owed one RESYNC with reason
    /// 0, sent when the queue drains. Everything else (CMD responses, bounded
    /// by max_inflight; FWDATA acks) still queues.
    /// </summary>
    private bool Shed(byte[] frame)
    {
        if (frame.Length == 0) return false;
        switch ((LinkFrameType)frame[0])
        {
            case LinkFrameType.Poll:
                return true;
            case LinkFrameType.Notify:
            case LinkFrameType.Resync:
                _owedResync.Add(frame.Length > 4 ? frame[4] : (byte)0);
                return true;
            default:
                return false;
        }
    }

    private async Task WriteLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            while (true)
            {
                await _outSignal.WaitAsync(token).ConfigureAwait(false);
                Outgoing item;
                lock (_lock)
                {
                    if (_closing) return;
                    if (!_out.TryDequeue(out item)) continue;
                    _outBytes -= item.Length;
                    if (_lagging && _outBytes <= _tuning.OutboundLowWater)
                    {
                        _lagging = false;
                        // A queued close makes a RESYNC pointless.
                        if (!_closeQueued)
                        {
                            foreach (var handle in _owedResync)
                            {
                                Add(new Outgoing(Kind.Binary, new LinkResyncFrame(handle, (byte)LinkResyncReason.NotificationsDropped).Encode(), 0));
                                _outSignal.Release();
                            }
                        }
                        _owedResync.Clear();
                    }
                    if (item.Kind != Kind.Close) _sendStartedAt = Stopwatch.GetTimestamp();
                }
                if (item.Kind == Kind.Close)
                {
                    await SendCloseAsync(item.Code).ConfigureAwait(false);
                    return;
                }
                await _ws.SendAsync((ReadOnlyMemory<byte>)item.Data,
                    item.Kind == Kind.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                    true, token).ConfigureAwait(false);
                lock (_lock) _sendStartedAt = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            Abort("send failed");
        }
    }

    // ── Inbound ──

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16384];
        var message = new MemoryStream();
        bool discarding = false;
        try
        {
            while (true)
            {
                var r = await _ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await AnswerCloseAsync().ConfigureAwait(false);
                    return;
                }
                if (discarding)
                {
                    // The rest of an oversized message.
                    if (r.EndOfMessage) discarding = false;
                    continue;
                }
                if (message.Length + r.Count > _tuning.MaxMessage)
                {
                    // Over limits.max_frame: closed rather than buffered,
                    // authenticated or not (spec 4).
                    message.SetLength(0);
                    discarding = !r.EndOfMessage;
                    Enqueue(new LinkOutbound.Close(LinkCloseCode.ProtocolError));
                    continue;
                }
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var bytes = message.ToArray();
                message.SetLength(0);
                Deliver(r.MessageType == WebSocketMessageType.Text ? Kind.Text : Kind.Binary, bytes);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException) { }
    }

    /// <summary>The client closed: answer with our queued close code if we
    /// had one, else 1000, and let the socket go.</summary>
    private async Task AnswerCloseAsync()
    {
        int code = LinkCloseCode.NormalClosure;
        lock (_lock)
        {
            foreach (var item in _out)
                if (item.Kind == Kind.Close) code = item.Code;
            _closing = true;
            _out.Clear();
            _outBytes = 0;
        }
        _outSignal.Release();
        await SendCloseAsync(code).ConfigureAwait(false);
    }

    private bool Refusing
    {
        get { lock (_lock) return _closing || _closeQueued; }
    }

    private void Deliver(Kind kind, byte[] data)
    {
        if (Refusing || _handler == null) return;
        if (Interlocked.Add(ref _inBytes, data.Length) > _tuning.InboundLimit)
        {
            Enqueue(new LinkOutbound.Close(LinkCloseCode.TooFast));
            return;
        }
        _in.Writer.TryWrite(new Inbound(kind, data));
    }

    private async Task DispatchLoopAsync()
    {
        var handler = _handler!;
        await foreach (var item in _in.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.AuthCheck)
            {
                bool authed;
                lock (_handlerLock) authed = SafeIsAuthenticated(handler);
                if (!authed)
                {
                    LinkLog.Write($"Link server: {_peer} did not authenticate in time");
                    Enqueue(new LinkOutbound.Close(LinkCloseCode.AuthFailed));
                }
                continue;
            }
            Interlocked.Add(ref _inBytes, -item.Data.Length);
            if (Refusing) continue;
            try
            {
                if (item.Kind == Kind.Text)
                {
                    string json;
                    try { json = StrictUtf8.GetString(item.Data); }
                    catch (DecoderFallbackException)
                    {
                        Enqueue(new LinkOutbound.Close(LinkCloseCode.ProtocolError));
                        continue;
                    }
                    lock (_handlerLock) handler.ReceiveText(json);
                }
                else
                {
                    lock (_handlerLock) handler.ReceiveBinary(item.Data);
                }
            }
            catch (Exception e)
            {
                // A handler bug costs the message, not the session.
                LinkLog.Write($"Link server: the handler threw on a message from {_peer}: {e}");
            }
        }
    }

    private static bool SafeIsAuthenticated(ILinkConnection handler)
    {
        try { return handler.IsAuthenticated; }
        catch { return false; }
    }

    // ── Deadlines ──

    private async Task WatchdogAsync()
    {
        var t = _tuning;
        var shortest = new[] { t.IdleDeadline, t.AuthDeadline, t.StallDeadline, t.CloseTimeout }.Min();
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(shortest.TotalMilliseconds / 5, 10, 500));
        bool authChecked = false;
        try
        {
            using var timer = new PeriodicTimer(tick);
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                long closeSent, sendStarted;
                bool closing;
                lock (_lock)
                {
                    closeSent = _closeSentAt;
                    sendStarted = _sendStartedAt;
                    closing = _closing;
                }
                if (closeSent != 0 && Stopwatch.GetElapsedTime(closeSent) >= t.CloseTimeout)
                {
                    Abort("no answer to our close");
                    return;
                }
                if (sendStarted != 0 && Stopwatch.GetElapsedTime(sendStarted) >= t.StallDeadline)
                {
                    // Spec 5's 1011; a client that isn't reading can't be told.
                    Abort("client stopped reading (1011)");
                    return;
                }
                if (closing) continue;
                if (!authChecked && Stopwatch.GetElapsedTime(_startedAt) >= t.AuthDeadline)
                {
                    // Read on the dispatcher, so it never runs alongside a message.
                    authChecked = true;
                    if (_handler != null) _in.Writer.TryWrite(new Inbound(Kind.Text, [], AuthCheck: true));
                }
                if (Stopwatch.GetElapsedTime(_stream.LastReadTimestamp) >= t.IdleDeadline)
                {
                    LinkLog.Write($"Link server: {_peer} silent for {t.IdleDeadline.TotalSeconds:0.#} s (1001)");
                    CloseNow(LinkCloseCode.KeepaliveFailure);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>
/// The connection's stream as the WebSocket sees it: replays bytes read past
/// the HTTP head first, and stamps every read so the watchdog sees pongs,
/// which the WebSocket consumes without surfacing.
/// </summary>
internal sealed class ActivityStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _prefix;
    private int _prefixAt;
    private long _lastRead = Stopwatch.GetTimestamp();

    public ActivityStream(Stream inner, byte[] prefix)
    {
        _inner = inner;
        _prefix = prefix;
    }

    public long LastReadTimestamp => Interlocked.Read(ref _lastRead);

    private int TakePrefix(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, _prefix.Length - _prefixAt);
        if (n <= 0) return 0;
        _prefix.AsSpan(_prefixAt, n).CopyTo(buffer);
        _prefixAt += n;
        return n;
    }

    private int Stamp(int n)
    {
        if (n > 0) Interlocked.Exchange(ref _lastRead, Stopwatch.GetTimestamp());
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = TakePrefix(buffer.Span);
        if (n > 0) return n;
        return Stamp(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(Span<byte> buffer)
    {
        int n = TakePrefix(buffer);
        return n > 0 ? n : Stamp(_inner.Read(buffer));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
