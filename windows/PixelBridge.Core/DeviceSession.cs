using System.Text.Json;
using System.Text.Json.Nodes;
using PixelBridge.Core.Adb;
using PixelBridge.Core.Protocol;
using PixelBridge.Core.Transport;

namespace PixelBridge.Core;

public enum LinkState
{
    NoAdb, Disconnected, Unauthorized, Offline, WrongDevice, ApkMissing, ApkOutdated, NotPaired, Connecting, Connected, Error,
}

public static class LinkStateText
{
    /// <summary>User-facing label shared by every desktop UI.</summary>
    public static string Label(this LinkState s) => s switch
    {
        LinkState.Connected => "Connected", LinkState.Disconnected => "Not connected", LinkState.Connecting => "Connecting…",
        LinkState.NotPaired => "Not paired", LinkState.ApkMissing => "Phone app missing", LinkState.ApkOutdated => "Phone app outdated",
        LinkState.Unauthorized => "USB debugging not allowed", LinkState.NoAdb => "adb not found", LinkState.WrongDevice => "New phone",
        LinkState.Offline => "Phone offline", _ => "Error",
    };
}

public sealed class BridgeSettings
{
    /// <summary>Phone serial to use. Empty = the single attached, authorised, non-emulator phone (never guessed among several).</summary>
    public string TargetSerial { get; set; } = "";
    /// <summary>Device codenames (ro.product.device) the user has approved. Others wait for an explicit "Use this phone".</summary>
    public List<string> ApprovedDevices { get; set; } = ["panther"];
    public string WirelessEndpoint { get; set; } = "";
    public string WirelessSerial { get; set; } = "";
    public string WirelessService { get; set; } = "";
    public bool WirelessSecure { get; set; }
    public string? AdbPath { get; set; }
    public string? ScrcpyPath { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool UseComputerCallAudio { get; set; } = true;
    public bool ShowOnConnect { get; set; } = true;
    public bool AutoUpdateCompanion { get; set; } = true;
    public int SensorRateHz { get; set; } = 50;
    public string CameraId { get; set; } = "0";
    public double CameraZoom { get; set; } = 1;
    public string CameraStabilization { get; set; } = "auto";
    public string CameraOrientation { get; set; } = "auto";
    public bool SavePower { get; set; } = true;
    public int VideoWidth { get; set; } = 1280;
    public int VideoHeight { get; set; } = 720;
    public int VideoFps { get; set; } = 30;
    public int VideoBitrate { get; set; } = 4_000_000;
    public bool StartAtLogin { get; set; }
    public bool NotificationsEnabled { get; set; }
    public string? MicOutputDevice { get; set; }
    public bool SensorWebSocketEnabled { get; set; } = true;
    public int SensorWebSocketPort { get; set; } = 27200;
    public bool VirtualCameraEnabled { get; set; } = true;
    public string? ApiToken { get; set; }
    public bool OscEnabled { get; set; }
    public int OscPort { get; set; } = 9000;
    public string? MicMonitorDevice { get; set; }
    public float MicGain { get; set; } = 1.0f;
    public int MicJitterMs { get; set; } = 60;
    public bool PreviewMirror { get; set; }
    public bool NotificationShowContent { get; set; }
    public double TiltMouseSensitivity { get; set; } = 600;
    public double BatteryTempWarnC { get; set; } = 40;
    public bool NmeaEnabled { get; set; }
    public int NmeaPort { get; set; } = 27210;
    public bool ApiIncludeLocation { get; set; }

    private static string PathOnDisk => Path.Combine(AppPaths.DataDir, "settings.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static BridgeSettings Load()
    {
        try { if (File.Exists(PathOnDisk)) return JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(PathOnDisk)) ?? new(); }
        catch (Exception e) { Log.Warn($"settings unreadable, using defaults: {e.Message}"); }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, Opts));
    }
}

/// <summary>
/// Owns discovery, validation, ADB forwards and the four authenticated channels for the target serial.
/// Only the configured serial is ever touched; other devices are reported but never deployed to.
/// </summary>
public sealed class DeviceSession : IAsyncDisposable
{
    public const string Package = "dev.pixelbridge.app";
    public const string ExpectedAppVersion = "0.4.4";
    private static readonly (string name, int port)[] Channels = [("control", 27183), ("video", 27184), ("audio", 27185), ("telemetry", 27186)];

    private readonly BridgeSettings _settings;
    private readonly PairingStore _pairing;
    private readonly Action _saveSettings;
    private readonly Dictionary<string, ChannelClient> _channels = new();
    private readonly List<int> _ownedForwards = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _linkCts;
    private Task? _runTask;
    private long _lastPongNs;
    private int _backoffSec = 2;
    private DateTime _nextVersionProbe = DateTime.MinValue;
    private DateTime _nextAttempt = DateTime.MinValue;

