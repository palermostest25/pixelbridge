using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using PixelBridge.Core;
using PixelBridge.Core.Api;
using PixelBridge.Core.Protocol;
using PixelBridge.Media;

namespace PixelBridge.App;

public sealed record NotificationItem(DateTime Received, string App, string? Sender, string? Text, bool Redacted);

/// <summary>Owns the device session and every pipeline; the UI only reads state and calls these methods.</summary>
public sealed class AppController : IAsyncDisposable
{
    public BridgeSettings Settings { get; }
    public DeviceSession Session { get; }
    public UsbSharing UsbSharing { get; }
    public VideoPipeline Video { get; }
    public AudioPipeline Audio { get; } = new();
    public SensorApiServer? Api { get; private set; }
    public OscExporter? Osc { get; private set; }
    public TiltMouse Tilt { get; } = new();
    public RadioController Radio { get; }
    public NmeaServer? Nmea { get; private set; }
    public LocationFix? Location { get; private set; }
    public DateTime LastLocationUtc { get; private set; }
    public bool LocationRequested { get; private set; }
    public string? LocationError { get; private set; }
    public TetherStatus? Tether { get; private set; }

    public BatteryStatus? Battery { get; private set; }
    public DateTime LastBatteryUtc { get; private set; }
    public readonly Dictionary<int, SensorSample> LatestSensors = new();
    public long SensorSamples;
    public DateTime LastSensorUtc { get; private set; }
    public ObservableCollection<NotificationItem> Notifications { get; } = new();

    public bool PrivacyMode { get; private set; }
    public bool CameraRequested { get; private set; }
    public bool MicRequested { get; private set; }
    public string? CameraError { get; private set; }
    public string? MicError { get; private set; }
    public string VirtualCameraState { get; private set; } = "not started";
    public bool VirtualCameraActive { get; private set; }

    public volatile bool PreviewVisible;
    public event Action<SensorSample>? SensorReceived;
    public event Action<Nv12Frame>? PreviewFrame;
    public event Action<string>? Toast;

    private StreamWriter? _sensorExport;
    private DateTime _sensorExportUntil;
    private readonly Lock _exportLock = new();
    private FileStream? _videoRecord;
    private DateTime _videoRecordUntil;
    private bool _tempWarned;

    public AppController()
    {
        Settings = BridgeSettings.Load();
        if (string.IsNullOrEmpty(Settings.ApiToken)) { Settings.ApiToken = SensorApiServer.NewToken(); Settings.Save(); }
        Session = new DeviceSession(Settings);
        UsbSharing = new UsbSharing(Session);
        Session.StateChanged += (s, detail) => { if (s == LinkState.Connected) _ = UsbSharing.RefreshAsync(); };
        Video = new VideoPipeline(Session) { Settings = Settings };
        Radio = new RadioController(Session);
        Session.Location += OnLocation;
        Audio.Gain = Settings.MicGain;
        Audio.JitterMs = Settings.MicJitterMs;

        Session.Sensor += OnSensor;
        Session.Battery += OnBattery;
        Session.AudioFrame += f => { if (!PrivacyMode && MicRequested) Audio.Push(f.Payload); };
        Session.VideoFrame += OnVideoFrameForRecording;
        Session.Notification += OnNotification;
        Session.StreamEvent += OnStreamEvent;
        Session.StateChanged += OnState;
        Session.DeviceError += e => Toast?.Invoke($"Phone: {e["message"]}");
        Video.FrameDecoded += f => PreviewFrame?.Invoke(f);
    }

    public void Start()
    {
        Session.Start();
        if (Settings.SensorWebSocketEnabled) StartApi();
        if (Settings.OscEnabled) Osc = new OscExporter(Settings.OscPort);
        Session.SetSensorDemand("osc", Settings.OscEnabled);
        if (Settings.NmeaEnabled) { try { Nmea = new NmeaServer(Settings.NmeaPort); } catch (Exception e) { Log.Warn($"NMEA feed failed: {e.Message}"); } }
        if (Settings.MicOutputDevice != null) Audio.SetCableOutput(Settings.MicOutputDevice);
        StartVirtualCamera();
    }

    private void StartApi()
    {
        try
        {
            Api = new SensorApiServer(Settings.SensorWebSocketPort, Settings.ApiToken!, StatusSnapshot);
            Api.DemandChanged += on => Session.SetSensorDemand("api", on);
            Api.Start();
        }
        catch (Exception e) { Log.Warn($"sensor API failed to start: {e.Message}"); Api = null; }
    }

