using System.Diagnostics;
using System.Threading.Channels;
using PixelBridge.Core;

namespace PixelBridge.Linux.Media;

/// <summary>Ephemeral call streams through PulseAudio or PipeWire's PulseAudio compatibility layer.</summary>
sealed class CallAudioDevice : ICallAudioDevice
{
    private Process? _capture, _playback;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _speakerQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
    private int _disposed;
    public event Action<byte[]>? Microphone;
    public void Start()
    {
        _playback = Launch("pacat", "--playback");
        _capture = Launch("parec", "--record");
        var ct = _stop.Token;
        _ = Task.Run(async () => {
            try {
                var data = new byte[640];
                while (!ct.IsCancellationRequested) { await _capture.StandardOutput.BaseStream.ReadExactlyAsync(data, ct); Microphone?.Invoke(data.ToArray()); }
            } catch (OperationCanceledException) { }
            catch { _speakerQueue.Writer.TryComplete(); }
        }, ct);
        _ = Task.Run(async () => {
            try { await foreach (var data in _speakerQueue.Reader.ReadAllAsync(ct)) { await _playback.StandardInput.BaseStream.WriteAsync(data, ct); await _playback.StandardInput.BaseStream.FlushAsync(ct); } }
            catch (OperationCanceledException) { }
            catch { _stop.Cancel(); }
        }, ct);
    }
    private Process Launch(string tool, string direction)
    {
        var path = Tools.Which(tool) ?? throw new IOException("Install pulseaudio-utils / enable PipeWire audio to use computer call audio");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { direction, "--raw", "--format=s16le", "--rate=16000", "--channels=1", "--latency-msec=40" }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new IOException("Could not start desktop call audio");
        _ = Task.Run(async () => { var buffer = new char[1024]; try { while (await process.StandardError.ReadAsync(buffer, _stop.Token) > 0) { } } catch { } });
        return process;
    }
    public void Play(byte[] pcm) => _speakerQueue.Writer.TryWrite(pcm);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _speakerQueue.Writer.TryComplete();
        foreach (var process in new[] { _capture, _playback }) { try { if (process is { HasExited: false }) process.Kill(true); } catch { } process?.Dispose(); }
    }
}
