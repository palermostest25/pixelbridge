using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PixelBridge.Core;
using PixelBridge.Core.Protocol;

namespace PixelBridge.Media;

/// <summary>
/// Decodes the phone's H.264 stream on a dedicated thread with a bounded queue. Late non-keyframes are
/// dropped (and an IDR requested) rather than building latency. Decoded frames fan out to the virtual
/// camera feed and the preview.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    private readonly DeviceSession _session;
    private readonly BlockingCollection<Frame> _queue = new(new ConcurrentQueue<Frame>(), 6);
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private H264Decoder? _decoder;
    private readonly VideoGeometry _geometry = new();
    public BridgeSettings Settings { get; set; } = new();
    private bool _waitingForKeyframe = true;
    private long _framesIn, _framesDecoded, _framesDropped, _bytesIn;
    private readonly Stopwatch _rateWatch = Stopwatch.StartNew();
    private long _rateFrames, _rateBytes;
    private readonly Queue<double> _latencies = new();

    public VirtualCamFeed Feed { get; } = new();
    public JsonObject? Config { get; private set; }
    public Nv12Frame? Latest { get; private set; }
    public bool Paused { get; set; }
    public double Fps { get; private set; }
    public double BitrateMbps { get; private set; }
    public long FramesDropped => _framesDropped;
    public long FramesDecoded => _framesDecoded;
    public DateTime LastFrameUtc { get; private set; }
    /// <summary>Approximate phone-capture to host-decode latency (ms), p50 of the last 120 frames, using the clock-sync offset.</summary>
    public double LatencyP50Ms { get; private set; }
    public double LatencyP95Ms { get; private set; }

    public event Action<Nv12Frame>? FrameDecoded;

    public VideoPipeline(DeviceSession session)
    {
        _session = session;
        session.VideoConfig += OnConfig;
        session.VideoFrame += OnFrame;
        session.StateChanged += (s, _) => { if (s != LinkState.Connected) GoOffline(); };
        _thread = new Thread(Run) { IsBackground = true, Name = "pb-video-decode" };
        _thread.Start();
    }

    private void OnConfig(JsonObject cfg)
    {
        if (CameraControls.FormatChanged(Config, cfg)) _waitingForKeyframe = true;
        Config = cfg;
    }

    private void OnFrame(Frame f)
    {
        Interlocked.Increment(ref _framesIn);
        Interlocked.Add(ref _bytesIn, f.Payload.Length);
        if (!_queue.TryAdd(f))
        {
            // Backlog: drop everything not decodable on its own and ask for a fresh IDR.
            while (_queue.TryTake(out _)) Interlocked.Increment(ref _framesDropped);
            _waitingForKeyframe = true;
            _session.RequestIdr();
            if (f.Flags.HasFlag(FrameFlags.CodecConfig) || f.Flags.HasFlag(FrameFlags.Keyframe)) _queue.TryAdd(f);
        }
    }

    private void Run()
    {
        try
        {
            foreach (var f in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try { Process(f); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One bad frame or subscriber must never stop the decode thread.
                    Log.Warn($"video pipeline frame error: {e.GetType().Name}: {e.Message}");
                    _waitingForKeyframe = true;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Error($"video pipeline: {e.Message}"); }
    }

    private void Process(Frame f)
    {
        {
            {
                _decoder ??= new H264Decoder();
                bool config = f.Flags.HasFlag(FrameFlags.CodecConfig), key = f.Flags.HasFlag(FrameFlags.Keyframe);
                if (config) { _decoder.Decode(f.Payload, f.TimestampNs); return; }
                if (_waitingForKeyframe && !key) { Interlocked.Increment(ref _framesDropped); return; }
                _waitingForKeyframe = false;
                _decoder.Decode(f.Payload, f.TimestampNs);
                var nv = _decoder.TakeLatest();
                if (nv is null) return;
                _framesDecoded++;
                LastFrameUtc = DateTime.UtcNow;
                TrackLatency(f.TimestampNs);
                UpdateRates(f.Payload.Length);
                if (Paused) return;
                nv = new Nv12Frame(nv.Width, nv.Height, _geometry.Transform(nv.Data, nv.Width, nv.Height, CameraControls.Rotation(Settings, Config), Settings.PreviewMirror), nv.TimestampNs);
                Latest = nv;
                Feed.Write(nv);
                FrameDecoded?.Invoke(nv);
            }
        }
    }

    private void TrackLatency(long devicePtsNs)
    {
        if (!_session.Clock.HasEstimate || Config?["timestampSource"]?.GetValue<string>() != "boottime") return;
        double ms = (HostClock.NowNs() - _session.Clock.ToHostNs(devicePtsNs)) / 1e6;
        if (ms < 0 || ms > 5000) return;
        _latencies.Enqueue(ms);
        while (_latencies.Count > 120) _latencies.Dequeue();
        if (_framesDecoded % 15 == 0)
        {
            var sorted = _latencies.Order().ToArray();
            LatencyP50Ms = sorted[sorted.Length / 2];
            LatencyP95Ms = sorted[(int)(sorted.Length * 0.95)];
        }
    }

    private void UpdateRates(int bytes)
    {
        _rateFrames++; _rateBytes += bytes;
        if (_rateWatch.ElapsedMilliseconds >= 1000)
        {
            Fps = _rateFrames * 1000.0 / _rateWatch.ElapsedMilliseconds;
            BitrateMbps = _rateBytes * 8 / 1000.0 / _rateWatch.ElapsedMilliseconds;
            _rateFrames = 0; _rateBytes = 0;
            _rateWatch.Restart();
        }
    }

    /// <summary>Stop showing video: the virtual camera switches to its placeholder, never a stale frame.</summary>
    public void GoOffline(bool paused = false)
    {
        Feed.SetState(paused ? VirtualCamFeed.FeedState.Paused : VirtualCamFeed.FeedState.Offline);
        Latest = null;
        Fps = 0; BitrateMbps = 0;
        _waitingForKeyframe = true;
    }

    /// <summary>True when frames are actually arriving (never report streaming without samples).</summary>
    public bool IsLive => (DateTime.UtcNow - LastFrameUtc).TotalSeconds < 1.5 && !Paused;

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(1000);
        Feed.Dispose();
        _decoder?.Dispose();
    }
}
