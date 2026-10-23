using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using PixelBridge.Core;

namespace PixelBridge.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BleDevice : Observable
{
    private string? _name; private int _rssi; private string _state = "seen"; private DateTime _lastSeen;
    public required string Address { get; init; }
    public string? Name { get => _name; set { Set(ref _name, value); Raise(nameof(Title)); } }
    public int Rssi { get => _rssi; set { Set(ref _rssi, value); Raise(nameof(Bars)); } }
    public bool Connectable { get; set; }
    public string State { get => _state; set => Set(ref _state, value); }
    public DateTime LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }
    public string Title => string.IsNullOrEmpty(Name) ? "Unnamed device" : Name!;
    /// <summary>0–4 signal bars from RSSI.</summary>
    public int Bars => Rssi == 0 ? -1 : Rssi >= -60 ? 4 : Rssi >= -70 ? 3 : Rssi >= -80 ? 2 : Rssi >= -90 ? 1 : 0;
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    private string _detail = "";
    public ObservableCollection<GattCharacteristic> Characteristics { get; } = new();
}

public sealed class GattCharacteristic : Observable
{
    private string _value = "";
    public required string Service { get; init; }
    public required string Uuid { get; init; }
    public required string Properties { get; init; }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string ShortUuid => KnownUuids.Name(Uuid);
    public bool CanRead => Properties.Contains("read");
    public bool CanWrite => Properties.Contains("write");
    public bool CanNotify => Properties.Contains("notify") || Properties.Contains("indicate");
}

public static class KnownUuids
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["2a00"] = "Device Name", ["2a01"] = "Appearance", ["2a19"] = "Battery Level", ["2a29"] = "Manufacturer",
        ["2a24"] = "Model Number", ["2a26"] = "Firmware Revision", ["2a37"] = "Heart Rate", ["2a6e"] = "Temperature",
        ["180f"] = "Battery", ["180a"] = "Device Information", ["180d"] = "Heart Rate", ["1800"] = "Generic Access",
    };

    public static string Name(string uuid)
    {
        var u = uuid.ToLowerInvariant();
        if (u.EndsWith("-0000-1000-8000-00805f9b34fb") && u.StartsWith("0000"))
        {
            var s = u.Substring(4, 4);
            return Names.TryGetValue(s, out var n) ? $"{n} (0x{s})" : $"0x{s}";
        }
        return uuid;
    }
}

/// <summary>UI-facing state for the phone's Bluetooth LE bridge, Wi-Fi and network status (RadioAsync ops).</summary>
public sealed class RadioController : Observable
{
    private readonly DeviceSession _s;
    private bool _scanning;
    private string _status = "";
    public ObservableCollection<BleDevice> Devices { get; } = new();
    public ObservableCollection<BleDevice> Bonded { get; } = new();
    public ObservableCollection<WifiNetwork> WifiNetworks { get; } = new();
    public JsonObject? WifiStatus { get; private set; }
    public JsonObject? Caps { get; private set; }
    public bool Scanning { get => _scanning; private set => Set(ref _scanning, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public RadioController(DeviceSession s)
    {
        _s = s;
        s.RadioEvent += e => Application.Current?.Dispatcher.BeginInvoke(() => OnEvent(e));
        s.StateChanged += (st, _) => { if (st != LinkState.Connected) Application.Current?.Dispatcher.BeginInvoke(() => { Scanning = false; foreach (var d in Devices) d.State = "seen"; }); };
    }

    private async Task<JsonObject?> Call(string op, object? args = null, int timeoutSec = 15)
    {
        try
        {
            var r = await _s.RadioAsync(op, args, TimeSpan.FromSeconds(timeoutSec));
            if (r["ok"]?.GetValue<bool>() == true) return r["result"]?.AsObject() ?? new JsonObject();
            Status = (string?)r["message"] ?? (string?)r["error"] ?? "failed";
        }
        catch (Exception e) { Status = e is TimeoutException ? "The phone didn't answer in time." : e.Message; }
        return null;
    }

    public async Task RefreshAsync()
    {
        Caps = await Call("caps");
        WifiStatus = await Call("wifi.status");
        OnPropertyChangedAll();
    }

    public async Task ScanBleAsync(int seconds = 15)
    {
        Status = "";
        if (await Call("ble.scan.start", new { durationMs = seconds * 1000 }) != null) Scanning = true;
    }

    public async Task StopBleAsync() { await Call("ble.scan.stop"); Scanning = false; }

    public async Task LoadBondedAsync()
    {
        var r = await Call("bt.bonded");
        if (r?["devices"] is not JsonArray arr) return;
        Bonded.Clear();
        foreach (var d in arr)
            Bonded.Add(new BleDevice { Address = (string)d!["address"]!, Name = (string?)d["name"], Detail = $"{d["type"]} · paired on the phone" });
    }

    public async Task ScanWifiAsync()
    {
        Status = "Scanning Wi-Fi (Android limits scans to 4 every 2 minutes)…";
        var r = await Call("wifi.scan", timeoutSec: 20);
        if (r?["results"] is not JsonArray arr) return;
        WifiNetworks.Clear();
        foreach (var n in arr)
            WifiNetworks.Add(new WifiNetwork((string?)n!["ssid"] is { Length: > 0 } ss ? ss : "(hidden network)", (string?)n["bssid"] ?? "",
                (int)n["rssi"]!, (int)n["frequencyMhz"]!, (string?)n["security"] ?? ""));
        Status = r["freshScan"]?.GetValue<bool>() == true ? $"{arr.Count} networks found." : $"{arr.Count} networks (cached; Android throttled a fresh scan).";
    }

    public async Task ConnectAsync(BleDevice d)
    {
        d.State = "connecting";
        d.Characteristics.Clear();
        if (await Call("ble.connect", new { address = d.Address }) == null) d.State = "failed";
    }

    public Task DisconnectAsync(BleDevice d) => Call("ble.disconnect", new { address = d.Address }).ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => d.State = "seen"));

