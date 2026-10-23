using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PixelBridge.Core;

public sealed record TetherStatus(
    bool WindowsAdapterUp, string? AdapterName, string? AdapterDescription, string? WindowsIp, string? Gateway,
    long LinkSpeedBps, int? PhoneMetric, int? OtherBestMetric, string? OtherBestName, bool PhoneIsPreferred,
    string? PhoneUsbFunctions, string? PhoneNcmIp, bool PersistentDefault)
{
    public string Summary => !WindowsAdapterUp
        ? "USB tethering is off (no phone network adapter in Windows)."
        : PhoneIsPreferred ? "Internet on this PC goes through the Pixel." : $"Pixel connected as a network, but Windows prefers {OtherBestName}.";
}

/// <summary>Read-only USB tethering state from both ends (Windows adapters/routes, phone via ADB).</summary>
public static class TetherProbe
{
    private static async Task<string> RunAsync(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var o = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            return o;
        }
        catch { return ""; }
    }

    private static async Task<Dictionary<string, int>> LinuxRouteMetricsAsync()
    {
        var result = new Dictionary<string, int>();
        foreach (var line in (await RunAsync("ip", "-4 route show default").ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var dev = Regex.Match(line, @"\bdev (\S+)").Groups[1].Value;
            var m = Regex.Match(line, @"\bmetric (\d+)");
            int metric = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            if (dev.Length > 0 && (!result.TryGetValue(dev, out var old) || metric < old)) result[dev] = metric;
        }
        return result;
    }

    private static readonly Regex Ncm = new("(?i)UsbNcm|NCM|RNDIS|Remote NDIS");

    public static async Task<TetherStatus> RunAsync(DeviceSession s)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var phone = nics.FirstOrDefault(n => IsPhoneTether(n) && n.OperationalStatus == OperationalStatus.Up);
        string? ip = null, gw = null;
        if (phone != null)
        {
            var p = phone.GetIPProperties();
            ip = p.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            gw = p.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        }
        var metrics = await RouteMetricsAsync().ConfigureAwait(false);
        int? phoneMetric = phone != null && metrics.TryGetValue(phone.Name, out var pm) ? pm : null;
        var others = metrics.Where(kv => phone == null || kv.Key != phone.Name).OrderBy(kv => kv.Value).ToList();
        int? otherBest = others.Count > 0 ? others[0].Value : null;

        string? funcs = null, ncmIp = null; bool persistent = false;
        if (s.Adb != null && s.IsConnected)
        {
            var functions = await s.Adb.ShellAsync(s.Serial, "svc usb getFunctions", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            funcs = (functions.StdOut + functions.StdErr).Trim();
            var a = (await s.Adb.ShellAsync(s.Serial, "ip -4 -o addr show ncm0", TimeSpan.FromSeconds(5)).ConfigureAwait(false)).StdOut;
            ncmIp = Regex.Match(a, @"inet (\d+\.\d+\.\d+\.\d+)").Groups[1].Value is { Length: > 0 } v ? v : null;
            var u = (await s.Adb.ShellAsync(s.Serial, "dumpsys usb | grep -o 'screen_unlocked_functions=[A-Za-z_,]*'", TimeSpan.FromSeconds(5)).ConfigureAwait(false)).StdOut;
            persistent = u.Contains("NCM", StringComparison.OrdinalIgnoreCase) || u.Contains("RNDIS", StringComparison.OrdinalIgnoreCase);
        }
        return new TetherStatus(phone != null, phone?.Name, phone?.Description, ip, gw, phone?.Speed ?? 0,
            phoneMetric, otherBest, others.FirstOrDefault().Key, phoneMetric != null && (otherBest == null || phoneMetric <= otherBest),
            funcs, ncmIp, persistent);
    }

    /// <summary>Windows: UsbNcm/RNDIS adapter description. Linux: interface bound to the cdc_ncm / rndis_host kernel driver.</summary>
    public static bool IsPhoneTether(NetworkInterface n)
    {
        if (OperatingSystem.IsWindows()) return Ncm.IsMatch(n.Description);
        try
        {
            var driver = new DirectoryInfo($"/sys/class/net/{n.Name}/device/driver").ResolveLinkTarget(true)?.Name ?? "";
            return driver is "cdc_ncm" or "rndis_host" or "cdc_ether";
        }
        catch { return false; }
    }

    /// <summary>Effective default-route metric per interface (read-only): Windows NetTCPIP, Linux `ip route`.</summary>
    private static async Task<Dictionary<string, int>> RouteMetricsAsync()
    {
        if (!OperatingSystem.IsWindows()) return await LinuxRouteMetricsAsync().ConfigureAwait(false);
        var result = new Dictionary<string, int>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-NetRoute -DestinationPrefix 0.0.0.0/0 -ErrorAction SilentlyContinue | ForEach-Object { $_.InterfaceAlias + '|' + ($_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily IPv4).InterfaceMetric) }\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|');
                if (parts.Length == 2 && int.TryParse(parts[1], out var m) && (!result.TryGetValue(parts[0], out var old) || m < old)) result[parts[0]] = m;
            }
        }
        catch (Exception e) { Log.Warn($"route metric probe failed: {e.Message}"); }
        return result;
    }

    /// <summary>
    /// Set which side Windows prefers for internet (elevated; the user confirms via UAC). preferPhone=null restores automatic metrics.
    /// Only touches the phone adapter's interface metric; other adapters are left alone.
    /// </summary>
    public static async Task<bool> SetPreferenceAsync(string adapterName, bool? preferPhone)
    {
        if (!OperatingSystem.IsWindows())
        {
            // NetworkManager owns routes on most desktops; change the connection's metric through it (polkit prompts if needed).
            var nmMetric = preferPhone switch { true => "5", false => "900", null => "-1" };
            var conn = await RunAsync("nmcli", $"-g GENERAL.CONNECTION device show {adapterName}").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(conn)) return false;
            await RunAsync("nmcli", $"connection modify \"{conn.Trim()}\" ipv4.route-metric {nmMetric}").ConfigureAwait(false);
            await RunAsync("nmcli", $"device reapply {adapterName}").ConfigureAwait(false);
            return true;
        }
        var metric = preferPhone switch { true => "-InterfaceMetric 5", false => "-InterfaceMetric 500", null => "-AutomaticMetric Enabled" };
        var cmd = $"Set-NetIPInterface -InterfaceAlias '{adapterName.Replace("'", "''")}' -AddressFamily IPv4 {metric}";
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{cmd}\"") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync().ConfigureAwait(false);
            Log.Info($"route preference set ({metric}) exit {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { Log.Info("route preference change cancelled at UAC"); return false; }
    }
}
