using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PixelBridge.Core.Adb;

namespace PixelBridge.Core;

/// <summary>Pixel Camera creates the originals; only files from this capture session are copied.</summary>
public sealed class NativeCapture
{
    private CancellationTokenSource? _stop;
    public bool Running => _stop != null;
    public string Status { get; private set; } = "";

    public void Stop() => _stop?.Cancel();

    public async Task RunAsync(DeviceSession session, bool video, string? scrcpyPath, Func<bool> allowed)
    {
        if (Running || !allowed() || !session.IsConnected || session.Adb is not { } adb) return;
        var serial = session.Serial;
        var scrcpy = AdbClient.LocateScrcpy(scrcpyPath);
        if (scrcpy == null) { Status = "Install scrcpy or set its path in Settings to open Pixel Camera."; return; }
        using var stop = new CancellationTokenSource();
        _stop = stop;
        var ct = stop.Token;
        Process? process = null;
        var saved = 0;
        Status = "Opening Pixel Camera…";
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (!allowed()) throw new OperationCanceledException();
            if (!session.IsConnected || session.Serial != serial) throw new IOException("The phone disconnected. Any original stays on the phone.");
        }
        try
        {
            var downloads = DownloadsDirectory();
            Directory.CreateDirectory(downloads);
            var clock = await adb.ShellAsync(serial, "date +%s%3N", TimeSpan.FromSeconds(5), ct);
            Check();
            if (!clock.Ok || !long.TryParse(clock.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var since) || since <= 0)
                throw new IOException("Could not read the phone clock. Reconnect and try again.");
            var mode = video ? "VIDEO_CAMERA" : "STILL_IMAGE_CAMERA";
            var launch = await adb.ShellAsync(serial, $"am start -W -a android.media.action.{mode} -p com.google.android.GoogleCamera", TimeSpan.FromSeconds(12), ct);
            Check();
            if (!launch.Ok || !launch.StdOut.Contains("Status: ok"))
                throw new IOException("Pixel Camera couldn’t open. Unlock the phone and check that Pixel Camera is installed.");
            var info = new ProcessStartInfo(scrcpy) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(scrcpy)! };
            info.Environment["ADB"] = adb.AdbPath;
            foreach (var arg in new[] { "-s", serial, "--no-audio", "--force-adb-forward", "--max-size=1280", "--max-fps=15", "--turn-screen-off", "--window-title", "PixelBridge Capture" })
                info.ArgumentList.Add(arg);
            process = Process.Start(info) ?? throw new IOException("Could not open the phone window.");
            Status = "Use Pixel Camera’s shutter. Stop any recording and wait for Saved before closing the phone window.";
            var copied = new HashSet<long>();
            bool lastPass = false;
            while (true)
            {
                await Task.Delay(2000, ct);
                Check();
                var result = await adb.ShellAsync(serial, Query(since, copied), TimeSpan.FromSeconds(8), ct);
                Check();
                if (!result.Ok || !string.IsNullOrWhiteSpace(result.StdErr))
                    throw new IOException("Could not check for finished captures. Originals stay on the phone.");
                foreach (var file in Files(result.StdOut))
                {
                    if (copied.Contains(file.Id)) continue;
                    Check();
                    Status = $"Saving {file.Name} to Downloads…";
                    var temp = Path.Combine(downloads, $".pixelbridge-{Guid.NewGuid():N}.part");
                    try
                    {
                        var pull = await adb.RunAsync(["-s", serial, "pull", "/sdcard/DCIM/Camera/" + file.Name, temp], TimeSpan.FromMinutes(15), ct);
                        Check();
                        if (!pull.Ok || !File.Exists(temp) || new FileInfo(temp).Length != file.Bytes)
                            throw new IOException($"Couldn’t save {file.Name}. The original stays on the phone.");
                        var target = Save(temp, file.Name, downloads);
                        copied.Add(file.Id); saved++;
                        Status = $"Saved {Path.GetFileName(target)} to Downloads.";
                    }
                    finally { try { File.Delete(temp); } catch (IOException) { } }
                }
                if (lastPass) break;
                lastPass = process.HasExited;
            }
            Status = saved > 0 ? $"Saved {saved} {(saved == 1 ? "file" : "files")} to Downloads."
                : process.ExitCode == 0 ? "Capture closed. No new files were saved."
                : "The phone window couldn’t stay open. Check scrcpy and reconnect the phone.";
        }
        catch (OperationCanceledException) { Status = "Capture closed. Originals stay on the phone."; }
        catch (Exception e) { Status = e.Message; }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            process?.Dispose();
            _stop = null;
        }
    }

    internal static string Query(long since, HashSet<long> copied)
    {
        var skip = copied.Count == 0 ? "" : " AND _id NOT IN (" + string.Join(",", copied.Order()) + ")";
        return $"content query --uri content://media/external_primary/file --projection _id:_display_name:_size --where \"relative_path='DCIM/Camera/' AND owner_package_name='com.google.android.GoogleCamera' AND is_pending=0 AND is_trashed=0 AND media_type IN (1,3) AND _size>0 AND datetaken>={since}{skip}\" --sort '_id ASC' | head -n 24";
    }

    private static readonly Regex Row = new(@"\ARow: [0-9]+ _id=([0-9]+), _display_name=(PXL_[A-Za-z0-9_.-]{1,150}\.(?:jpg|jpeg|heic|heif|dng|mp4|mov)), _size=([0-9]+)\z");
    internal static IEnumerable<(long Id, string Name, long Bytes)> Files(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var row = Row.Match(line.TrimEnd('\r'));
            if (row.Success && long.TryParse(row.Groups[1].Value, out var id) && id > 0
                && long.TryParse(row.Groups[3].Value, out var bytes) && bytes > 0)
                yield return (id, row.Groups[2].Value, bytes);
        }
    }

    internal static string Save(string temp, string name, string downloads)
    {
        for (int n = 0; n < 1000; n++)
        {
            var target = Path.Combine(downloads, n == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            try { File.Move(temp, target); return target; }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new IOException("Too many copies of this file in Downloads.");
    }

    private static string DownloadsDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var configured = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                "{374DE290-123F-4565-9164-39C4925E467B}", null) as string;
            if (!string.IsNullOrEmpty(configured)) return Environment.ExpandEnvironmentVariables(configured);
        }
        else if (OperatingSystem.IsLinux())
        {
            var config = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "user-dirs.dirs");
            if (File.Exists(config))
                foreach (var line in File.ReadLines(config))
                    if (line.StartsWith("XDG_DOWNLOAD_DIR=\"") && line.EndsWith('"'))
                    {
                        var directory = line[18..^1].Replace("$HOME", home, StringComparison.Ordinal);
                        if (Path.IsPathFullyQualified(directory)) return directory;
                    }
        }
        return Path.Combine(home, "Downloads");
    }
}