    // ---------------------------------------------------------------- virtual camera

    public void StartVirtualCamera()
    {
        if (!Settings.VirtualCameraEnabled) { VirtualCameraState = "disabled in Settings"; return; }
        if (!VirtualCamera.IsSupportedOs) { VirtualCameraState = "unsupported OS (needs Windows 11); use OBS fallback"; return; }
        if (!VirtualCamera.IsSourceRegistered) { VirtualCameraState = "not installed — run tools\\install-vcam.ps1 (admin, one time)"; return; }
        try
        {
            int hr = VirtualCamera.Start();
            VirtualCameraActive = hr >= 0;
            VirtualCameraState = hr >= 0 ? "registered with Windows as \"PixelBridge Camera\"" : $"MFCreateVirtualCamera failed 0x{hr:X8}";
            Log.Info($"virtual camera start hr=0x{hr:X8}");
        }
        catch (DllNotFoundException) { VirtualCameraState = "PixelBridge.Native.dll missing"; }
    }

    public void StopVirtualCamera()
    {
        if (!VirtualCameraActive) return;
        Video.GoOffline();
        VirtualCamera.Stop();
        VirtualCameraActive = false;
        VirtualCameraState = "stopped";
    }

    // ---------------------------------------------------------------- streams

    public void StartCamera()
    {
        if (PrivacyMode) { Toast?.Invoke("Privacy mode is on"); return; }
        CameraRequested = true;
        CameraError = null;
        Video.Paused = false;
        Video.Enabled = true;
        Session.StartStream("video", new { cameraId = Settings.CameraId, width = Settings.VideoWidth, height = Settings.VideoHeight,
            fps = Settings.SavePower ? Math.Min(15, Settings.VideoFps) : Settings.VideoFps, bitrate = Settings.VideoBitrate, zoomRatio = Settings.CameraZoom, stabilization = Settings.CameraStabilization });
    }

    public void StopCamera()
    {
        CameraRequested = false;
        Session.StopStream("video");
        Video.GoOffline();
    }

    public void SwitchCamera(string id)
    {
        Settings.CameraId = id;
        Settings.Save();
        if (CameraRequested) StartCamera();
    }

    public CameraControls Camera => new(Session.Capabilities, Settings);
    public void UpdateCameraControls() { Settings.Save(); if (CameraRequested) Session.StartStream("video", Camera.UpdateOptions(Settings)); }
    public void SelectLens(CameraLens lens) { var changed = Settings.CameraId != lens.CameraId; Settings.CameraId = lens.CameraId; Settings.CameraZoom = lens.Zoom; Settings.Save(); if (CameraRequested) { if (changed) StartCamera(); else UpdateCameraControls(); } }

    public void StartMic()
    {
        if (PrivacyMode) { Toast?.Invoke("Privacy mode is on"); return; }
        MicRequested = true;
        MicError = null;
        Session.StartStream("audio", new { rateHz = 48000 });
    }

    public void StopMic()
    {
        MicRequested = false;
        Session.StopStream("audio");
    }

    /// <summary>Pause all acquisition immediately: camera, mic and sensors stop on the phone; virtual camera shows the placeholder.</summary>
    public void SetPrivacy(bool on)
    {
        PrivacyMode = on;
        Session.PauseSensors(on);
        if (on)
        {
            Session.StopStream("video");
            Session.StopStream("audio");
            Session.StopStream("location");
            Location = null;
            Session.StopStream("sensors");
            Video.GoOffline(paused: true);
            Audio.StopRecording();
            Tilt.Enabled = false;
            Log.Info("privacy mode ON: all acquisition stopped");
        }
        else
        {
            Session.RefreshSensorDemand();
            if (CameraRequested) StartCamera();
            if (LocationRequested) StartLocation();
            if (MicRequested) StartMic();
            Log.Info("privacy mode OFF");
        }
    }

    public void StartLocation()
    {
        if (PrivacyMode) { Toast?.Invoke("Privacy mode is on"); return; }
        LocationRequested = true; LocationError = null;
        Session.StartStream("location", new { intervalMs = 1000 });
    }

    public void StopLocation()
    {
        LocationRequested = false;
        Session.StopStream("location");
        Location = null;
    }

