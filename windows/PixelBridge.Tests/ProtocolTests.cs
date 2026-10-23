using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PixelBridge.Core;
using PixelBridge.Core.Adb;
using PixelBridge.Core.Api;
using PixelBridge.Core.Protocol;
using PixelBridge.Core.Transport;

namespace PixelBridge.Tests;

public class FrameTests
{
    private static readonly JsonObject Vector = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "auth.json")))!.AsObject();

    [Fact]
    public void EncodesSharedVectorExactly()
    {
        var f = new Frame(65, [9], FrameFlags.Keyframe, 1);
        Assert.Equal((string)Vector["frameExample"]!["encodedHex"]!, Convert.ToHexStringLower(FrameWriter.Encode(f, 2)));
    }

    [Fact]
    public void HmacMatchesSharedVector()
    {
        var key = Convert.FromHexString((string)Vector["keyHex"]!);
        Assert.Equal((string)Vector["clientMacHex"]!, Convert.ToHexStringLower(AuthCrypto.ClientMac(key, "control", "AAAA", "BBBB")));
        Assert.Equal((string)Vector["serverMacHex"]!, Convert.ToHexStringLower(AuthCrypto.ServerMac(key, "control", "AAAA", "BBBB")));
    }

    [Fact]
    public async Task RoundTripsSequence()
    {
        var ms = new MemoryStream();
        var w = new FrameWriter(ms);
        w.Write(Frame.Json(MsgType.Status, "{}"));
        w.Write(new Frame(MsgType.VideoFrame, [1, 2, 3], FrameFlags.CodecConfig, -7));
        ms.Position = 0;
        var r = new FrameReader(ms, 100);
        Assert.Equal(MsgType.Status, (await r.ReadAsync(default)).Type);
        var f = await r.ReadAsync(default);
        Assert.Equal(new byte[] { 1, 2, 3 }, f.Payload);
        Assert.Equal(FrameFlags.CodecConfig, f.Flags);
        Assert.Equal(-7, f.TimestampNs);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("oversize")]
    [InlineData("flags")]
    [InlineData("sequence")]
    public async Task RejectsMalformedHeaders(string defect)
    {
        var bytes = FrameWriter.Encode(new Frame(1, [0, 0, 0, 0]), defect == "sequence" ? 5UL : 0UL);
        if (defect == "version") bytes[4] = 9;
        if (defect == "oversize") BinaryPrimitives.WriteUInt32BigEndian(bytes, 1_000_000);
        if (defect == "flags") bytes[7] = 0x80;
        await Assert.ThrowsAsync<ProtocolException>(() => new FrameReader(new MemoryStream(bytes), 65536).ReadAsync(default));
    }

    [Fact]
    public async Task TruncatedFrameIsEndOfStream()
    {
        var bytes = FrameWriter.Encode(new Frame(1, new byte[10]), 0)[..20];
        await Assert.ThrowsAsync<EndOfStreamException>(() => new FrameReader(new MemoryStream(bytes), 100).ReadAsync(default));
    }

    [Fact]
    public void RandomGarbageNeverCrashesTheParserUnexpectedly()
    {
        var rng = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            var buf = new byte[rng.Next(0, 80)];
            rng.NextBytes(buf);
            try { new FrameReader(new MemoryStream(buf), 64).ReadAsync(default).GetAwaiter().GetResult(); }
            catch (ProtocolException) { }
            catch (EndOfStreamException) { }
        }
    }
}

public class ModelTests
{
    private static Frame Sensor(int type, params float[] v)
    {
        var p = new byte[8 + 4 * v.Length];
        BinaryPrimitives.WriteInt32BigEndian(p, type);
        p[4] = 3; p[5] = (byte)v.Length;
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleBigEndian(p.AsSpan(8 + 4 * i), v[i]);
        return new Frame(MsgType.SensorSample, p, 0, 42);
    }

    [Fact] public void ParsesSensor() => Assert.Equal(new[] { 1f, 2f, 3f }, SensorSample.Parse(Sensor(4, 1, 2, 3)).Values);
    [Fact] public void RejectsNaNSensor() => Assert.Throws<ProtocolException>(() => SensorSample.Parse(Sensor(4, float.NaN)));
    [Fact] public void RejectsInfiniteSensor() => Assert.Throws<ProtocolException>(() => SensorSample.Parse(Sensor(4, float.PositiveInfinity)));

    [Fact]
    public void RejectsLyingSensorLength()
    {
        var f = Sensor(4, 1, 2);
        f.Payload[5] = 9;
        Assert.Throws<ProtocolException>(() => SensorSample.Parse(f));
    }

    [Fact]
    public void RejectsImpossibleCoordinates() =>
        Assert.Throws<ProtocolException>(() => LocationFix.Parse("""{"lat":123,"lon":0,"timeMs":0}"""));

