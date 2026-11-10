using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using DSPiConsole.Core.Link;

namespace DSPiConsole.Core.Tests.Link;

/// <summary>
/// A scriptable hub for client tests: a real server-side WebSocket over an
/// in-memory pipe pair, so the client runs its actual socket code. Messages
/// the client sends are queued for the test to read; the test answers with
/// whatever the scenario needs.
/// </summary>
internal sealed class FakeLinkHub : IAsyncDisposable
{
    private WebSocket? _server;
    private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>();
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;

    /// <summary>How many times a client opened a socket to this hub.</summary>
    public int Connections { get; private set; }

    /// <summary>When set, the next open fails with this exception instead.</summary>
    public Exception? RefuseNextWith { get; set; }

    /// <summary>The socket factory to hand to <see cref="LinkClient"/>.</summary>
    public Task<WebSocket> OpenAsync(LinkEndpoint endpoint, CancellationToken token)
    {
        if (RefuseNextWith is { } refusal)
        {
            RefuseNextWith = null;
            return Task.FromException<WebSocket>(refusal);
        }
        var toServer = new Pipe();
        var toClient = new Pipe();
        var clientStream = new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        var serverStream = new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        var client = WebSocket.CreateFromStream(clientStream, new WebSocketCreationOptions { IsServer = false, SubProtocol = LinkService.SubProtocol });
        _server = WebSocket.CreateFromStream(serverStream, new WebSocketCreationOptions { IsServer = true, SubProtocol = LinkService.SubProtocol });
        Connections++;
        _reader = Task.Run(() => ReadLoop(_server));
        return Task.FromResult(client);
    }

    private async Task ReadLoop(WebSocket server)
    {
        var buffer = new byte[1 << 16];
        var message = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await server.ReceiveAsync(buffer, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    _inbox.Writer.TryWrite(new Closed((int?)r.CloseStatus));
                    return;
                }
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var bytes = message.ToArray();
                message.SetLength(0);
                _inbox.Writer.TryWrite(r.MessageType == WebSocketMessageType.Text
                    ? LinkMessage.Decode(bytes)
                    : LinkFrame.Decode(bytes));
            }
        }
        catch { }
    }

    public sealed record Closed(int? Code);

    /// <summary>The next thing the client sent, as a message, frame or close.</summary>
    public async Task<object> NextAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        return await _inbox.Reader.ReadAsync(cts.Token);
    }

    public async Task<T> ExpectAsync<T>(int timeoutMs = 5000) => Assert.IsType<T>(await NextAsync(timeoutMs));

    /// <summary>True when the client sends nothing more within the wait.</summary>
    public async Task<bool> QuietAsync(int ms = 200)
    {
        using var cts = new CancellationTokenSource(ms);
        try { await _inbox.Reader.WaitToReadAsync(cts.Token); return false; }
        catch (OperationCanceledException) { return true; }
    }

    public Task SendAsync(LinkMessage message) =>
        _server!.SendAsync(message.EncodeUtf8(), WebSocketMessageType.Text, true, CancellationToken.None);

    public Task SendTextAsync(string json) =>
        _server!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    public Task SendAsync(LinkFrame frame) =>
        _server!.SendAsync(frame.Encode(), WebSocketMessageType.Binary, true, CancellationToken.None);

    public Task CloseAsync(int code, string? reason = null) =>
        _server!.CloseOutputAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);

    /// <summary>The hub's half of hello.</summary>
    public static LinkHelloHub Hello(LinkAuthMode auth, string? pinSource = null, params string[] caps) =>
        new(new LinkHubInfo("6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b", "Studio Mac", LinkHubKind.Console, "1.1.6"),
            auth, caps.Length > 0 ? caps : ["cmd", "notify", "poll", "snapshot", "lock", "rename"],
            new LinkProtoVersion(), new LinkLimits(65536, 8192, 8, 20, 200000),
            auth == LinkAuthMode.Pin ? pinSource ?? LinkPinSource.Window : null);

    public static LinkDeviceInfo Board(int handle = 0, string serial = "E46058388B1A2E2C") =>
        new(handle, serial, "Living room", 1, "1.1.6", 0, 9, 8, 32, LinkDeviceState.Online, LinkDeviceLinkKind.Usb);

    /// <summary>Runs the usual open-mode handshake: answers hello, the courtesy
    /// pair (session <paramref name="session"/>) and device.list.</summary>
    public async Task AcceptOpenAsync(ushort session = 12, params LinkDeviceInfo[] devices)
    {
        await ExpectAsync<LinkHelloClient>();
        await SendAsync(Hello(LinkAuthMode.None));
        var pair = await ExpectAsync<LinkAuthPair>();
        await SendAsync(LinkOk.WithBody(pair.Id, new LinkAuthOkBody(session, LinkRole.Admin)));
        var list = await ExpectAsync<LinkDeviceListRequest>();
        await SendAsync(LinkOk.WithBody(list.Id, new LinkDeviceListBody(devices.Length > 0 ? devices : [Board()])));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _server?.Abort();
        if (_reader != null) await Task.WhenAny(_reader, Task.Delay(1000));
        _cts.Dispose();
    }

    /// <summary>A read stream and a write stream as one.</summary>
    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => read.ReadAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => write.WriteAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { read.Dispose(); write.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
