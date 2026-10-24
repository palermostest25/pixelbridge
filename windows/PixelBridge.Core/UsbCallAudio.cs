using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace PixelBridge.Core;

public interface ICallAudioDevice : IDisposable
{
    event Action<byte[]>? Microphone;
    void Start();
    void Play(byte[] pcm);
}

/// <summary>Bounded PCM framing for the authorized ADB pipe. No recording or network listener.</summary>
public static class CallAudioProtocol
{
    public const int MaximumFrame = 6400;
    public static async Task ReadHeaderAsync(Stream source, CancellationToken ct)
    {
        var header = new byte[12]; await source.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 4).SequenceEqual("PBA1"u8) || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)) != 16000 || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8)) != 1)
            throw new IOException("Update the phone app to enable USB call audio");
    }
    public static async Task<byte[]> ReadPcmAsync(Stream source, CancellationToken ct)
    {
        while (true) {
        var header = new byte[5]; await source.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length < 0 || length > MaximumFrame) throw new IOException("Invalid call audio frame size");
        var data = new byte[length]; await source.ReadExactlyAsync(data, ct);
        if (header[0] == 4 && length == 24) continue; // Counters only; never audio or identifiers.
        if (header[0] == 3) throw new IOException(Encoding.UTF8.GetString(data));
        if (header[0] != 1 || length == 0 || length % 2 != 0) throw new IOException("Invalid call audio samples");
        return data;
        }
    }
    public static byte[] PcmFrame(byte[] pcm, bool muted)
    {
        if (pcm.Length == 0 || pcm.Length > MaximumFrame || pcm.Length % 2 != 0) throw new ArgumentException("Invalid microphone samples");
        var frame = new byte[pcm.Length + 5]; frame[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), pcm.Length);
        if (!muted) pcm.CopyTo(frame, 5);
        return frame;
    }
}