    public void SetNmea(bool on)
    {
        Settings.NmeaEnabled = on; Settings.Save();
        Nmea?.Dispose(); Nmea = null;
        if (on) { try { Nmea = new NmeaServer(Settings.NmeaPort); } catch (Exception e) { Toast?.Invoke($"NMEA port {Settings.NmeaPort} unavailable: {e.Message}"); } }
    }

    public async Task RefreshTetherAsync() => Tether = await TetherProbe.RunAsync(Session);

    private void OnLocation(LocationFix f)
    {
        if (PrivacyMode) return;
        Location = f;
        LastLocationUtc = DateTime.UtcNow;
        Nmea?.Publish(f);
        if (Settings.ApiIncludeLocation) Api?.PublishLocation(f);
    }

    public void SetOsc(bool on) { Settings.OscEnabled = on; Settings.Save(); Osc?.Dispose(); Osc = on ? new OscExporter(Settings.OscPort) : null; Session.SetSensorDemand("osc", on); }

    public void SetSensorRate(int hz)
    {
        Settings.SensorRateHz = hz;
        Settings.Save();
        Session.RefreshSensorDemand();
    }

    // ---------------------------------------------------------------- events

    private void OnState(LinkState s, string detail)
    {
        if (s == LinkState.Connected)
        {
            _tempWarned = false;
            if (PrivacyMode) Session.StopStream("sensors");
            // Resume what the user had turned on (camera/mic need the phone app to be foreground-started, which connect does).
            if (CameraRequested && !PrivacyMode) StartCamera();
            if (MicRequested && !PrivacyMode) StartMic();
            if (LocationRequested && !PrivacyMode) StartLocation();
            _ = Radio.RefreshAsync();
        }
        else
        {
            Video.GoOffline();
            Tilt.Enabled = false;
        }
    }

    private void OnSensor(SensorSample s)
    {
        if (PrivacyMode) return;
        lock (LatestSensors) LatestSensors[s.Type] = s;
        Interlocked.Increment(ref SensorSamples);
        LastSensorUtc = DateTime.UtcNow;
        var hostNs = Session.Clock.HasEstimate ? Session.Clock.ToHostNs(s.TimestampNs) : HostClock.NowNs();
        Api?.Publish(s, hostNs);
        Osc?.Send(s);
        Tilt.OnSensor(s, Settings.TiltMouseSensitivity);
        lock (_exportLock)
        {
            if (_sensorExport != null)
            {
                _sensorExport.WriteLine($"{s.TimestampNs},{hostNs},{SensorTypes.Name(s.Type)},{s.Accuracy},{string.Join(",", s.Values.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}");
                if (DateTime.UtcNow > _sensorExportUntil) StopSensorExportLocked();
            }
        }
        SensorReceived?.Invoke(s);
    }

    private void OnBattery(BatteryStatus b)
    {
        Battery = b;
        LastBatteryUtc = DateTime.UtcNow;
        Api?.PublishBattery(b);
        if (b.TempC is { } t && t >= Settings.BatteryTempWarnC && !_tempWarned)
        {
            _tempWarned = true;
            Toast?.Invoke($"Pixel battery at {t:0.0} °C. Consider stopping camera/mic. If the phone is hot, swollen or smells, disconnect it.");
        }
    }

