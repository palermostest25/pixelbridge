using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using PixelBridge.Core.Protocol;

namespace PixelBridge.Core.Transport;

public sealed class AuthException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record HelloInfo(string Channel, bool Paired, string? AppVersion, string? DeviceModel, int ProtocolMajor, int ProtocolMinor);

/// <summary>One authenticated TCP connection to a forwarded device channel (protocol section 3).</summary>
public sealed class ChannelClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly FrameReader _reader;
    private readonly FrameWriter _writer;
    private readonly Lock _writeLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public string Channel { get; }
    public HelloInfo Hello { get; private set; } = null!;
    public event Action<Frame>? FrameReceived;
    public event Action<Exception?>? Closed;
    public bool IsOpen { get; private set; } = true;

    private ChannelClient(TcpClient tcp, string channel)
    {
        _tcp = tcp;
        Channel = channel;
        _stream = tcp.GetStream();
        _reader = new FrameReader(_stream, ChannelLimits.MaxPayload(channel));
        _writer = new FrameWriter(_stream);
    }

    /// <summary>Connect to 127.0.0.1:hostPort and complete mutual HMAC authentication.</summary>
    public static async Task<ChannelClient> ConnectAsync(int hostPort, string channel, byte[]? key, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true, ReceiveBufferSize = 1 << 20 };
        var c = (ChannelClient?)null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            await tcp.ConnectAsync(IPAddress.Loopback, hostPort, timeout.Token).ConfigureAwait(false);
            c = new ChannelClient(tcp, channel);
            await c.HandshakeAsync(key, timeout.Token).ConfigureAwait(false);
            return c;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task HandshakeAsync(byte[]? key, CancellationToken ct)
    {
        Frame hello;
        try { hello = await _reader.ReadAsync(ct).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw new IOException("device closed before HELLO (app not listening?)"); }
        if (hello.Type != MsgType.Hello) throw new ProtocolException("expected HELLO");
        var j = JsonNode.Parse(hello.Text)!.AsObject();
        Hello = new HelloInfo((string)j["channel"]!, (bool)j["paired"]!, (string?)j["appVersion"], (string?)j["deviceModel"],
            (int)j["protocolMajor"]!, (int)j["protocolMinor"]!);
        if (Hello.ProtocolMajor != 1) throw new ProtocolException($"unsupported protocol major {Hello.ProtocolMajor}");
        if (Hello.Channel != Channel) throw new ProtocolException($"connected to '{Hello.Channel}', expected '{Channel}'");
        if (!Hello.Paired) throw new AuthException("not-paired", "device is not paired with this PC");
        if (key is null) throw new AuthException("no-key", "no pairing key stored on this PC");

        var serverNonce = (string)j["serverNonce"]!;
        var clientNonce = AuthCrypto.Nonce();
        var mac = AuthCrypto.ClientMac(key, Channel, serverNonce, clientNonce);
        Send(Frame.Json(MsgType.AuthResponse, JsonSerializer.Serialize(new { channel = Channel, clientNonce, mac = Convert.ToBase64String(mac) })));

        var reply = await _reader.ReadAsync(ct).ConfigureAwait(false);
        if (reply.Type == MsgType.AuthFail)
            throw new AuthException((string?)JsonNode.Parse(reply.Text)?["code"] ?? "auth", "device rejected authentication (re-pair needed)");
        if (reply.Type != MsgType.AuthOk) throw new ProtocolException("expected AUTH_OK");
        var proof = Convert.FromBase64String((string)JsonNode.Parse(reply.Text)!["mac"]!);
        if (!AuthCrypto.FixedTimeEquals(proof, AuthCrypto.ServerMac(key, Channel, serverNonce, clientNonce)))
            throw new AuthException("bad-server-mac", "device failed to prove the pairing key");
    }

    public void StartReading()
    {
        _loop = Task.Run(async () =>
        {
            Exception? err = null;
            try
            {
                while (!_cts.IsCancellationRequested)
                    FrameReceived?.Invoke(await _reader.ReadAsync(_cts.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { err = e; }
            IsOpen = false;
            Closed?.Invoke(err);
        });
    }

    public void Send(Frame f)
    {
        lock (_writeLock) _writer.Write(f);
    }

    public bool TrySend(Frame f)
    {
        try { Send(f); return true; } catch { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        IsOpen = false;
        _cts.Cancel();
        _tcp.Dispose();
        if (_loop != null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}