/// <summary>Starts audio only for a live call; closes capture, playback and helper on privacy/disconnect.</summary>
public sealed class UsbCallAudio : IDisposable
{
    private readonly DeviceSession _session;
    private readonly Func<ICallAudioDevice> _createDevice;
    private readonly object _gate = new();
    private Connection? _connection;
    private string? _callId;
    private volatile bool _allowed = true, _enabled = true;
    private volatile bool _microphoneMuted, _listeningMuted;
    public bool Enabled => _enabled;
    public bool Active { get; private set; }
    public bool ListeningMuted => _listeningMuted;
    public string Status { get; private set; } = "Computer microphone and speakers are ready for your next call.";
    public UsbCallAudio(DeviceSession session, Func<ICallAudioDevice> createDevice)
    {
        _session = session; _createDevice = createDevice;
        session.RadioEvent += OnEvent;
        session.StateChanged += OnState;
    }
    private void OnState(LinkState state, string detail) { if (state != LinkState.Connected) { Stop(); _callId = null; } else if (_allowed && Enabled) _ = RetryAsync(); }
    private void OnEvent(JsonObject row) { if ((string?)row["event"] == "phone.calls") Update(row); }
    private void Update(JsonObject row)
    {
        if (!_allowed || !Enabled) return;
        _microphoneMuted = (bool?)row["muted"] == true;
        var call = (row["calls"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => (string?)x["state"] is "active" or "dialing");
        string? id = (string?)call?["id"];
        lock (_gate) {
            if (!_allowed || !Enabled || !_session.IsConnected) return;
            if (id == _callId) return;
            StopLocked(); _callId = id;
            if (id == null) return;
            var connection = new Connection(_createDevice, () => _microphoneMuted, () => _listeningMuted);
            _connection = connection; Status = "Connecting USB call audio…";
            _ = RunAsync(connection);
        }
    }
    private async Task RunAsync(Connection connection)
    {
        try {
            await connection.RunAsync(_session, () => { lock (_gate) { if (_connection == connection) { Active = true; Status = "Call audio is using this computer over USB."; } } });
        } catch (OperationCanceledException) { }
        catch (Exception e) { lock (_gate) { if (_connection == connection) { Active = false; Status = "Computer call audio: " + e.Message; } } }
        finally { connection.Dispose(); lock (_gate) { if (_connection == connection) { _connection = null; Active = false; } } }
    }
    public void SetEnabled(bool enabled) { _enabled = enabled; Stop(); _callId = null; if (enabled) _ = RetryAsync(); else Status = "Call audio is on the phone or its connected headset."; }
    public void SetAllowed(bool allowed) { _allowed = allowed; if (!allowed) { Stop(); _callId = null; } else if (Enabled) _ = RetryAsync(); }
    public void ToggleListeningMute() => _listeningMuted = !_listeningMuted;
    public async Task RetryAsync()
    {
        if (!_allowed || !Enabled || !_session.IsConnected) return;
        try {
            var response = await _session.RadioAsync("phone.status");
            if ((bool?)response["ok"] == true && response["result"]?["calls"] is JsonObject calls) { _callId = null; Update(calls); }
        } catch (Exception e) { Status = e.Message; }
    }
    private void Stop() { lock (_gate) StopLocked(); }
    private void StopLocked() { var connection = _connection; _connection = null; connection?.Dispose(); Active = false; Status = "Computer microphone and speakers are ready for your next call."; }
    public void Dispose() { _session.RadioEvent -= OnEvent; _session.StateChanged -= OnState; Stop(); }

    private sealed class Connection(Func<ICallAudioDevice> createDevice, Func<bool> microphoneMuted, Func<bool> listeningMuted) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<byte[]> _microphone = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private ICallAudioDevice? _device;
        private Process? _process;
        private int _disposed;
        private readonly object _resources = new();
        public async Task RunAsync(DeviceSession session, Action ready)
        {
            var ct = _stop.Token;
            var adb = session.Adb ?? throw new IOException("Connect your phone first");
            var result = await adb.ShellAsync(session.Serial, "pm path dev.pixelbridge.app", TimeSpan.FromSeconds(8), ct);
            var path = result.StdOut.Split('\n').FirstOrDefault(x => x.StartsWith("package:"))?[8..].Trim() ?? "";
            if (!result.Ok || !Regex.IsMatch(path, @"^/[A-Za-z0-9_/.=+~\-]+$")) throw new IOException("Could not locate the phone call-audio helper");
            ct.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(adb.AdbPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-s", session.Serial, "shell", "-T", $"CLASSPATH='{path}' app_process / dev.pixelbridge.app.phone.ShellCallAudio" }) start.ArgumentList.Add(arg);
            Process process;
            ICallAudioDevice device;
            lock (_resources) {
                ct.ThrowIfCancellationRequested();
                process = new Process { StartInfo = start }; _process = process; process.Start();
                device = createDevice(); _device = device; device.Microphone += OnMicrophone; device.Start();
            }
            // Discard diagnostic output, keeping buffers bounded and call audio out of logs.
            var diagnostics = Task.Run(async () => { var buffer = new char[1024]; while (await process.StandardError.ReadAsync(buffer, ct) > 0) { } }, ct);
            using var cancel = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var writer = Task.Run(async () => {
                await foreach (var pcm in _microphone.Reader.ReadAllAsync(ct)) {
                    var frame = CallAudioProtocol.PcmFrame(pcm, microphoneMuted());
                    await process.StandardInput.BaseStream.WriteAsync(frame, ct);
                    await process.StandardInput.BaseStream.FlushAsync(ct);
                }
            }, ct);
            try {
                await CallAudioProtocol.ReadHeaderAsync(process.StandardOutput.BaseStream, ct);
                bool first = true;
                while (!ct.IsCancellationRequested) {
                    var pcm = await CallAudioProtocol.ReadPcmAsync(process.StandardOutput.BaseStream, ct);
                    if (writer.IsFaulted) throw writer.Exception!.GetBaseException();
                    if (first) { first = false; ready(); }
                    device.Play(listeningMuted() ? new byte[pcm.Length] : pcm);
                }
            } finally { _stop.Cancel(); _microphone.Writer.TryComplete(); try { await writer; } catch { } try { await diagnostics; } catch { } }
        }
        private void OnMicrophone(byte[] pcm) { if (pcm.Length > 0 && pcm.Length <= CallAudioProtocol.MaximumFrame && pcm.Length % 2 == 0) _microphone.Writer.TryWrite(pcm); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel(); _microphone.Writer.TryComplete();
            lock (_resources) {
            if (_device != null) { _device.Microphone -= OnMicrophone; try { _device.Dispose(); } catch { } }
            try { _process?.StandardInput.Close(); if (_process is { HasExited: false }) _process.Kill(true); } catch { }
            _process?.Dispose();
            }
        }
    }
}
