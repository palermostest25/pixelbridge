using System.Text.Json;
using System.Text.RegularExpressions;

namespace PixelBridge.Core;

public sealed record HotspotState(bool Supported, bool Enabled);

/// <summary>One-shot, explicit controls through the authorized ADB shell. No password access.</summary>
public static class HotspotControl
{
    public const string EnableNotice = "Turn on the phone's Wi-Fi hotspot using its existing password and name? This shares its internet connection, may use mobile data, uses extra battery, and can interrupt the current Wi-Fi connection. Android or your carrier may require approval on the phone.";
    public static async Task<HotspotState> RunAsync(DeviceSession session, string action)
    {
        if (action is not "status" and not "on" and not "off") throw new ArgumentException("Unknown hotspot action.");
        if (session.Adb is not { } adb || string.IsNullOrWhiteSpace(session.Serial)) throw new IOException("Connect the phone first.");
        var serial = session.Serial;
        var package = await adb.ShellAsync(serial, "pm path " + DeviceSession.Package, TimeSpan.FromSeconds(5));
        var path = package.StdOut.Split('\n').FirstOrDefault(x => x.StartsWith("package:"))?[8..].Trim();
        if (!package.Ok || path == null || !Regex.IsMatch(path, @"\A/[A-Za-z0-9_/.=+~\-]+\z")) throw new IOException("Update the phone companion to use hotspot controls.");
        var result = await adb.ShellAsync(serial, "CLASSPATH=" + PhoneCommands.Quote(path) + " app_process / dev.pixelbridge.app.phone.ShellHotspot " + action, TimeSpan.FromSeconds(35));
        var state = Parse(result.StdOut, result.Ok);
        if (action != "status" && !state.Supported) throw new IOException("Direct hotspot controls are unavailable on this Android version. Open Phone settings.");
        return state;
    }

    public static HotspotState Parse(string output, bool success)
    {
        var line = output.Split('\n').LastOrDefault(x => x.TrimStart().StartsWith('{'));
        if (line == null) throw new IOException("Hotspot controls are unavailable. Update the phone app, or open Android hotspot settings.");
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!success || !root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new IOException(root.TryGetProperty("error", out var error) ? error.GetString() : "Android could not change the hotspot.");
        var supported = root.TryGetProperty("supported", out var support) && support.GetBoolean();
        var hasState = root.TryGetProperty("enabled", out var enabled);
        if (supported && (!hasState || enabled.ValueKind is not JsonValueKind.True and not JsonValueKind.False)) throw new IOException("Android did not report hotspot status. Refresh or open Phone settings.");
        return new(supported, hasState && enabled.GetBoolean());
    }
}
