namespace PixelBridge.Core;

/// <summary>Explicit USB internet sharing. Never changes Wi-Fi or other network adapters.</summary>
public sealed class UsbSharing(DeviceSession session)
{
    public bool Enabled { get; private set; }
    public bool Busy { get; private set; }
    public string Message { get; private set; } = "";
    public event Action? Changed;
    public async Task RefreshAsync()
    {
        if (Busy || session.Adb == null || !session.IsConnected) return;
        var r = await session.Adb.ShellAsync(session.Serial, "svc usb getFunctions", TimeSpan.FromSeconds(5));
        if (r.Ok) Enabled = IsSharing(r.StdOut + r.StdErr);
        Changed?.Invoke();
    }
    public static bool IsSharing(string functions) => functions.Split(',', StringSplitOptions.TrimEntries).Any(f => f.Equals("ncm", StringComparison.OrdinalIgnoreCase) || f.Equals("rndis", StringComparison.OrdinalIgnoreCase));
    public async Task SetAsync(bool enabled)
    {
        if (Busy || session.Adb == null || !session.IsConnected) return;
        Busy = true; Message = "Changing USB sharing…"; Changed?.Invoke();
        try
        {
            var adb = session.Adb; var serial = session.Serial;
            var result = await adb.ShellAsync(serial, "svc usb setFunctions " + (enabled ? "ncm" : "mtp"), TimeSpan.FromSeconds(8));
            // Switching USB functions re-enumerates the cable, so a dropped command is not a failure by itself.
            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(1000);
                var check = await adb.ShellAsync(serial, "svc usb getFunctions", TimeSpan.FromSeconds(2));
                if (!check.Ok) continue;
                Enabled = IsSharing(check.StdOut + check.StdErr);
                if (Enabled == enabled) { Message = enabled ? "USB sharing on. Reconnecting…" : "USB sharing off. Reconnecting…"; return; }
            }
            Message = "USB sharing did not change. Check the phone’s USB settings.";
        }
        catch (Exception e) { Message = "Could not change USB sharing: " + e.Message; }
        finally { Busy = false; Changed?.Invoke(); }
    }
}