    public async Task ReadAsync(BleDevice d, GattCharacteristic c)
    {
        var r = await Call("ble.read", new { address = d.Address, service = c.Service, characteristic = c.Uuid });
        if (r?["valueB64"] is { } v) c.Value = Format(Convert.FromBase64String((string)v!));
    }

    public async Task WriteAsync(BleDevice d, GattCharacteristic c, byte[] value) =>
        await Call("ble.write", new { address = d.Address, service = c.Service, characteristic = c.Uuid, valueB64 = Convert.ToBase64String(value), withResponse = true });

    public async Task NotifyAsync(BleDevice d, GattCharacteristic c, bool enable) =>
        await Call("ble.notify", new { address = d.Address, service = c.Service, characteristic = c.Uuid, enable });

    private void OnEvent(JsonObject e)
    {
        switch ((string?)e["event"])
        {
            case "ble.scanResult":
            {
                var addr = (string)e["address"]!;
                var d = Devices.FirstOrDefault(x => x.Address == addr);
                if (d == null)
                {
                    d = new BleDevice { Address = addr, Connectable = (bool?)e["connectable"] ?? false };
                    Devices.Add(d);
                }
                d.Name ??= (string?)e["name"];
                d.Rssi = (int)e["rssi"]!;
                d.LastSeen = DateTime.Now;
                var mfr = e["manufacturerIds"]?.AsArray().Select(x => (int)x!).ToList() ?? [];
                d.Detail = $"{(d.Connectable ? "Connectable" : "Beacon")} · {d.Rssi} dBm{(mfr.Contains(76) ? " · Apple" : mfr.Contains(6) ? " · Microsoft" : mfr.Contains(117) ? " · Samsung" : "")}";
                // Keep strongest first without reordering on every packet.
                if (Devices.Count % 10 == 0) Resort();
                break;
            }
            case "ble.scanStopped": Scanning = false; Resort(); break;
            case "ble.connectionState":
            {
                var d = Devices.Concat(Bonded).FirstOrDefault(x => x.Address == (string?)e["address"]);
                if (d != null) d.State = (string)e["state"]!;
                break;
            }
            case "ble.services":
            {
                var d = Devices.Concat(Bonded).FirstOrDefault(x => x.Address == (string?)e["address"]);
                if (d == null) break;
                d.Characteristics.Clear();
                foreach (var s in e["services"]!.AsArray())
                    foreach (var c in s!["characteristics"]!.AsArray())
                        d.Characteristics.Add(new GattCharacteristic
                        {
                            Service = (string)s["uuid"]!, Uuid = (string)c!["uuid"]!,
                            Properties = string.Join(" ", c["properties"]!.AsArray().Select(p => (string)p!)),
                        });
                d.State = "ready";
                break;
            }
            case "ble.notification":
            {
                var d = Devices.Concat(Bonded).FirstOrDefault(x => x.Address == (string?)e["address"]);
                var c = d?.Characteristics.FirstOrDefault(x => x.Uuid == (string?)e["characteristic"]);
                if (c != null) c.Value = Format(Convert.FromBase64String((string)e["valueB64"]!)) + "  (notified)";
                break;
            }
        }
    }

    private void Resort()
    {
        var sorted = Devices.OrderByDescending(d => d.Rssi).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Devices.IndexOf(sorted[i]);
            if (cur != i) Devices.Move(cur, i);
        }
    }

    public void ClearScan() => Devices.Clear();

    /// <summary>Hex plus printable text when the bytes look like UTF-8.</summary>
    public static string Format(byte[] v)
    {
        var hex = Convert.ToHexString(v);
        var text = Encoding.UTF8.GetString(v);
        bool printable = v.Length > 0 && text.All(ch => !char.IsControl(ch));
        return printable ? $"\"{text}\"  ·  0x{hex}" : v.Length == 1 ? $"{v[0]}  ·  0x{hex}" : $"0x{hex}";
    }

    private void OnPropertyChangedAll() { Raise(nameof(WifiStatus)); Raise(nameof(Caps)); }
}

public sealed record WifiNetwork(string Ssid, string Bssid, int Rssi, int FrequencyMhz, string Security)
{
    public string Band => FrequencyMhz >= 5925 ? "6 GHz" : FrequencyMhz >= 4900 ? "5 GHz" : "2.4 GHz";
    public string SecurityShort => Security.Contains("SAE") ? "WPA3" : Security.Contains("WPA2") || Security.Contains("RSN") ? "WPA2" : Security.Contains("WPA") ? "WPA" : Security.Contains("WEP") ? "WEP" : "Open";
    public int Bars => Rssi >= -60 ? 4 : Rssi >= -70 ? 3 : Rssi >= -80 ? 2 : Rssi >= -90 ? 1 : 0;
}
