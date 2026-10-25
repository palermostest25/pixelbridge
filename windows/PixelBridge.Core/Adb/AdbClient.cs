using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PixelBridge.Core.Adb;

public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public sealed record AdbDevice(string Serial, string State, string Model, string Device, bool IsEmulator);

/// <summary>
/// Thin wrapper over adb.exe. Device commands are qualified with <c>-s SERIAL</c>.
/// Server restart is reserved for user-confirmed key import. Network debugging changes require explicit wireless setup.
/// </summary>
public sealed partial class AdbClient(string adbPath)
{
    public string AdbPath { get; } = adbPath;

    /// <summary>Resolve adb.exe: explicit setting, then the scrcpy folder, then the SDK, then PATH.</summary>
    public static string? Locate(string? configured)
    {
        var exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "platform-tools", exe),
            Path.Combine(local, "Android", "Sdk", "platform-tools", exe),
            Path.Combine(local, "PixelBridgeTools", "android-sdk", "platform-tools", exe),
            Path.Combine(home, "Android", "Sdk", "platform-tools", exe),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim(), exe));
        // scrcpy's Windows zip ships adb.exe; look in the usual download location.
        if (OperatingSystem.IsWindows())
            candidates.AddRange(SafeGlob(Path.Combine(home, "Downloads"), "scrcpy-win64-*").SelectMany(d => new[] { Path.Combine(d, exe), Path.Combine(d, Path.GetFileName(d), exe) }));
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    /// <summary>scrcpy executable (for the phone-screen window), same search strategy as adb.</summary>
    public static string? LocateScrcpy(string? configured)
    {
        var exe = OperatingSystem.IsWindows() ? "scrcpy.exe" : "scrcpy";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var c = new List<string?> { configured };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(dir)) c.Add(Path.Combine(dir.Trim(), exe));
        if (OperatingSystem.IsWindows())
            c.AddRange(SafeGlob(Path.Combine(home, "Downloads"), "scrcpy-win64-*").SelectMany(d => new[] { Path.Combine(d, exe), Path.Combine(d, Path.GetFileName(d), exe) }));
        else c.AddRange(["/usr/bin/scrcpy", "/usr/local/bin/scrcpy", "/snap/bin/scrcpy", "/opt/homebrew/bin/scrcpy"]);
        return c.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    private static IEnumerable<string> SafeGlob(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : []; } catch { return []; }
    }

    public async Task<AdbResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("adb failed to start");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new AdbResult(-1, "", "timeout");
        }
        return new AdbResult(p.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }

    public Task<AdbResult> RunAsync(params string[] args) => RunAsync(args, null);

    public Task<AdbResult> ShellAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default) =>
        RunAsync(["-s", serial, "shell", command], timeout, ct);

    public async Task<IReadOnlyList<AdbDevice>> DevicesAsync(CancellationToken ct = default)
    {
        var r = await RunAsync(["devices", "-l"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return ParseDevices(r.StdOut);
    }

    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var list = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith('*')) continue;
            var parts = DeviceLine().Match(line);
            if (!parts.Success) continue;
            var serial = parts.Groups[1].Value;
            var rest = parts.Groups[3].Value;
            string Prop(string name) => Regex.Match(rest, name + @":(\S+)").Groups[1].Value;
            list.Add(new AdbDevice(serial, parts.Groups[2].Value, Prop("model"), Prop("device"),
                serial.StartsWith("emulator-", StringComparison.Ordinal)));
        }
        return list;
    }

    /// <summary>Forward a free host loopback port (adb chooses it) to a device loopback port.</summary>
    public async Task<int> ForwardAsync(string serial, int devicePort, CancellationToken ct = default)
    {
        var r = await RunAsync(["-s", serial, "forward", "tcp:0", $"tcp:{devicePort}"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (!r.Ok || !int.TryParse(r.StdOut.Trim(), out var port))
            throw new IOException($"adb forward failed ({r.ExitCode}): {r.StdErr.Trim()}");
        return port;
    }

    public Task<AdbResult> RemoveForwardAsync(string serial, int hostPort) =>
        RunAsync(["-s", serial, "forward", "--remove", $"tcp:{hostPort}"], TimeSpan.FromSeconds(10));

    public async Task<string> GetPropAsync(string serial, string prop, CancellationToken ct = default) =>
        (await ShellAsync(serial, $"getprop {prop}", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false)).StdOut.Trim();

    public async Task<string?> PackageVersionAsync(string serial, string package, CancellationToken ct = default)
    {
        var r = await ShellAsync(serial, $"dumpsys package {package}", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var m = Regex.Match(r.StdOut, @"versionName=(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(\S+)\s+(device|offline|unauthorized|recovery|sideload|bootloader|no permissions|authorizing|connecting|host)\b(.*)$")]
    private static partial Regex DeviceLine();
}
