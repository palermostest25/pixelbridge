using System.Text.RegularExpressions;

namespace PixelBridge.Core;

/// <summary>Explicit host actions, always scoped to the selected, authorized phone.</summary>
public sealed class PhoneCommands(DeviceSession session)
{
    private async Task Run(params string[] arguments)
    {
        if (session.Adb == null || string.IsNullOrWhiteSpace(session.Serial)) throw new IOException("Connect the phone first");
        var result = await session.Adb.RunAsync(new[] { "-s", session.Serial }.Concat(arguments).ToArray(), TimeSpan.FromMinutes(3));
        if (!result.Ok) throw new IOException((result.StdErr + result.StdOut).Trim());
    }
    public Task OpenCompanionAsync() => Run("shell", "am start -n " + DeviceSession.Package + "/.MainActivity");
    public Task OpenSetupAsync(string feature) { if (feature is not "communications" and not "calling" and not "notifications") throw new ArgumentException("Unknown setup feature"); return Run("shell", "am start -n " + DeviceSession.Package + "/.phone.SetupActivity --es feature " + feature); }
    public Task RestartAsync() => Run("reboot");
    public Task SleepAsync() => Run("shell", "input keyevent KEYCODE_SLEEP");
    public Task WakeAsync() => Run("shell", "input keyevent KEYCODE_WAKEUP");
    public Task LaunchAsync(string component)
    {
        if (!Regex.IsMatch(component, @"\A[A-Za-z0-9_.]+/[A-Za-z0-9_.$]+\z")) throw new ArgumentException("Invalid app component");
        return Run("shell", "am start -n " + Quote(component));
    }
    public async Task<IReadOnlyList<string>> DownloadsAsync()
    {
        if (session.Adb == null || !session.IsConnected) throw new IOException("Connect the phone first");
        var r = await session.Adb.ShellAsync(session.Serial, "ls -1p /sdcard/Download", TimeSpan.FromSeconds(10));
        if (!r.Ok) throw new IOException(r.StdErr.Trim());
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !x.EndsWith('/') && ValidName(x)).Take(200).ToArray();
    }
    public Task UploadAsync(string localFile)
    {
        if (!File.Exists(localFile)) throw new FileNotFoundException("Choose a file to send", localFile);
        var name = Path.GetFileName(localFile); if (!ValidName(name)) throw new ArgumentException("Unsupported filename");
        return UploadFreshAsync(localFile, name);
    }
    private async Task UploadFreshAsync(string localFile, string name)
    {
        if (session.Adb == null || !session.IsConnected) throw new IOException("Connect the phone first");
        var check = await session.Adb.ShellAsync(session.Serial, "if [ -e " + Quote("/sdcard/Download/" + name) + " ]; then echo exists; else echo free; fi", TimeSpan.FromSeconds(10));
        if (!check.Ok || check.StdOut.Trim() != "free") throw new IOException("A file with this name is already in phone Downloads, or the folder is unavailable. Rename the file before sending.");
        await Run("push", localFile, "/sdcard/Download/" + name);
    }
    public Task DownloadAsync(string name, string localDestination)
    {
        if (!ValidName(name)) throw new ArgumentException("Choose a file from the phone's Downloads folder");
        return Run("pull", "/sdcard/Download/" + name, localDestination);
    }
    public static bool ValidName(string name) => name.Length is > 0 and <= 255 && name is not "." and not ".." && !name.Any(c => char.IsControl(c) || c is '/' or '\\');
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