    public AdbClient? Adb { get; private set; }
    public LinkState State { get; private set; } = LinkState.Disconnected;
    public string StateDetail { get; private set; } = "";
    public IReadOnlyList<AdbDevice> OtherDevices { get; private set; } = [];
    public JsonObject? Capabilities { get; private set; }
    public JsonObject? ModuleStatus { get; private set; }
    public Dictionary<string, string> DeviceProps { get; } = new();
    public ClockSync Clock { get; } = new();
    public IReadOnlyDictionary<string, int> PortMap => _portMap;
    private readonly Dictionary<string, int> _portMap = new();
    private string _resolvedSerial = "";
    /// <summary>The serial every adb command targets (explicit setting, or the single attached phone).</summary>
    public string Serial => _resolvedSerial.Length > 0 ? _resolvedSerial : _settings.TargetSerial;
    public string WirelessEndpoint => _settings.WirelessEndpoint;
    public bool WirelessSecure => _settings.WirelessSecure;
    public bool IsWireless => WirelessEndpoint.Length > 0 && (Serial == WirelessEndpoint || Serial == _settings.WirelessService);
    public string TransportLabel => IsWireless || Serial.Contains(':') || Serial.StartsWith("adb-") ? "Wi-Fi" : "USB";
    private string PairingIdentity => IsWireless ? _settings.WirelessSerial : Serial;
    private int _wirelessChanging;
    private int _wirelessGeneration;
    private DateTime _nextWirelessAttempt = DateTime.MinValue;

    /// <summary>User chose "Use this phone": remember its codename and serial, then connect.</summary>
    public void ApproveCurrentDevice()
    {
        if (DeviceProps.TryGetValue("ro.product.device", out var codename) && codename.Length > 0 && !_settings.ApprovedDevices.Contains(codename))
            _settings.ApprovedDevices.Add(codename);
        if (_settings.TargetSerial.Length == 0) _settings.TargetSerial = Serial;
        _saveSettings();
        RetryNow();
    }
    public bool IsConnected => State == LinkState.Connected;

    public event Action<LinkState, string>? StateChanged;
    public event Action<SensorSample>? Sensor;
    public event Action<BatteryStatus>? Battery;
    public event Action<JsonObject>? Status;
    public event Action<byte, JsonObject>? StreamEvent;
    public event Action<JsonObject>? VideoConfig;
    public event Action? PhoneAttached;
    private string? _presentSerial;
    private readonly HashSet<string> _attemptedCompanionUpdates = new();
    public event Action<Frame>? VideoFrame;
    public event Action<JsonObject>? AudioConfig;
    public event Action<Frame>? AudioFrame;
    public event Action<JsonObject>? Notification;
    public event Action<JsonObject>? DeviceError;
    public event Action<JsonObject>? RadioEvent;
    public event Action<LocationFix>? Location;

    public DeviceSession(BridgeSettings settings, PairingStore? pairing = null, Action? saveSettings = null)
    {
        _settings = settings;
        _pairing = pairing ?? new PairingStore();
        _saveSettings = saveSettings ?? settings.Save;
    }

