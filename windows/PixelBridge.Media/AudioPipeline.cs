using NAudio.CoreAudioApi;
using NAudio.Wave;
using PixelBridge.Core;

namespace PixelBridge.Media;

public sealed record AudioEndpoint(string Id, string Name)
{
    /// <summary>VB-Audio Virtual Cable playback side; conferencing apps then record from "CABLE Output".</summary>
    public bool IsVirtualCableInput => Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Name;
}

/// <summary>
/// Phone microphone PCM -> optional WASAPI playback into a user-selected endpoint (normally VB-CABLE's
/// "CABLE Input") and/or a monitor device, with gain, mute, level meter and bounded WAV recording.
/// Never changes Windows default devices.
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    private readonly WaveFormat _format = new(48000, 16, 1);
    private Sink? _cable, _monitor;
    private WaveFileWriter? _recorder;
    private DateTime _recordUntil;
    private readonly Lock _lock = new();

    public float Gain { get; set; } = 1.0f;
    public bool Muted { get; set; }
    public int JitterMs { get; set; } = 60;
    public double LevelDbfs { get; private set; } = -120;
    public double PeakDbfs { get; private set; } = -120;
    public long FramesIn { get; private set; }
    public DateTime LastFrameUtc { get; private set; }
    public string? RecordingPath { get; private set; }
    public bool IsRecording => _recorder != null;
    public string? CableDeviceName => _cable?.Name;
    public string? MonitorDeviceName => _monitor?.Name;
    public bool IsLive => (DateTime.UtcNow - LastFrameUtc).TotalSeconds < 1;

    public static IReadOnlyList<AudioEndpoint> PlaybackEndpoints()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => new AudioEndpoint(d.ID, d.FriendlyName)).ToList();
    }

    public static IReadOnlyList<AudioEndpoint> CaptureEndpoints()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => new AudioEndpoint(d.ID, d.FriendlyName)).ToList();
    }

    public void SetCableOutput(string? deviceId) { lock (_lock) { _cable?.Dispose(); _cable = deviceId is null ? null : Sink.Open(deviceId, _format, JitterMs); } }
    public void SetMonitorOutput(string? deviceId) { lock (_lock) { _monitor?.Dispose(); _monitor = deviceId is null ? null : Sink.Open(deviceId, _format, JitterMs); } }

    /// <summary>Feed one AUDIO_FRAME payload (PCM16 LE mono 48 kHz).</summary>
    public void Push(byte[] pcm)
    {
        FramesIn++;
        LastFrameUtc = DateTime.UtcNow;
        var buf = new byte[pcm.Length];
        double sumSq = 0; int peak = 0;
        float g = Muted ? 0f : Gain;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            int s = (int)(BitConverter.ToInt16(pcm, i) * g);
            s = Math.Clamp(s, short.MinValue, short.MaxValue);
            buf[i] = (byte)s; buf[i + 1] = (byte)(s >> 8);
            sumSq += (double)s * s; peak = Math.Max(peak, Math.Abs(s));
        }
        int n = pcm.Length / 2;
        LevelDbfs = n > 0 ? 20 * Math.Log10(Math.Max(Math.Sqrt(sumSq / n), 1) / 32768) : -120;
        PeakDbfs = 20 * Math.Log10(Math.Max(peak, 1) / 32768.0);
        if (Muted) return; // privacy mute: nothing leaves this method
        lock (_lock)
        {
            _cable?.Write(buf);
            _monitor?.Write(buf);
            if (_recorder != null)
            {
                _recorder.Write(buf, 0, buf.Length);
                if (DateTime.UtcNow >= _recordUntil) StopRecordingLocked();
            }
        }
    }

    /// <summary>Operator-initiated WAV recording, capped at 60 s, saved under Documents\PixelBridge.</summary>
    public string StartRecording(int seconds = 60)
    {
        lock (_lock)
        {
            StopRecordingLocked();
            Directory.CreateDirectory(AppPaths.RecordingsDir);
            RecordingPath = Path.Combine(AppPaths.RecordingsDir, $"pixel-mic-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            _recorder = new WaveFileWriter(RecordingPath, _format);
            _recordUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 60));
            Log.Info($"mic recording started ({Math.Clamp(seconds, 1, 60)} s max)");
            return RecordingPath;
        }
    }

    public void StopRecording() { lock (_lock) StopRecordingLocked(); }

    private void StopRecordingLocked()
    {
        if (_recorder == null) return;
        _recorder.Dispose();
        _recorder = null;
        Log.Info("mic recording saved");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopRecordingLocked();
            _cable?.Dispose(); _monitor?.Dispose();
            _cable = _monitor = null;
        }
    }

    private sealed class Sink : IDisposable
    {
        private readonly WasapiOut _out;
        private readonly BufferedWaveProvider _buffer;
        private readonly MMDevice _device;
        public string Name => _device.FriendlyName;

        private Sink(MMDevice device, WaveFormat fmt, int jitterMs)
        {
            _device = device;
            _buffer = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };
            _out = new WasapiOut(device, AudioClientShareMode.Shared, true, Math.Clamp(jitterMs, 40, 150));
            _out.Init(_buffer);
            _out.Play();
        }

        public static Sink? Open(string id, WaveFormat fmt, int jitterMs)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                return new Sink(en.GetDevice(id), fmt, jitterMs);
            }
            catch (Exception e) { Log.Warn($"audio output open failed: {e.Message}"); return null; }
        }

        public void Write(byte[] pcm)
        {
            // Keep latency bounded: if the device fell behind, drop the backlog instead of growing it.
            if (_buffer.BufferedDuration > TimeSpan.FromMilliseconds(250)) _buffer.ClearBuffer();
            _buffer.AddSamples(pcm, 0, pcm.Length);
        }

        public void Dispose() { _out.Stop(); _out.Dispose(); _device.Dispose(); }
    }
}