    [Fact]
    public void NmeaHasValidChecksums()
    {
        var f = LocationFix.Parse("""{"lat":-31.95,"lon":115.86,"altM":10,"accM":5,"speedMps":1,"bearingDeg":90,"timeMs":1761200000000,"provider":"gps","satellitesUsed":8}""");
        foreach (var line in f.ToNmea().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.StartsWith("$GP", line);
            var star = line.LastIndexOf('*');
            byte c = 0;
            foreach (var ch in line[1..star]) c ^= (byte)ch;
            Assert.Equal(c.ToString("X2"), line[(star + 1)..]);
        }
        Assert.Contains(",S,", f.ToNmea());
        Assert.Contains(",E,", f.ToNmea());
    }

    [Fact]
    public void ClockSyncUsesMinimumRttSample()
    {
        var c = new ClockSync();
        c.Add(0, 1_000_500, 1_000);     // rtt 1000, offset 1_000_000
        c.Add(10_000, 2_000_000, 30_000); // rtt 20000 (worse)
        Assert.Equal(1_000_000, c.OffsetNs);
    }

    [Fact]
    public void AdbParserFlagsEmulatorAndStates()
    {
        var d = AdbClient.ParseDevices("List of devices attached\n0A1B2C3D4E5F device product:panther_beta model:Pixel_7 device:panther transport_id:1\nemulator-5562 offline transport_id:2\n");
        Assert.Equal(2, d.Count);
        Assert.Equal("panther", d[0].Device);
        Assert.False(d[0].IsEmulator);
        Assert.True(d[1].IsEmulator);
        Assert.Equal("offline", d[1].State);
    }
}

/// T20 security checks against real loopback listeners.
public class SecurityTests
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    [Fact]
    public async Task SensorApiRequiresTokenAndBlocksForeignOrigins()
    {
        int port = FreePort();
        await using var api = new SensorApiServer(port, "secret-token", () => new { ok = true });
        api.Start();
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{port}/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://127.0.0.1:{port}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://127.0.0.1:{port}/status?token=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{port}/status?token=secret-token")).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/status?token=secret-token");
        req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task SensorWebSocketStreamsSamples()
    {
        int port = FreePort();
        await using var api = new SensorApiServer(port, "t", () => new { });
        api.Start();
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/sensors?token=t"), default);
        var buf = new byte[4096];
        var hello = await ws.ReceiveAsync(buf, default);
        Assert.Contains("pixelbridge.sensors.v1", Encoding.UTF8.GetString(buf, 0, hello.Count));
        for (int i = 0; i < 20 && api.ClientCount == 0; i++) await Task.Delay(50);
        api.Publish(new SensorSample(4, 3, [0.1f, 0.2f, 0.3f], 99), 100);
        var msg = await ws.ReceiveAsync(buf, default);
        var j = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, msg.Count))!;
        Assert.Equal("gyroscope", (string)j["sensor"]!);
    }

    [Fact]
    public void ApiListenerIsLoopbackOnly()
    {
        int port = FreePort();
        var api = new SensorApiServer(port, "t", () => new { });
        api.Start();
        var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == port).ToList();
        Assert.NotEmpty(listeners);
        Assert.All(listeners, e => Assert.True(IPAddress.IsLoopback(e.Address)));
        api.DisposeAsync().AsTask().Wait();
    }

    /// Fake phone implementing the device side of the handshake (mirrors ChannelServer.kt).
    private static async Task<(int port, Task<bool> accepted)> FakePhone(byte[] deviceKey, string channel)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        var t = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            l.Stop();
            var s = c.GetStream();
            var w = new FrameWriter(s); var r = new FrameReader(s, 65536);
            var nonce = AuthCrypto.Nonce();
            w.Write(Frame.Json(MsgType.Hello, JsonSerializer.Serialize(new { protocolMajor = 1, protocolMinor = 0, channel, serverNonce = nonce, paired = true })));
            var resp = JsonNode.Parse((await r.ReadAsync(default)).Text)!;
            var ok = AuthCrypto.FixedTimeEquals(Convert.FromBase64String((string)resp["mac"]!), AuthCrypto.ClientMac(deviceKey, channel, nonce, (string)resp["clientNonce"]!));
            if (!ok) { w.Write(Frame.Json(MsgType.AuthFail, """{"code":"bad-mac"}""")); return false; }
            w.Write(Frame.Json(MsgType.AuthOk, JsonSerializer.Serialize(new { mac = Convert.ToBase64String(AuthCrypto.ServerMac(deviceKey, channel, nonce, (string)resp["clientNonce"]!)) })));
            return true;
        });
        await Task.Yield();
        return (port, t);
    }

    [Fact]
    public async Task HandshakeSucceedsWithMatchingKey()
    {
        var key = new byte[32]; key[0] = 7;
        var (port, accepted) = await FakePhone(key, "control");
        await using var c = await ChannelClient.ConnectAsync(port, "control", key, default);
        Assert.True(await accepted);
    }

    [Fact]
    public async Task HandshakeRejectsWrongKey()
    {
        var phoneKey = new byte[32]; phoneKey[0] = 1;
        var attackerKey = new byte[32]; attackerKey[0] = 2;
        var (port, accepted) = await FakePhone(phoneKey, "control");
        await Assert.ThrowsAsync<AuthException>(() => ChannelClient.ConnectAsync(port, "control", attackerKey, default));
        Assert.False(await accepted);
    }
}
