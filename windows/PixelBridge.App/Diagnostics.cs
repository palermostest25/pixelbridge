using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PixelBridge.Core;
using PixelBridge.Core.Adb;
using PixelBridge.Media;

namespace PixelBridge.App;

/// <summary>Read-only health checks. Output is sanitised: no message text, coordinates, SSIDs or keys.</summary>
public static class Doctor
{
    public static async Task<string> RunAsync(AppController c)
    {
        var sb = new StringBuilder();
        void Line(bool? ok, string what, string detail) => sb.AppendLine($"[{(ok is null ? "INFO" : ok.Value ? " OK " : "FAIL")}] {what}: {detail}");
        sb.AppendLine($"PixelBridge Doctor {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line(null, "Windows", $"{Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");
        Line(VirtualCamera.IsSupportedOs, "MFCreateVirtualCamera support", VirtualCamera.IsSupportedOs ? "Windows 11 build ≥ 22000" : "needs Windows 11; use OBS fallback");
        var adbPath = AdbClient.Locate(c.Settings.AdbPath);
        Line(adbPath != null, "adb.exe", adbPath ?? "not found");
        if (adbPath != null)
        {
            var adb = new AdbClient(adbPath);
            var ver = await adb.RunAsync("version");
            Line(ver.Ok, "adb version", ver.StdOut.Split('\n').FirstOrDefault()?.Trim() ?? "");
            var devs = await adb.DevicesAsync();
            foreach (var d in devs) Line(d.Serial == c.Settings.TargetSerial ? d.State == "device" : null, $"adb device {d.Serial}", $"{d.State} {d.Model} {(d.IsEmulator ? "(emulator, ignored)" : "")}");
            if (devs.All(d => d.Serial != c.Settings.TargetSerial)) Line(false, "target serial", $"{c.Settings.TargetSerial} not attached");
            var fw = await adb.RunAsync("forward", "--list");
            Line(null, "adb forwards", fw.StdOut.Trim().Length == 0 ? "(none)" : fw.StdOut.Trim().Replace("\n", "; "));
        }
        Line(c.Session.IsConnected, "PixelBridge link", $"{c.Session.State} {c.Session.StateDetail}");
        Line(c.Session.PairedUtc != null, "pairing", c.Session.PairedUtc?.ToString("u") ?? "not paired");
        Line(VirtualCamera.IsSourceRegistered, "virtual camera source", VirtualCamera.IsSourceRegistered ? "registered (HKLM)" : "not installed (tools\\install-vcam.ps1)");
        Line(c.VirtualCameraActive, "virtual camera device", c.VirtualCameraState);
        Line(CameraPrivacyAllowsDesktopApps(), "Windows camera privacy", CameraPrivacyAllowsDesktopApps() ? "desktop apps allowed" : "camera access for desktop apps may be OFF (Settings > Privacy > Camera)");
        try
        {
            var outs = AudioPipeline.PlaybackEndpoints();
            var cable = outs.FirstOrDefault(o => o.IsVirtualCableInput);
            Line(cable != null, "virtual audio cable", cable?.Name ?? "VB-CABLE not installed: virtual mic requires driver (optional)");
        }
        catch (Exception e) { Line(false, "audio endpoints", e.Message); }
        Line(c.Api != null, "sensor API", c.Api != null ? $"ws://127.0.0.1:{c.Api.Port}/sensors (token required, {c.Api.Rejected} rejected requests)" : "disabled");
        if (c.Battery is { } b) Line(b.TempC is null or < 40, "phone battery", $"{b.Percent}% {b.TempC:0.0} °C {b.Health}");
        Line(null, "listeners", ListenerSummary());
        return sb.ToString();
    }

    private static bool CameraPrivacyAllowsDesktopApps()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged");
        return (k?.GetValue("Value") as string) != "Deny";
    }

    /// <summary>All TCP listeners owned by PixelBridge must be loopback-only.</summary>
    public static string ListenerSummary()
    {
        var props = IPGlobalProperties.GetIPGlobalProperties();
        var l = props.GetActiveTcpListeners().Where(e => e.Port is >= 27183 and <= 27210 || e.Port == 5037).Select(e => e.ToString()).Distinct();
        return string.Join(", ", l);
    }
}

/// <summary>Native USB tethering feasibility, strictly read-only (never changes USB functions).</summary>
public static class NetworkProbe
{
    public static async Task<string> RunAsync(DeviceSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("READ-ONLY CHECK. Nothing on the phone or PC was changed.");
        if (s.Adb != null && s.IsConnected)
        {
            async Task<string> Sh(string cmd) => (await s.Adb.ShellAsync(s.Serial, cmd, TimeSpan.FromSeconds(10))).StdOut.Trim();
            sb.AppendLine($"Phone USB functions (sys.usb.config): {await Sh("getprop sys.usb.config")}");
            sb.AppendLine($"Phone USB state (sys.usb.state):    {await Sh("getprop sys.usb.state")}");
            var teth = await Sh("dumpsys tethering");
            var usbLine = teth.Split('\n').Where(l => Regex.IsMatch(l, @"(?i)usb|rndis|ncm|tetherable|Tethering restricted|isTetheringSupported")).Take(12);
            sb.AppendLine("Tethering service (filtered):");
            foreach (var l in usbLine) sb.AppendLine("   " + l.Trim());
            var conn = await Sh("dumpsys connectivity | grep -m 3 -E 'Active default network|NetworkAgentInfo\\{.*(WIFI|CELLULAR)'");
            sb.AppendLine("Upstream (filtered, no SSIDs):");
            foreach (var l in conn.Split('\n')) sb.AppendLine("   " + Regex.Replace(l.Trim(), "\"[^\"]*\"", "\"<redacted>\"").Split(" lp{")[0]);
        }
        else sb.AppendLine("Phone not connected: phone-side checks skipped.");
        sb.AppendLine("Windows adapters that look like phone tethering (RNDIS/NCM):");
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => Regex.IsMatch(n.Description, "(?i)rndis|ncm|android|remote ndis|usb ethernet")).ToList();
        if (nics.Count == 0) sb.AppendLine("   none (expected while tethering is off)");
        foreach (var n in nics) sb.AppendLine($"   {n.Name}: {n.Description} [{n.OperationalStatus}]");
        sb.AppendLine();
        sb.AppendLine("Verdict: native tethering has NOT been tested. Enabling it changes the phone's USB mode and may drop ADB");
        sb.AppendLine("(the only control channel for a phone with a dead screen). It needs a supervised test with a recovery plan.");
        sb.AppendLine("See docs/USB_NETWORK.md.");
        return sb.ToString();
    }
}
