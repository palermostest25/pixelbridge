using System.Text.Json.Nodes;
using PixelBridge.Core;
using PixelBridge.Core.Api;
using PixelBridge.Core.Protocol;
using PixelBridge.Linux.Media;

namespace PixelBridge.Linux;

/// <summary>Linux counterpart of windows/PixelBridge.App/AppController.cs, sharing PixelBridge.Core.</summary>
public sealed class AppModel : IAsyncDisposable
{
    public BridgeSettings Settings { get; } = BridgeSettings.Load();
    public DeviceSession Session { get; }
    internal FfmpegVideo Video { get; } = new();
    internal PulseMic Mic { get; } = new();
    public SensorApiServer? Api { get; private set; }

    public BatteryStatus? Battery { get; private set; }
    public LocationFix? Location { get; private set; }
    public DateTime LastLocationUtc { get; private set; }
    public readonly Dictionary<int, SensorSample> LatestSensors = new();
    public DateTime LastSensorUtc { get; private set; }
    public bool Privacy { get; private set; }
    public bool CameraOn { get; private set; }
    public bool MicOn { get; private set; }
    public bool LocationOn { get; private set; }
    public string? CameraError { get; private set; }
    public string? MicError { get; private set; }
    public TetherStatus? Tether { get; private set; }
    public JsonObject? Wifi { get; private set; }
    public List<JsonObject> BleDevices { get; } = new();
    public bool Scanning { get; private set; }
    public string RadioStatus { get; private set; } = "";
    private JsonObject? _videoConfig;

    public event Action<SensorSample>? SensorReceived;
    public event Action<string>? Toast;

    public AppModel()
    {
        if (string.IsNullOrEmpty(Settings.ApiToken)) { Settings.ApiToken = SensorApiServer.NewToken(); Settings.Save(); }
        Session = new DeviceSession(Settings);
        Session.Sensor += s =>
        {
            if (Privacy) return;
            lock (LatestSensors) LatestSensors[s.Type] = s;
            LastSensorUtc = DateTime.UtcNow;
            Api?.Publish(s, HostClock.NowNs());
            SensorReceived?.Invoke(s);
        };
        Session.Battery += b => Battery = b;
        Session.Location += f => { if (!Privacy) { Location = f; LastLocationUtc = DateTime.UtcNow; } };
        Session.VideoConfig += c =>
        {
            _videoConfig = c;
            if (CameraOn) Video.Start((int?)c["width"] ?? 1280, (int?)c["height"] ?? 720, toLoopback: true);
        };
        Session.VideoFrame += f => Video.Feed(f.Payload, f.Flags.HasFlag(FrameFlags.Keyframe), f.Flags.HasFlag(FrameFlags.CodecConfig));
        Session.AudioFrame += f => Mic.Push(f.Payload);
        Session.StreamEvent += (t, j) =>
        {
            if (t != MsgType.StreamError) return;
            var msg = $"{j["message"]}";
            if ((string?)j["kind"] == "video") CameraError = msg;
            if ((string?)j["kind"] == "audio") MicError = msg;
            Toast?.Invoke(msg);
        };
        Session.RadioEvent += OnRadioEvent;
        Session.StateChanged += (s, detail) =>
        {
            if (s == LinkState.Connected)
            {
                if (CameraOn && !Privacy) StartCamera();
                if (MicOn && !Privacy) _ = StartMicAsync();
                if (LocationOn && !Privacy) StartLocation();
                _ = RefreshRadioAsync();
            }
            else Video.Stop();
        };
    }

    public void Start()
    {
        Session.Start();
        if (Settings.SensorWebSocketEnabled)
        {
            try { Api = new SensorApiServer(Settings.SensorWebSocketPort, Settings.ApiToken!, () => new { link = Session.State.ToString(), battery = Battery }); Api.Start(); }
            catch (Exception e) { Log.Warn($"sensor API: {e.Message}"); }
        }
    }

    public void StartCamera()
    {
        if (Privacy) return;
        if (!FfmpegVideo.Available) { CameraError = "ffmpeg is not installed (sudo apt install ffmpeg)"; return; }
        CameraOn = true; CameraError = null;
        Session.StartStream("video", new { cameraId = Settings.CameraId, width = Settings.VideoWidth, height = Settings.VideoHeight, fps = Settings.VideoFps, bitrate = Settings.VideoBitrate });
    }

    public void StopCamera() { CameraOn = false; Session.StopStream("video"); Video.Stop(); }

    public void SwitchCamera(string id) { Settings.CameraId = id; Settings.Save(); if (CameraOn) StartCamera(); }

    public async Task StartMicAsync()
    {
        if (Privacy) return;
        MicOn = true;
        MicError = await Mic.EnableAsync();
        Session.StartStream("audio", new { rateHz = 48000 });
    }

    public async Task StopMicAsync() { MicOn = false; Session.StopStream("audio"); await Mic.DisableAsync(); }

    public void StartLocation() { if (Privacy) return; LocationOn = true; Session.StartStream("location", new { intervalMs = 1000 }); }
    public void StopLocation() { LocationOn = false; Session.StopStream("location"); Location = null; }

    public void SetPrivacy(bool on)
    {
        Privacy = on;
        if (on) { foreach (var k in new[] { "video", "audio", "sensors", "location" }) Session.StopStream(k); Video.Stop(); Location = null; }
        else
        {
            Session.StartStream("sensors", new { rateHz = Settings.SensorRateHz });
            if (CameraOn) StartCamera(); if (MicOn) _ = StartMicAsync(); if (LocationOn) StartLocation();
        }
    }

    public async Task RefreshTetherAsync() => Tether = await TetherProbe.RunAsync(Session);

    private async Task<JsonObject?> Radio(string op, object? args = null, int timeout = 15)
    {
        try
        {
            var r = await Session.RadioAsync(op, args, TimeSpan.FromSeconds(timeout));
            if ((bool?)r["ok"] == true) return r["result"]?.AsObject() ?? new JsonObject();
            RadioStatus = (string?)r["message"] ?? "failed";
        }
        catch (Exception e) { RadioStatus = e.Message; }
        return null;
    }

    public async Task RefreshRadioAsync() => Wifi = await Radio("wifi.status");

    public async Task ScanBleAsync()
    {
        lock (BleDevices) BleDevices.Clear();
        if (await Radio("ble.scan.start", new { durationMs = 15000 }) != null) Scanning = true;
    }

    private void OnRadioEvent(JsonObject e)
    {
        switch ((string?)e["event"])
        {
            case "ble.scanResult":
                lock (BleDevices)
                {
                    var i = BleDevices.FindIndex(d => (string?)d["address"] == (string?)e["address"]);
                    if (i >= 0) BleDevices[i] = e; else BleDevices.Add(e);
                }
                break;
            case "ble.scanStopped": Scanning = false; break;
        }
    }

    public void LaunchScrcpy()
    {
        var exe = PixelBridge.Core.Adb.AdbClient.LocateScrcpy(Settings.ScrcpyPath);
        if (exe is null) { Toast?.Invoke("scrcpy not found (sudo apt install scrcpy)"); return; }
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in new[] { "-s", Session.Serial, "--no-audio", "--window-title", "PixelBridge phone" }) psi.ArgumentList.Add(a);
        System.Diagnostics.Process.Start(psi);
    }

    public async ValueTask DisposeAsync()
    {
        Video.Dispose();
        await Mic.DisableAsync();
        if (Api != null) await Api.DisposeAsync();
        await Session.DisposeAsync();
    }
}
