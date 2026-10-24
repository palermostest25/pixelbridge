using NAudio.CoreAudioApi;
using NAudio.Wave;
using PixelBridge.Core;

namespace PixelBridge.Media;

/// <summary>Call audio uses the computer's communication devices, independently of the phone-mic pipeline.</summary>
public sealed class CallAudioDevice : ICallAudioDevice
{
    private WaveInEvent? _microphone;
    private WasapiOut? _speakers;
    private MMDevice? _output;
    private readonly BufferedWaveProvider _buffer = new(new WaveFormat(16000, 16, 1)) { BufferDuration = TimeSpan.FromMilliseconds(250), DiscardOnBufferOverflow = true };
    private readonly object _gate = new();
    public event Action<byte[]>? Microphone;
    public void Start()
    {
        using var devices = new MMDeviceEnumerator();
        _output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
        _speakers = new WasapiOut(_output, AudioClientShareMode.Shared, true, 60);
        _speakers.Init(_buffer); _speakers.Play();
        _microphone = new WaveInEvent { DeviceNumber = -1, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 20, NumberOfBuffers = 3 };
        _microphone.DataAvailable += Capture;
        _microphone.StartRecording();
    }
    private void Capture(object? sender, WaveInEventArgs args)
    {
        var pcm = new byte[args.BytesRecorded]; Array.Copy(args.Buffer, pcm, pcm.Length); Microphone?.Invoke(pcm);
    }
    public void Play(byte[] pcm)
    {
        lock (_gate) { if (_speakers == null) return; if (_buffer.BufferedDuration.TotalMilliseconds > 150) _buffer.ClearBuffer(); _buffer.AddSamples(pcm, 0, pcm.Length); }
    }
    public void Dispose()
    {
        if (_microphone != null) { _microphone.DataAvailable -= Capture; _microphone.StopRecording(); _microphone.Dispose(); _microphone = null; }
        lock (_gate) { _speakers?.Stop(); _speakers?.Dispose(); _speakers = null; _output?.Dispose(); _output = null; _buffer.ClearBuffer(); }
    }
}
