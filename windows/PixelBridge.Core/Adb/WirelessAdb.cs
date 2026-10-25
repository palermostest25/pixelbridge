using System.Net;
using System.Text.RegularExpressions;

namespace PixelBridge.Core.Adb;

/// <summary>Explicit, USB-authorized Wi-Fi setup. Never scans the LAN or changes Wi-Fi networks.</summary>
public static class WirelessAdb
{
    public const string Consent = "Connect both devices to the same trusted Wi-Fi network. This enables Android's network debugging on port 5555. Computers holding an ADB key your phone trusts can control it. This mode is not encrypted; use it only on a private network. Setup briefly interrupts calls and screen mirroring. Use ‘Turn off wireless’ to stop the phone's network listener. After a phone restart or address change, connect USB and set up again.";

    public static bool IsUsbSerial(string serial) => serial.Length > 0 && !serial.Contains(':') && !serial.StartsWith("adb-") && !serial.StartsWith("emulator-");

    public static string? WifiAddress(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\d+:\s+wlan\d+\s+inet\s+(\d+\.\d+\.\d+\.\d+)/\d+\b");
            if (match.Success && IsPrivateAddress(match.Groups[1].Value)) return match.Groups[1].Value;
        }
        return null;
    }

    public static bool IsPrivateAddress(string value)
    {
        if (!Regex.IsMatch(value, @"^\d{1,3}(\.\d{1,3}){3}$") || !IPAddress.TryParse(value, out var ip)) return false;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168);
    }

    public static bool ValidEndpoint(string endpoint) => endpoint.Split(':') is [var host, var port] && IsPrivateAddress(host) && port.Length > 0 && port.All(c => c is >= '0' and <= '9') && int.TryParse(port, out var n) && n is > 0 and <= 65535;

    public static int? TlsPort(string output)
    {
        var text = output.Trim();
        if (int.TryParse(text, out var port) && port is > 0 and <= 65535) return port;
        var match = Regex.Match(text, @"(?m)^\s*tls_port=(\d+)\s*$");
        return match.Success && int.TryParse(match.Groups[1].Value, out port) && port is > 0 and <= 65535 ? port : null;
    }
    public static async Task<int?> ReadTlsPortAsync(AdbClient adb, string serial)
    {
        var port = TlsPort(await adb.GetPropAsync(serial, "service.adb.tls.port").ConfigureAwait(false));
        return port ?? TlsPort((await adb.ShellAsync(serial, "dumpsys adb | grep -E '^ *tls_port='", TimeSpan.FromSeconds(5)).ConfigureAwait(false)).StdOut);
    }

    public static bool ValidCode(string code) => code.Length == 6 && code.All(c => c is >= '0' and <= '9');
    public static (string Endpoint, string Service)? Discover(string output, string? host = null, string? identity = null)
    {
        var found = new List<(string, string)>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[1].TrimEnd('.') != "_adb-tls-connect._tcp" || !ValidEndpoint(parts[2])) continue;
            if (host != null && parts[2].Split(':')[0] != host) continue;
            if (identity != null && !parts[0].StartsWith("adb-" + identity + "-", StringComparison.Ordinal)) continue;
            var service = parts[0].Contains("._adb-tls-connect._tcp") ? parts[0] : parts[0] + "._adb-tls-connect._tcp";
            found.Add((parts[2], service));
        }
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>Prefer the saved Wi-Fi transport, then its USB transport. Unrelated explicit selections win.</summary>
    public static string Selection(string configured, string endpoint, string identity, IReadOnlyList<AdbDevice> devices, string service = "")
    {
        if (!ValidEndpoint(endpoint) || identity.Length == 0 || configured.Length > 0 && configured != identity && configured != endpoint && configured != service) return configured;
        if (devices.Any(d => d.Serial == endpoint && d.State == "device")) return endpoint;
        if (service.Length > 0 && devices.Any(d => d.Serial == service && d.State == "device")) return service;
        if (devices.Any(d => d.Serial == identity)) return identity;
        return endpoint;
    }
}
