using System.Diagnostics;
using System.Threading.Channels;
using PixelBridge.Core;

namespace PixelBridge.Linux.Media;

static class Tools
{
    public static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static async Task<(int code, string output)> RunAsync(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync();
            var e = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, o + e);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}

/// <summary>
/// Phone H.264 → ffmpeg → (a) small BGRA preview frames for the UI, (b) optionally a v4l2loopback device that
/// every Linux camera app sees as "PixelBridge Camera". Late input is dropped until the next keyframe.
/// </summary>
sealed class FfmpegVideo : IDisposable
{
    private Process? _ff;
    private Channel<byte[]>? _input;
    private CancellationTokenSource? _cts;
    private bool _waitKey = true;
    public int PreviewWidth { get; private set; } = 640;
    public int PreviewHeight { get; private set; } = 360;
    public string? V4l2Device { get; private set; }
    public event Action<byte[], int, int>? PreviewFrame; // BGRA, width, height
    public DateTime LastFrameUtc { get; private set; }
    public bool IsLive => (DateTime.UtcNow - LastFrameUtc).TotalSeconds < 1.5;
    public static bool Available => Tools.Which("ffmpeg") != null;

    /// <summary>The v4l2loopback node labelled "PixelBridge Camera", if the module is loaded.</summary>
    public static string? FindLoopback()
    {
        try
        {
            foreach (var d in Directory.GetDirectories("/sys/class/video4linux"))
                if (File.ReadAllText(Path.Combine(d, "name")).Contains("PixelBridge", StringComparison.OrdinalIgnoreCase))
                    return "/dev/" + Path.GetFileName(d);
        }
        catch { }
        return null;
    }

    public void Start(int width, int height, bool toLoopback)
    {
        Stop();
        PreviewWidth = 640;
        PreviewHeight = Math.Max(2, (int)Math.Round(640.0 * height / Math.Max(1, width) / 2) * 2);
        V4l2Device = toLoopback ? FindLoopback() : null;
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-fflags", "nobuffer", "-flags", "low_delay", "-probesize", "32",
                     "-analyzeduration", "0", "-f", "h264", "-i", "pipe:0",
                     "-map", "0:v", "-vf", $"scale={PreviewWidth}:{PreviewHeight}", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" })
            psi.ArgumentList.Add(a);
        if (V4l2Device != null)
            foreach (var a in new[] { "-map", "0:v", "-pix_fmt", "yuv420p", "-f", "v4l2", V4l2Device }) psi.ArgumentList.Add(a);
        _ff = Process.Start(psi)!;
        _cts = new CancellationTokenSource();
        _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropWrite });
        _waitKey = true;
        var ff = _ff; var input = _input; var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await foreach (var au in input.Reader.ReadAllAsync(ct)) await ff.StandardInput.BaseStream.WriteAsync(au, ct); }
            catch { }
        });
        _ = Task.Run(async () =>
        {
            int size = PreviewWidth * PreviewHeight * 4;
            var stream = ff.StandardOutput.BaseStream;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var buf = new byte[size];
                    await stream.ReadExactlyAsync(buf, ct);
                    LastFrameUtc = DateTime.UtcNow;
                    PreviewFrame?.Invoke(buf, PreviewWidth, PreviewHeight);
                }
            }
            catch { }
        });
        _ = Task.Run(async () => { var err = await ff.StandardError.ReadToEndAsync(); if (err.Length > 0) Log.Warn("ffmpeg: " + err.Trim()); });
        Log.Info($"ffmpeg video pipeline started{(V4l2Device != null ? " → " + V4l2Device : "")}");
    }

    public void Feed(byte[] accessUnit, bool keyframe, bool config)
    {
        if (_input is null) return;
        if (_waitKey && !keyframe && !config) return;
        if (keyframe) _waitKey = false;
        if (!_input.Writer.TryWrite(accessUnit)) _waitKey = true; // backlog: resync on the next keyframe
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { if (_ff is { HasExited: false }) _ff.Kill(); } catch { }
        _ff?.Dispose(); _ff = null; _input = null;
        LastFrameUtc = DateTime.MinValue;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Virtual microphone through PulseAudio / PipeWire-pulse (no root): a null sink plus a remapped source named
/// "PixelBridge Microphone" that apps can pick. Modules are unloaded again when disabled or on exit.
/// </summary>
sealed class PulseMic : IDisposable
{
    private readonly List<string> _modules = new();
    private Process? _pacat;
    public bool Enabled => _pacat != null;
    public float Gain { get; set; } = 1;
    public bool Muted { get; set; }
    public double LevelDbfs { get; private set; } = -120;
    public DateTime LastFrameUtc { get; private set; }
    public bool IsLive => (DateTime.UtcNow - LastFrameUtc).TotalSeconds < 1;
    public static bool Available => Tools.Which("pactl") != null && Tools.Which("pacat") != null;

    public async Task<string?> EnableAsync()
    {
        if (Enabled) return null;
        if (!Available) return "pactl/pacat not found (install pulseaudio-utils or pipewire-pulse)";
        var (c1, sink) = await Tools.RunAsync("pactl", "load-module", "module-null-sink", "sink_name=pixelbridge_sink",
            "sink_properties=device.description=PixelBridge-Output");
        if (c1 != 0) return "Couldn't create the virtual sink: " + sink.Trim();
        _modules.Add(sink.Trim());
        var (c2, src) = await Tools.RunAsync("pactl", "load-module", "module-remap-source", "master=pixelbridge_sink.monitor",
            "source_name=pixelbridge_mic", "source_properties=device.description=PixelBridge-Microphone");
        if (c2 == 0) _modules.Add(src.Trim());
        var psi = new ProcessStartInfo("pacat") { RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in new[] { "--playback", "--device=pixelbridge_sink", "--format=s16le", "--rate=48000", "--channels=1", "--latency-msec=60", "--client-name=PixelBridge" })
            psi.ArgumentList.Add(a);
        _pacat = Process.Start(psi);
        Log.Info("virtual microphone enabled (PixelBridge-Microphone)");
        return null;
    }

    public void Push(byte[] pcm)
    {
        LastFrameUtc = DateTime.UtcNow;
        var buf = new byte[pcm.Length];
        double sum = 0;
        float g = Muted ? 0 : Gain;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            int s = Math.Clamp((int)(BitConverter.ToInt16(pcm, i) * g), short.MinValue, short.MaxValue);
            buf[i] = (byte)s; buf[i + 1] = (byte)(s >> 8);
            sum += (double)s * s;
        }
        LevelDbfs = 20 * Math.Log10(Math.Max(Math.Sqrt(sum / Math.Max(1, pcm.Length / 2)), 1) / 32768);
        if (Muted || _pacat is null) return;
        try { _pacat.StandardInput.BaseStream.Write(buf); } catch { }
    }

    public async Task DisableAsync()
    {
        try { _pacat?.Kill(); } catch { }
        _pacat?.Dispose(); _pacat = null;
        foreach (var m in Enumerable.Reverse(_modules)) await Tools.RunAsync("pactl", "unload-module", m);
        _modules.Clear();
    }

    public void Dispose() => DisableAsync().GetAwaiter().GetResult();
}