    private void OnNotification(JsonObject j)
    {
        if (!Settings.NotificationsEnabled) return;
        bool redacted = (bool?)j["redacted"] ?? true;
        var item = new NotificationItem(DateTime.Now, (string?)j["appLabel"] ?? (string?)j["app"] ?? "?",
            redacted || !Settings.NotificationShowContent ? null : (string?)j["senderDisplay"],
            redacted || !Settings.NotificationShowContent ? null : (string?)j["text"], redacted || !Settings.NotificationShowContent);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Notifications.Insert(0, item);
            while (Notifications.Count > 200) Notifications.RemoveAt(Notifications.Count - 1);
        });
    }

    private void OnStreamEvent(byte type, JsonObject j)
    {
        var kind = (string?)j["kind"];
        if (type == MsgType.StreamError)
        {
            var msg = $"{j["code"]}: {j["message"]}";
            if (kind == "video") { CameraError = msg; Toast?.Invoke("Camera: " + msg); }
            if (kind == "audio") { MicError = msg; Toast?.Invoke("Microphone: " + msg); }
            if (kind == "location") { LocationError = msg; Toast?.Invoke("Location: " + msg); }
        }
        if (type == MsgType.StreamStopped && kind == "video" && (string?)j["reason"] is "thermal" or "paused") CameraError = $"stopped by phone ({j["reason"]})";
    }

    // ---------------------------------------------------------------- recording / export (operator-initiated, bounded)

    public string StartSensorExport(int seconds = 60)
    {
        lock (_exportLock)
        {
            StopSensorExportLocked();
            Directory.CreateDirectory(AppPaths.RecordingsDir);
            var path = Path.Combine(AppPaths.RecordingsDir, $"pixel-sensors-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _sensorExport = new StreamWriter(path);
            Session.SetSensorDemand("export", true);
            _sensorExport.WriteLine("device_timestamp_ns,host_timestamp_ns,sensor,accuracy,v0,v1,v2,v3,v4");
            _sensorExportUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 600));
            return path;
        }
    }

    public bool SensorExportActive { get { lock (_exportLock) return _sensorExport != null; } }
    public void StopSensorExport() { lock (_exportLock) StopSensorExportLocked(); }
    private void StopSensorExportLocked() { _sensorExport?.Dispose(); _sensorExport = null; Session.SetSensorDemand("export", false); }

    public string StartVideoRecording(int seconds = 60)
    {
        StopVideoRecording();
        Directory.CreateDirectory(AppPaths.RecordingsDir);
        var path = Path.Combine(AppPaths.RecordingsDir, $"pixel-camera-{DateTime.Now:yyyyMMdd-HHmmss}.h264");
        _videoRecord = File.Create(path);
        _videoRecordUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 60));
        Session.RequestIdr();
        Log.Info("camera recording started (raw H.264, 60 s max)");
        return path;
    }

    public bool VideoRecordingActive => _videoRecord != null;

    public void StopVideoRecording()
    {
        var f = Interlocked.Exchange(ref _videoRecord, null);
        f?.Dispose();
    }

    private void OnVideoFrameForRecording(Frame f)
    {
        var rec = _videoRecord;
        if (rec == null || PrivacyMode) return;
        try { lock (rec) rec.Write(f.Payload); } catch (ObjectDisposedException) { }
        if (DateTime.UtcNow > _videoRecordUntil) StopVideoRecording();
    }

    // ---------------------------------------------------------------- misc

    public object StatusSnapshot() => new
    {
        link = Session.State.ToString(),
        detail = Session.StateDetail,
        device = Session.IsConnected ? Session.DeviceProps : null,
        battery = Battery,
        modules = Session.ModuleStatus?["modules"]?.ToJsonString(),
        sensorsLive = (DateTime.UtcNow - LastSensorUtc).TotalSeconds < 1.5,
        cameraLive = Video.IsLive,
        micLive = Audio.IsLive,
        privacy = PrivacyMode,
        rttMs = Session.Clock.LastRttMs,
    };

    public static void SetStartAtLogin(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
        if (enable) key.SetValue("PixelBridge", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("PixelBridge", throwOnMissingValue: false);
    }

    public static bool IsStartAtLoginEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("PixelBridge") is string s && s.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
    }

    public string? FindScrcpy()
    {
        return PixelBridge.Core.Adb.AdbClient.LocateScrcpy(Settings.ScrcpyPath);
    }

    public void LaunchScrcpy(bool virtualDisplay)
    {
        var exe = FindScrcpy();
        if (exe is null) { Toast?.Invoke("scrcpy.exe not found; set its path in Settings"); return; }
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        foreach (var a in new[] { "-s", Session.Serial, "--no-audio", "--force-adb-forward", "--max-size=1280", "--max-fps=15", "--turn-screen-off", "--window-title", virtualDisplay ? "PixelBridge (virtual display)" : "PixelBridge phone" })
            psi.ArgumentList.Add(a);
        if (virtualDisplay)
        {
            psi.ArgumentList.Add("--new-display=1280x800/200");
            psi.ArgumentList.Add($"--start-app={DeviceSession.Package}");
        }
        try { System.Diagnostics.Process.Start(psi); } catch (Exception e) { Toast?.Invoke("Could not open phone screen: " + e.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        Tilt.Enabled = false;
        StopSensorExport();
        StopVideoRecording();
        StopVirtualCamera();
        Video.Dispose();
        Audio.Dispose();
        Osc?.Dispose();
        Nmea?.Dispose();
        if (Api != null) await Api.DisposeAsync();
        await Session.DisposeAsync();
    }
}
