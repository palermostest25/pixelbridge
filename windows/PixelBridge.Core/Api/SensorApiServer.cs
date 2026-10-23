using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PixelBridge.Core.Api;

/// <summary>
/// Read-only loopback sensor API: <c>ws://127.0.0.1:PORT/sensors?token=TOKEN</c> streams JSON samples;
/// <c>GET /status?token=TOKEN</c> returns a JSON snapshot; <c>GET /health</c> needs no token.
/// Binds 127.0.0.1 only. Requests with a non-loopback Origin are refused (blocks cross-origin web pages),
/// and every data endpoint requires the per-install token. No control operations are exposed here.
/// </summary>
public sealed class SensorApiServer : IAsyncDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly TcpListener _listener;
    private readonly string _token;
    private readonly Func<object> _statusProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Channel<string>> _clients = new();
    private readonly Lock _lock = new();
    private Task? _acceptTask;

    public int Port { get; }
    public int ClientCount { get { lock (_lock) return _clients.Count; } }
    public long Rejected { get; private set; }
    public event Action<bool>? DemandChanged;

    public SensorApiServer(int port, string token, Func<object> statusProvider)
    {
        Port = port;
        _token = token;
        _statusProvider = statusProvider;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    public void Start()
    {
        _listener.Start(8);
        _acceptTask = Task.Run(AcceptLoop);
        Log.Info($"sensor API listening on ws://127.0.0.1:{Port}/sensors");
    }

    public void Publish(SensorSample s, long hostNs)
    {
        if (ClientCount == 0) return;
        var json = JsonSerializer.Serialize(new
        {
            type = "sample", sensor = SensorTypes.Name(s.Type), sensorType = s.Type, unit = SensorTypes.Unit(s.Type),
            deviceTimestampNs = s.TimestampNs, hostTimestampNs = hostNs, accuracy = s.Accuracy, values = s.Values,
        });
        Broadcast(json);
    }

    public void PublishBattery(BatteryStatus b) => Broadcast(JsonSerializer.Serialize(new { type = "battery", battery = b }));

    public void PublishLocation(LocationFix f) => Broadcast(JsonSerializer.Serialize(new { type = "location", location = f }));

    private void Broadcast(string json)
    {
        lock (_lock) foreach (var c in _clients) c.Writer.TryWrite(json);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        using var _ = tcp;
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(5000);
            var req = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            if (req is null) return;
            var (path, query, headers) = req.Value;

            if (headers.TryGetValue("origin", out var origin) && !IsLoopbackOrigin(origin))
            {
                Rejected++;
                await WriteHttp(stream, 403, "{\"error\":\"origin not allowed\"}").ConfigureAwait(false);
                return;
            }
            if (path == "/health") { await WriteHttp(stream, 200, "{\"ok\":true}").ConfigureAwait(false); return; }
            if (!query.TryGetValue("token", out var tok) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tok), Encoding.UTF8.GetBytes(_token)))
            {
                Rejected++;
                await WriteHttp(stream, 401, "{\"error\":\"missing or invalid token\"}").ConfigureAwait(false);
                return;
            }
            if (path == "/status") { await WriteHttp(stream, 200, JsonSerializer.Serialize(_statusProvider())).ConfigureAwait(false); return; }
            if (path != "/sensors" || !headers.TryGetValue("sec-websocket-key", out var key))
            {
                await WriteHttp(stream, 404, "{\"error\":\"not found\"}").ConfigureAwait(false);
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WsGuid)));
            var resp = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), _cts.Token).ConfigureAwait(false);

            var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });
            lock (_lock) { _clients.Add(ch); DemandChanged?.Invoke(true); }
            using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var reader = Task.Run(async () => { try { await DrainClientFrames(stream, linkCts.Token); } finally { linkCts.Cancel(); } });
            try
            {
                await ch.Writer.WriteAsync(JsonSerializer.Serialize(new { type = "hello", schema = "pixelbridge.sensors.v1" }), linkCts.Token).ConfigureAwait(false);
                await foreach (var msg in ch.Reader.ReadAllAsync(linkCts.Token).ConfigureAwait(false))
                {
                    if (reader.IsCompleted) break;
                    await WriteWsText(stream, msg, linkCts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                linkCts.Cancel();
                lock (_lock) { _clients.Remove(ch); DemandChanged?.Invoke(_clients.Count > 0); }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host is "localhost" or "127.0.0.1" or "[::1]");

    private static async Task<(string path, Dictionary<string, string> query, Dictionary<string, string> headers)?> ReadRequestAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[8192];
        int len = 0;
        while (len < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
            if (n == 0) return null;
            len += n;
            var text = Encoding.ASCII.GetString(buf, 0, len);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            var lines = text[..end].Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2 || parts[0] != "GET") return null;
            var target = parts[1];
            var qi = target.IndexOf('?');
            var path = qi >= 0 ? target[..qi] : target;
            var query = new Dictionary<string, string>();
            if (qi >= 0)
                foreach (var kv in target[(qi + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = kv.IndexOf('=');
                    if (eq > 0) query[Uri.UnescapeDataString(kv[..eq])] = Uri.UnescapeDataString(kv[(eq + 1)..]);
                }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                var c = l.IndexOf(':');
                if (c > 0) headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim();
            }
            return (path, query, headers);
        }
        return null;
    }

    private static async Task WriteHttp(Stream s, int code, string body)
    {
        var reason = code switch { 200 => "OK", 401 => "Unauthorized", 403 => "Forbidden", _ => "Not Found" };
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await s.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteWsText(Stream s, string text, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var header = payload.Length < 126 ? new byte[] { 0x81, (byte)payload.Length }
            : payload.Length <= ushort.MaxValue ? new byte[] { 0x81, 126, (byte)(payload.Length >> 8), (byte)payload.Length }
            : throw new InvalidOperationException("message too large");
        await s.WriteAsync(header, ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>Consume (and discard) client frames; returns when the client closes or sends garbage.</summary>
    private static async Task DrainClientFrames(Stream s, CancellationToken ct)
    {
        var h = new byte[14];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await s.ReadExactlyAsync(h.AsMemory(0, 2), ct).ConfigureAwait(false);
                int op = h[0] & 0x0F;
                long len = h[1] & 0x7F;
                if (len == 126) { await s.ReadExactlyAsync(h.AsMemory(0, 2), ct).ConfigureAwait(false); len = (h[0] << 8) | h[1]; }
                else if (len == 127) return; // no large client messages accepted
                if (len > 4096) return;
                var rest = new byte[4 + len];
                await s.ReadExactlyAsync(rest, ct).ConfigureAwait(false);
                if (op == 0x8) return; // close
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptTask != null) { try { await _acceptTask.ConfigureAwait(false); } catch { } }
    }
}

/// <summary>Optional loopback-only OSC/UDP exporter: /pixelbridge/&lt;sensor&gt; ,f... (big-endian float32).</summary>
public sealed class OscExporter : IDisposable
{
    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
    private readonly IPEndPoint _target;

    public OscExporter(int port) => _target = new IPEndPoint(IPAddress.Loopback, port);

    public int Port => _target.Port;

    public void Send(SensorSample s)
    {
        var address = "/pixelbridge/" + SensorTypes.Name(s.Type);
        var tags = "," + new string('f', s.Values.Length);
        var ms = new MemoryStream();
        WritePadded(ms, address);
        WritePadded(ms, tags);
        Span<byte> f = stackalloc byte[4];
        foreach (var v in s.Values) { System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(f, v); ms.Write(f); }
        var bytes = ms.ToArray();
        try { _udp.Send(bytes, bytes.Length, _target); } catch (SocketException) { }
    }

    private static void WritePadded(Stream s, string str)
    {
        var b = Encoding.ASCII.GetBytes(str);
        s.Write(b);
        int pad = 4 - b.Length % 4;
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }

    public void Dispose() => _udp.Dispose();
}
