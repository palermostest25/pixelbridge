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
    public string? AdbPath { get; set; }
    public string? ScrcpyPath { get; set; }
    public bool AutoConnect { get; set; } = true;
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
    public const string ExpectedAppVersion = "0.4.1";
    private static readonly (string name, int port)[] Channels = [("control", 27183), ("video", 27184), ("audio", 27185), ("telemetry", 27186)];

    private readonly BridgeSettings _settings;
    private readonly PairingStore _pairing;
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
    public string Serial => _settings.TargetSerial.Length > 0 ? _settings.TargetSerial : _resolvedSerial;

    /// <summary>User chose "Use this phone": remember its codename and serial, then connect.</summary>
    public void ApproveCurrentDevice()
    {
        if (DeviceProps.TryGetValue("ro.product.device", out var codename) && codename.Length > 0 && !_settings.ApprovedDevices.Contains(codename))
            _settings.ApprovedDevices.Add(codename);
        if (_settings.TargetSerial.Length == 0) _settings.TargetSerial = Serial;
        _settings.Save();
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

    public DeviceSession(BridgeSettings settings, PairingStore? pairing = null)
    {
        _settings = settings;
        _pairing = pairing ?? new PairingStore();
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
        if (_settings.TargetSerial.Length == 0)
        {
            // No phone chosen yet: adopt the one attached phone, but never guess between several.
            var phones = devices.Where(d => !d.IsEmulator && d.State is "device" or "unauthorized").ToList();
            if (phones.Count == 1) _resolvedSerial = phones[0].Serial;
            else
            {
                _resolvedSerial = "";
                if (phones.Count == 0) { _presentSerial = null; _nextAttempt = DateTime.MinValue; _backoffSec = 2; }
                OtherDevices = devices;
                SetState(LinkState.Disconnected, phones.Count == 0 ? "No phone detected on USB" : "Several phones attached: choose one in Settings");
                return;
            }
        }
        else _resolvedSerial = _settings.TargetSerial;
        OtherDevices = devices.Where(d => d.Serial != Serial).ToList();
        var target = devices.FirstOrDefault(d => d.Serial == Serial);
        if (target is null)
        {
            _presentSerial = null;
            SetState(LinkState.Disconnected, "Phone not detected on USB");
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
            if (IsConnected) return;
            SetState(LinkState.Connecting, "validating device");
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

            var key = _pairing.Load(Serial);
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
            SetState(LinkState.Connected, $"{DeviceProps["ro.product.model"]} · Android {DeviceProps["ro.build.version.release"]}");
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
        var key = _pairing.CreateAndSave(Serial);
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

    public void Unpair() => _pairing.Delete(Serial);

    public DateTime? PairedUtc => _pairing.PairedUtc(Serial);

    public async Task<AdbResult?> InstallApkAsync(string apkPath, CancellationToken ct = default)
    {
        if (Adb is null) return null;
        var r = await Adb.RunAsync(["-s", Serial, "install", "-r", apkPath], TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        Log.Info($"install exit {r.ExitCode}: {r.StdOut.Trim()} {r.StdErr.Trim()}");
        _nextAttempt = DateTime.MinValue;
        if (r.Ok) SetState(LinkState.Disconnected, "installed; reconnecting");
        return r;
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