    public void Start()
    {
        _runCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_runCts.Token));
    }

    private void SetState(LinkState s, string detail = "")
    {
        if (s == State && detail == StateDetail) return;
        State = s;
        StateDetail = detail;
        Log.Info($"link state {s}{(detail.Length > 0 ? ": " + detail : "")}");
        StateChanged?.Invoke(s, detail);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { Log.Error($"session tick: {e.Message}"); }
            try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _wirelessChanging) != 0) return;
        var generation = Volatile.Read(ref _wirelessGeneration);
        var adbPath = AdbClient.Locate(_settings.AdbPath);
        if (adbPath is null) { SetState(LinkState.NoAdb, OperatingSystem.IsWindows() ? "adb.exe not found; install Android platform-tools or set its path in Settings" : "adb not found; install it (e.g. sudo apt install adb) or set its path in Settings"); return; }
        if (Adb?.AdbPath != adbPath) Adb = new AdbClient(adbPath);

        if (IsConnected)
        {
            // Heartbeat watchdog: 5 s without PONG = link loss.
            if (HostClock.NowNs() - Interlocked.Read(ref _lastPongNs) > 5_000_000_000L)
            {
                Log.Warn("heartbeat timeout");
                await TeardownAsync("heartbeat timeout").ConfigureAwait(false);
            }
            return;
        }

        var devices = await Adb.DevicesAsync(ct).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _wirelessGeneration)) return;
        if (_settings.AutoConnect && WirelessAdb.ValidEndpoint(WirelessEndpoint) && DateTime.UtcNow >= _nextWirelessAttempt &&
            (_settings.TargetSerial.Length == 0 || _settings.TargetSerial == _settings.WirelessSerial || _settings.TargetSerial == WirelessEndpoint || _settings.TargetSerial == _settings.WirelessService) &&
            !devices.Any(d => (d.Serial == WirelessEndpoint || d.Serial == _settings.WirelessService) && d.State == "device"))
        {
            _nextWirelessAttempt = DateTime.UtcNow.AddSeconds(30);
            if (WirelessSecure)
            {
                var services = await Adb.RunAsync(["mdns", "services"], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _wirelessGeneration)) return;
                var candidate = WirelessAdb.Discover(services.StdOut, identity: _settings.WirelessSerial);
                if (candidate is { } next)
                {
                    await Adb.RunAsync(["connect", next.Endpoint], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    var serviceIdentity = await Adb.GetPropAsync(next.Service, "ro.serialno", ct).ConfigureAwait(false);
                    var endpointIdentity = serviceIdentity == _settings.WirelessSerial ? serviceIdentity : await Adb.GetPropAsync(next.Endpoint, "ro.serialno", ct).ConfigureAwait(false);
                    if (generation != Volatile.Read(ref _wirelessGeneration)) return;
                    if (endpointIdentity == _settings.WirelessSerial)
                    { _settings.WirelessEndpoint = next.Endpoint; _settings.WirelessService = next.Service; _saveSettings(); }
                }
            }
            await Adb.RunAsync(["connect", WirelessEndpoint], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            devices = await Adb.DevicesAsync(ct).ConfigureAwait(false);
        }
        if (generation != Volatile.Read(ref _wirelessGeneration) || Volatile.Read(ref _wirelessChanging) != 0) return;
        var selected = WirelessAdb.Selection(_settings.TargetSerial, WirelessEndpoint, _settings.WirelessSerial, devices, _settings.WirelessService);
        if (selected.Length == 0)
        {
            // No phone chosen yet: adopt the one attached phone, but never guess between several.
            var phones = devices.Where(d => !d.IsEmulator && d.State is "device" or "unauthorized").ToList();
            if (phones.Count == 1) _resolvedSerial = phones[0].Serial;
            else
            {
                _resolvedSerial = "";
                if (phones.Count == 0) { _presentSerial = null; _nextAttempt = DateTime.MinValue; _backoffSec = 2; }
                OtherDevices = devices;
                SetState(LinkState.Disconnected, phones.Count == 0 ? "No phone detected. Connect USB or check the saved phone’s Wi-Fi network." : "Several phones attached: choose one in Settings");
                return;
            }
        }
        else _resolvedSerial = selected;
        OtherDevices = devices.Where(d => d.Serial != Serial).ToList();
        var target = devices.FirstOrDefault(d => d.Serial == Serial);
        if (target is null)
        {
            _presentSerial = null;
            SetState(LinkState.Disconnected, "Phone not detected. Check USB or the saved phone’s Wi-Fi network.");
            _backoffSec = 2;
            _nextAttempt = DateTime.MinValue;
            return;
        }
        if (_presentSerial != target.Serial) { _presentSerial = target.Serial; PhoneAttached?.Invoke(); }
        switch (target.State)
        {
            case "unauthorized": SetState(LinkState.Unauthorized, "USB debugging not authorised for this PC"); return;
            case "device": break;
            default: SetState(LinkState.Offline, $"adb state '{target.State}'"); return;
        }
        if (!_settings.AutoConnect && State != LinkState.Connecting) { SetState(LinkState.Disconnected, "auto-connect disabled; press Connect"); return; }
        if (State is LinkState.ApkMissing or LinkState.ApkOutdated && DateTime.UtcNow >= _nextVersionProbe)
        {
            _nextVersionProbe = DateTime.UtcNow.AddSeconds(5);
            if (await Adb.PackageVersionAsync(Serial, Package, ct).ConfigureAwait(false) == ExpectedAppVersion) RetryNow();
        }
        if (DateTime.UtcNow < _nextAttempt) return;
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Manual connect (also used by auto-connect).</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Adb is null) return;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            if (IsConnected || Volatile.Read(ref _wirelessChanging) != 0) return;
            SetState(LinkState.Connecting, "validating device");
            if (IsWireless)
            {
                var identity = await Adb.GetPropAsync(Serial, "ro.serialno", ct).ConfigureAwait(false);
                if (identity.Length == 0 || identity != _settings.WirelessSerial)
                { NeedsUser(LinkState.WrongDevice, "The saved Wi-Fi address does not identify your phone. Reconnect USB and set up wireless again."); return; }
            }
            var echo = await Adb.ShellAsync(Serial, "echo pb-ok", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (!echo.Ok || !echo.StdOut.Contains("pb-ok")) { Fail(LinkState.Offline, "shell handshake failed"); return; }
            var boot = await Adb.GetPropAsync(Serial, "sys.boot_completed", ct).ConfigureAwait(false);
            var currentUser = (await Adb.ShellAsync(Serial, "am get-current-user", TimeSpan.FromSeconds(5), ct).ConfigureAwait(false)).StdOut.Trim();
            if (!int.TryParse(currentUser, out var userId) || userId < 0) userId = 0;
            var userState = (await Adb.ShellAsync(Serial, $"am get-started-user-state {userId}", TimeSpan.FromSeconds(5), ct).ConfigureAwait(false)).StdOut;
            var readiness = PhoneBoot.Evaluate(boot, userState);
            if (readiness != PhoneReadiness.Ready)
            {
                SetState(LinkState.Connecting, readiness == PhoneReadiness.Booting ? "Android is starting. PixelBridge will reconnect automatically." : "Unlock Android once after its restart. PixelBridge will continue automatically.");
                _nextAttempt = DateTime.UtcNow.AddSeconds(2); return;
            }
            foreach (var p in new[] { "ro.product.device", "ro.product.model", "ro.build.version.release", "ro.build.version.sdk", "ro.build.id" })
                DeviceProps[p] = await Adb.GetPropAsync(Serial, p, ct).ConfigureAwait(false);
            if (!_settings.ApprovedDevices.Contains(DeviceProps["ro.product.device"]))
            {
                NeedsUser(LinkState.WrongDevice, $"{DeviceProps["ro.product.model"]} ({DeviceProps["ro.product.device"]}) hasn't been approved yet");
                return;
            }
            var version = await Adb.PackageVersionAsync(Serial, Package, ct).ConfigureAwait(false);
            if (version != ExpectedAppVersion && _settings.AutoUpdateCompanion)
            {
                var apk = Path.Combine(AppContext.BaseDirectory, "PixelBridge.apk");
                var updateKey = Serial + ":" + version;
                if (File.Exists(apk) && _attemptedCompanionUpdates.Add(updateKey))
                {
                    SetState(LinkState.Connecting, "Updating the phone companion…");
                    var installed = await Adb.RunAsync(["-s", Serial, "install", "-r", apk], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                    if (!installed.Ok) { NeedsUser(LinkState.ApkOutdated, "Companion update failed: " + string.Concat((installed.StdErr + installed.StdOut).Trim().Take(350)) + ". Updates need the original signing key."); return; }
                    version = await Adb.PackageVersionAsync(Serial, Package, ct).ConfigureAwait(false);
                }
            }
            if (version is null) { NeedsUser(LinkState.ApkMissing, "PixelBridge app not installed on the phone"); return; }
            if (version != ExpectedAppVersion) { NeedsUser(LinkState.ApkOutdated, $"phone app {version}, PC expects {ExpectedAppVersion}"); return; }

            SetState(LinkState.Connecting, "launching phone app");
            await Adb.ShellAsync(Serial, $"am start -n {Package}/.MainActivity", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            SetState(LinkState.Connecting, "forwarding channels");
            await RemoveOwnedForwardsAsync().ConfigureAwait(false);
            foreach (var (name, port) in Channels)
            {
                var host = await Adb.ForwardAsync(Serial, port, ct).ConfigureAwait(false);
                _ownedForwards.Add(host);
                _portMap[name] = host;
            }
            Log.Info("forwards " + string.Join(", ", _portMap.Select(kv => $"{kv.Key}=127.0.0.1:{kv.Value}")));

            var key = _pairing.Load(PairingIdentity);
            _linkCts = new CancellationTokenSource();
            ChannelClient? control = null;
            for (int attempt = 0; attempt < 10 && control is null; attempt++)
            {
                try { control = await ChannelClient.ConnectAsync(_portMap["control"], "control", key, ct).ConfigureAwait(false); }
                catch (AuthException) { throw; }
                catch (Exception e) when (attempt < 9)
                {
                    Log.Info($"control connect attempt {attempt + 1}: {e.Message}");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
            Register(control!);
            foreach (var name in new[] { "telemetry", "video", "audio" })
                Register(await ChannelClient.ConnectAsync(_portMap[name], name, key, ct).ConfigureAwait(false));

            Interlocked.Exchange(ref _lastPongNs, HostClock.NowNs());
            _backoffSec = 2;
            SetState(LinkState.Connected, $"{TransportLabel} · {DeviceProps["ro.product.model"]} · Android {DeviceProps["ro.build.version.release"]}");
            foreach (var c in _channels.Values) c.StartReading();
            _ = HeartbeatLoopAsync(_linkCts.Token);
            RefreshSensorDemand();
        }
        catch (AuthException e)
        {
            await CleanupChannelsAsync().ConfigureAwait(false);
            NeedsUser(LinkState.NotPaired, e.Message);
        }
        catch (Exception e)
        {
            await CleanupChannelsAsync().ConfigureAwait(false);
            Fail(LinkState.Error, e.Message);
        }
        finally { _gate.Release(); }
    }

    /// <summary>States that wait for an explicit user action (pair, install, select device) instead of retrying.</summary>
    private void NeedsUser(LinkState s, string detail)
    {
        _nextAttempt = DateTime.MaxValue;
        SetState(s, detail);
    }

    /// <summary>Clear a waiting state and retry on the next tick.</summary>
    public void RetryNow()
    {
        _nextAttempt = DateTime.MinValue;
        _nextWirelessAttempt = DateTime.MinValue;
        _backoffSec = 2;
        _settings.AutoConnect = true;
    }

    private void Fail(LinkState s, string detail)
    {
        _nextAttempt = DateTime.UtcNow.AddSeconds(_backoffSec);
        _backoffSec = Math.Min(_backoffSec * 2, 30);
        SetState(s, $"{detail} (retry in {(_nextAttempt - DateTime.UtcNow).TotalSeconds:0}s)");
    }

    private void Register(ChannelClient c)
    {
        _channels[c.Channel] = c;
        c.FrameReceived += f => Dispatch(c.Channel, f);
        c.Closed += err =>
        {
            if (IsConnected)
            {
                Log.Warn($"{c.Channel} channel closed: {err?.GetType().Name} {err?.Message}");
                _ = TeardownAsync($"{c.Channel} closed");
            }
        };
    }

    private void Dispatch(string channel, Frame f)
    {
        try
        {
            switch (f.Type)
            {
                case MsgType.SensorSample: Sensor?.Invoke(SensorSample.Parse(f)); break;
                case MsgType.BatteryStatus: Battery?.Invoke(BatteryStatus.Parse(f.Text)); break;
                case MsgType.NotificationEvent: Notification?.Invoke(JsonNode.Parse(f.Text)!.AsObject()); break;
                case MsgType.LocationSample: Location?.Invoke(LocationFix.Parse(f.Text)); break;
                case MsgType.VideoFrame: VideoFrame?.Invoke(f); break;
                case MsgType.AudioFrame: AudioFrame?.Invoke(f); break;
                case MsgType.VideoConfig: VideoConfig?.Invoke(JsonNode.Parse(f.Text)!.AsObject()); break;
                case MsgType.AudioConfig: AudioConfig?.Invoke(JsonNode.Parse(f.Text)!.AsObject()); break;
                case MsgType.Capabilities: Capabilities = JsonNode.Parse(f.Text)!.AsObject(); Log.Info("capabilities received"); break;
                case MsgType.Status:
                    ModuleStatus = JsonNode.Parse(f.Text)!.AsObject();
                    Status?.Invoke(ModuleStatus);
                    break;
                case MsgType.Pong:
                    var j = JsonNode.Parse(f.Text)!;
                    var now = HostClock.NowNs();
                    Clock.Add((long)j["hostNs"]!, (long)j["deviceNs"]!, now);
                    Interlocked.Exchange(ref _lastPongNs, now);
                    break;
                case MsgType.StreamStarted or MsgType.StreamStopped or MsgType.StreamError:
                    var ev = JsonNode.Parse(f.Text)!.AsObject();
                    Log.Info($"stream event 0x{f.Type:X2} kind={ev["kind"]} {ev["code"]} {ev["message"]} {ev["reason"]}");
                    StreamEvent?.Invoke(f.Type, ev);
                    break;
                case MsgType.RadioResponse:
                    var rr = JsonNode.Parse(f.Text)!.AsObject();
                    if (_radioPending.TryRemove((int)rr["id"]!, out var tcs)) tcs.TrySetResult(rr);
                    break;
                case MsgType.RadioEvent: RadioEvent?.Invoke(JsonNode.Parse(f.Text)!.AsObject()); break;
                case MsgType.Error:
                    var er = JsonNode.Parse(f.Text)!.AsObject();
                    Log.Warn($"device error {er["code"]}: {er["message"]}");
                    DeviceError?.Invoke(er);
                    break;
                default: Log.Warn($"{channel}: ignoring type 0x{f.Type:X2}"); break;
            }
        }
        catch (Exception e) { Log.Warn($"{channel}: bad frame 0x{f.Type:X2}: {e.Message}"); }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _radioPending = new();
    private int _radioId;

    /// <summary>
    /// Phone radio request (protocol section 9): Bluetooth LE bridge, Wi-Fi and network status.
    /// Returns the RADIO_RESPONSE object ({id, ok, error?, result?}); throws on link loss or timeout.
    /// </summary>
    public async Task<JsonObject> RadioAsync(string op, object? args = null, TimeSpan? timeout = null)
    {
        if (!_channels.TryGetValue("control", out var c)) throw new InvalidOperationException("phone not connected");
        int id = Interlocked.Increment(ref _radioId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _radioPending[id] = tcs;
        try
        {
            if (!c.TrySend(Frame.Json(MsgType.RadioRequest, JsonSerializer.Serialize(new { id, op, args = args ?? new { } }))))
                throw new IOException("send failed");
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        finally { _radioPending.TryRemove(id, out _); }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_channels.TryGetValue("control", out var c))
                c.TrySend(Frame.Json(MsgType.Heartbeat, JsonSerializer.Serialize(new { hostNs = HostClock.NowNs() })));
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private readonly HashSet<string> _sensorUsers = new();
    private bool _sensorsPaused;
    public void SetSensorDemand(string owner, bool enabled)
    {
        lock (_sensorUsers) { var changed = enabled ? _sensorUsers.Add(owner) : _sensorUsers.Remove(owner); if (changed) RefreshSensorDemand(); }
    }
    public void PauseSensors(bool paused) { lock (_sensorUsers) { _sensorsPaused = paused; RefreshSensorDemand(); } }
    public void RefreshSensorDemand()
    {
        lock (_sensorUsers)
        {
            if (_sensorUsers.Count > 0 && !_sensorsPaused)
            {
                if (_sensorUsers.SetEquals(new[]{"compass"})) StartStream("sensors", new { rateHz = 10, types = new[]{1,2,9,11} });
                else StartStream("sensors", new { rateHz = _settings.SensorRateHz });
            }
            else StopStream("sensors");
        }
    }

    public bool StartStream(string kind, object options) =>
        _channels.TryGetValue("control", out var c) &&
        c.TrySend(Frame.Json(MsgType.StartStream, JsonSerializer.Serialize(new { kind, options })));

    public bool StopStream(string kind) =>
        _channels.TryGetValue("control", out var c) &&
        c.TrySend(Frame.Json(MsgType.StopStream, JsonSerializer.Serialize(new { kind })));

    public bool RequestIdr() =>
        _channels.TryGetValue("control", out var c) && c.TrySend(new Frame(MsgType.RequestIdr, []));

    /// <summary>One-time pairing through the authorised ADB transport (protocol section 3).</summary>
    public async Task<bool> PairAsync(CancellationToken ct = default)
    {
        if (Adb is null) return false;
        var key = _pairing.CreateAndSave(PairingIdentity);
        var b64url = Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var host = string.Concat(Environment.MachineName.Where(char.IsLetterOrDigit));
        var r = await Adb.RunAsync(["-s", Serial, "shell", "am", "start", "-n", $"{Package}/.PairActivity", "--es", "key", b64url, "--es", "host", host],
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        Array.Clear(key);
        var ok = r.Ok && !r.StdOut.Contains("Error") && !r.StdErr.Contains("Error");
        Log.Info(ok ? "pairing key delivered" : $"pairing failed: exit {r.ExitCode} {r.StdErr.Trim()}");
        _nextAttempt = DateTime.UtcNow.AddSeconds(2);
        _backoffSec = 2;
        if (ok) SetState(LinkState.Disconnected, "paired; reconnecting");
        return ok;
    }

    public void Unpair() => _pairing.Delete(PairingIdentity);

    public DateTime? PairedUtc => _pairing.PairedUtc(PairingIdentity);

    public async Task<AdbResult?> InstallApkAsync(string apkPath, CancellationToken ct = default)
    {
        if (Adb is null) return null;
        var r = await Adb.RunAsync(["-s", Serial, "install", "-r", apkPath], TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        Log.Info($"install exit {r.ExitCode}: {r.StdOut.Trim()} {r.StdErr.Trim()}");
        _nextAttempt = DateTime.MinValue;
        if (r.Ok) SetState(LinkState.Disconnected, "installed; reconnecting");
        return r;
    }

    /// <summary>Called only after explicit network-debugging consent.</summary>
    public async Task<string> EnableWirelessAsync()
    {
        if (Interlocked.CompareExchange(ref _wirelessChanging, 1, 0) != 0) throw new IOException("Wireless setup is already running.");
        Interlocked.Increment(ref _wirelessGeneration);
        var auto = _settings.AutoConnect;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false); _gate.Release();
            var adb = Adb ?? throw new IOException("Install or locate ADB first.");
            var usb = Serial;
            var devices = await adb.DevicesAsync().ConfigureAwait(false);
            if (!WirelessAdb.IsUsbSerial(usb) || !devices.Any(d => d.Serial == usb && d.State == "device"))
                throw new IOException("Connect your trusted phone by USB first, then try again.");
            var identity = await adb.GetPropAsync(usb, "ro.serialno").ConfigureAwait(false);
            if (identity != usb) throw new IOException("Could not verify the USB phone's identity.");
            var addresses = await adb.ShellAsync(usb, "ip -4 -o addr show", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var ip = WirelessAdb.WifiAddress(addresses.StdOut) ?? throw new IOException("Connect the phone to a private Wi-Fi network first. No Wi-Fi address was found.");
            await DisconnectAsync().ConfigureAwait(false);
            var enabled = await adb.RunAsync(["-s", usb, "tcpip", "5555"], TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!enabled.Ok) throw new IOException("Could not enable wireless: " + enabled.StdErr + enabled.StdOut);
            _settings.WirelessEndpoint = ip + ":5555"; _settings.WirelessSerial = identity; _settings.WirelessService = ""; _settings.WirelessSecure = false; _settings.AutoConnect = auto; _saveSettings();
            for (var i = 0; i < 5; i++)
            {
                await adb.RunAsync(["connect", WirelessEndpoint], TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (await adb.GetPropAsync(WirelessEndpoint, "ro.serialno").ConfigureAwait(false) == identity)
                {
                    _resolvedSerial = WirelessEndpoint; RetryNow();
                    return "Wi-Fi connection verified. You can unplug USB. PixelBridge will reconnect automatically.";
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            throw new IOException("Wireless was enabled, but the phone could not be reached. Keep both devices on the same private Wi-Fi, check client isolation or firewall rules, then press Connect. USB can be used to set up again.");
        }
        finally { _settings.AutoConnect = auto; _nextAttempt = DateTime.MinValue; Volatile.Write(ref _wirelessChanging, 0); }
    }

    /// <summary>Android 11+ TLS pairing. The six-digit code is never saved or logged.</summary>
    public async Task<string> PairWirelessAsync(string address, string code, string connectAddress)
    {
        if (!WirelessAdb.ValidEndpoint(address) || !WirelessAdb.ValidCode(code) && code.Length > 0 || connectAddress.Length > 0 && !WirelessAdb.ValidEndpoint(connectAddress) || code.Length == 0 && connectAddress.Length == 0)
            throw new IOException("Enter the private IPv4 address:port and six-digit code shown by Android. For an already paired phone, enter its connection address.");
        if (WirelessEndpoint.Length > 0 && !WirelessSecure) throw new IOException("Turn off the existing USB-enabled wireless connection first.");
        if (Interlocked.CompareExchange(ref _wirelessChanging, 1, 0) != 0) throw new IOException("Wireless setup is already running.");
        Interlocked.Increment(ref _wirelessGeneration);
        var auto = _settings.AutoConnect;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false); _gate.Release();
            var path = AdbClient.Locate(_settings.AdbPath) ?? throw new IOException("Install or locate ADB first.");
            var adb = Adb = new AdbClient(path);
            if (code.Length > 0)
            {
                var result = await adb.RunAsync(["pair", address, code], TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                if (!result.Ok || !result.StdOut.Contains("Successfully paired")) throw new IOException("Pairing failed. Keep Android's pairing-code dialog open and try its current address and code. Update platform-tools if ADB does not support pairing.");
            }
            var services = await adb.RunAsync(["mdns", "services"], TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var candidate = WirelessAdb.Discover(services.StdOut, host: address.Split(':')[0]);
            var endpoint = connectAddress.Length > 0 ? connectAddress : candidate?.Endpoint;
            if (endpoint == null) throw new IOException("ADB paired successfully. Automatic discovery is unavailable; enter the IP address and port from Android's main Wireless debugging page, then choose Connect paired phone.");
            await adb.RunAsync(["connect", endpoint], TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var target = endpoint;
            var identity = await adb.GetPropAsync(target, "ro.serialno").ConfigureAwait(false);
            if (identity.Length == 0 && candidate is { } discovered && discovered.Endpoint == endpoint)
            { target = discovered.Service; identity = await adb.GetPropAsync(target, "ro.serialno").ConfigureAwait(false); }
            if (identity.Length == 0) throw new IOException("ADB paired, but could not connect. Use the connection port on Android's main Wireless debugging page; it differs from the pairing port.");
            var tlsPort = await adb.GetPropAsync(target, "service.adb.tls.port").ConfigureAwait(false);
            if (tlsPort != endpoint.Split(':')[1]) throw new IOException("This is not the phone's encrypted wireless connection port. Use the address on Android's main Wireless debugging page.");
            await DisconnectAsync().ConfigureAwait(false);
            _settings.TargetSerial = identity; _settings.WirelessEndpoint = endpoint; _settings.WirelessSerial = identity;
            _settings.WirelessService = candidate?.Endpoint == endpoint ? candidate.Value.Service : "";
            _settings.WirelessSecure = true; _settings.AutoConnect = auto; _saveSettings();
            _resolvedSerial = target; RetryNow();
            return "Encrypted Wi-Fi connection ready. If asked, choose Pair to finish pairing the PixelBridge companion.";
        }
        finally { _settings.AutoConnect = auto; _nextAttempt = DateTime.MinValue; Volatile.Write(ref _wirelessChanging, 0); }
    }

    private void ClearWireless(string identity)
    {
        if (_settings.TargetSerial == WirelessEndpoint || _settings.TargetSerial == _settings.WirelessService) _settings.TargetSerial = identity;
        _settings.WirelessEndpoint = ""; _settings.WirelessSerial = ""; _settings.WirelessService = ""; _settings.WirelessSecure = false; _saveSettings();
        _resolvedSerial = ""; RetryNow();
    }

    public async Task<string> DisableWirelessAsync()
    {
        if (Interlocked.CompareExchange(ref _wirelessChanging, 1, 0) != 0) throw new IOException("Wireless setup is already running.");
        Interlocked.Increment(ref _wirelessGeneration);
        var auto = _settings.AutoConnect;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false); _gate.Release();
            var adb = Adb ?? throw new IOException("Install or locate ADB first.");
            var identity = _settings.WirelessSerial;
            if (WirelessSecure)
            {
                await DisconnectAsync().ConfigureAwait(false);
                if (_settings.WirelessService.Length > 0) await adb.RunAsync(["disconnect", _settings.WirelessService], TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await adb.RunAsync(["disconnect", WirelessEndpoint], TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                _settings.AutoConnect = auto; ClearWireless(identity);
                return "Wi-Fi connection removed. Turn off Wireless debugging in Android settings to disable the phone's listener.";
            }
            var devices = await adb.DevicesAsync().ConfigureAwait(false);
            var target = devices.FirstOrDefault(d => (d.Serial == WirelessEndpoint || d.Serial == _settings.WirelessService || d.Serial == identity) && d.State == "device")?.Serial;
            if (target == null || identity.Length == 0 || await adb.GetPropAsync(target, "ro.serialno").ConfigureAwait(false) != identity)
                throw new IOException("Reconnect the phone by USB so PixelBridge can turn off its wireless listener.");
            await DisconnectAsync().ConfigureAwait(false);
            var result = await adb.RunAsync(["-s", target, "usb"], TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!result.Ok) throw new IOException("Could not turn off wireless: " + result.StdErr + result.StdOut);
            await adb.RunAsync(["disconnect", WirelessEndpoint], TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _settings.AutoConnect = auto; ClearWireless(identity);
            return "Wireless debugging is off. Connect USB to continue.";
        }
        finally { _settings.AutoConnect = auto; _nextAttempt = DateTime.MinValue; Volatile.Write(ref _wirelessChanging, 0); }
    }

    public async Task DisconnectAsync()
    {
        _settings.AutoConnect = false;
        await TeardownAsync("user disconnect").ConfigureAwait(false);
    }

    private async Task TeardownAsync(string reason)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var was = State;
            await CleanupChannelsAsync().ConfigureAwait(false);
            if (was == LinkState.Connected) Fail(LinkState.Disconnected, reason);
        }
        finally { _gate.Release(); }
    }

    private async Task CleanupChannelsAsync()
    {
        _linkCts?.Cancel();
        if (State == LinkState.Connected) State = LinkState.Disconnected;
        foreach (var c in _channels.Values) await c.DisposeAsync().ConfigureAwait(false);
        _channels.Clear();
        Capabilities = null;
        ModuleStatus = null;
        await RemoveOwnedForwardsAsync().ConfigureAwait(false);
    }

    private async Task RemoveOwnedForwardsAsync()
    {
        if (Adb is null) return;
        foreach (var p in _ownedForwards) { try { await Adb.RemoveForwardAsync(Serial, p).ConfigureAwait(false); } catch { } }
        _ownedForwards.Clear();
        _portMap.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _runCts?.Cancel();
        if (_runTask != null) { try { await _runTask.ConfigureAwait(false); } catch { } }
        await TeardownAsync("shutdown").ConfigureAwait(false);
    }
}
