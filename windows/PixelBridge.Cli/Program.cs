using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PixelBridge.Core;
using PixelBridge.Core.Protocol;

// pbctl: bounded, scriptable hardware tests against the real phone. Every run has a hard time limit.
var cmd = args.Length > 0 ? args[0] : "status";
string Opt(string name, string def) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
var seconds = Math.Clamp(int.Parse(Opt("seconds", "10")), 1, 120); // never unbounded; 120 s cap
var settings = BridgeSettings.Load();
settings.AutoConnect = true;
Log.Line += l => { if (args.Contains("--verbose")) Console.Error.WriteLine(l); };

await using var session = new DeviceSession(settings);
session.Start();

async Task<bool> WaitConnected(int timeoutSec)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < timeoutSec)
    {
        if (session.State == LinkState.Connected) return true;
        if (session.State is LinkState.NotPaired && cmd == "pair")
        {
            Console.WriteLine("Not paired -> delivering pairing key over ADB");
            await session.PairAsync();
        }
        else if (session.State is LinkState.NotPaired or LinkState.ApkMissing or LinkState.ApkOutdated or LinkState.WrongDevice or LinkState.Unauthorized or LinkState.NoAdb)
        {
            Console.WriteLine($"BLOCKED {session.State}: {session.StateDetail}");
            return false;
        }
        await Task.Delay(250);
    }
    Console.WriteLine($"TIMEOUT state={session.State} {session.StateDetail}");
    return false;
}

if (!await WaitConnected(40)) return 2;
Console.WriteLine($"CONNECTED {session.Serial} {string.Join(" ", session.DeviceProps.Select(kv => $"{kv.Key}={kv.Value}"))}");
Console.WriteLine($"PORTS {string.Join(" ", session.PortMap.Select(kv => $"{kv.Key}=127.0.0.1:{kv.Value}"))}");
await Task.Delay(1200);
Console.WriteLine($"CAPABILITIES {session.Capabilities?.ToJsonString()}");
Console.WriteLine($"STATUS {session.ModuleStatus?.ToJsonString()}");

int rc = 0;
switch (cmd)
{
    case "status":
    case "pair":
        break;

    case "sensors":
    {
        var counts = new ConcurrentDictionary<int, int>();
        var last = new ConcurrentDictionary<int, SensorSample>();
        var tsBad = 0;
        var prevTs = new ConcurrentDictionary<int, long>();
        BatteryStatus? bat = null;
        StreamWriter? jsonl = Opt("out", "") is { Length: > 0 } o ? new StreamWriter(o) : null;
        session.Sensor += s =>
        {
            counts.AddOrUpdate(s.Type, 1, (_, c) => c + 1);
            if (prevTs.TryGetValue(s.Type, out var p) && s.TimestampNs <= p) Interlocked.Increment(ref tsBad);
            prevTs[s.Type] = s.TimestampNs;
            last[s.Type] = s;
            if (jsonl != null) lock (jsonl) jsonl.WriteLine(JsonSerializer.Serialize(new { t = s.TimestampNs, sensor = SensorTypes.Name(s.Type), acc = s.Accuracy, v = s.Values }));
        };
        session.Battery += b => bat = b;
        session.StartStream("sensors", new { rateHz = int.Parse(Opt("rate", "50")) });
        await Task.Delay(seconds * 1000);
        session.StopStream("sensors");
        jsonl?.Dispose();
        Console.WriteLine($"SENSORS over {seconds}s (non-monotonic timestamps: {tsBad})");
        foreach (var (t, n) in counts.OrderBy(k => k.Key))
            Console.WriteLine($"  {SensorTypes.Name(t),-22} {n,6} samples  {n / (double)seconds,7:0.0} Hz  last=[{string.Join(", ", last[t].Values.Select(v => v.ToString("0.000")))}] {SensorTypes.Unit(t)} acc={last[t].Accuracy}");
        Console.WriteLine($"BATTERY {bat}");
        Console.WriteLine($"CLOCK offset={session.Clock.OffsetNs / 1e6:0.0}ms lastRtt={session.Clock.LastRttMs:0.00}ms minRtt={session.Clock.MinRttMs:0.00}ms");
        if (!counts.ContainsKey(SensorTypes.Gyroscope) || !counts.ContainsKey(SensorTypes.Accelerometer) || bat is null) rc = 1;
        break;
    }

    case "video":
    {
        var outPath = Opt("out", Path.Combine(Path.GetTempPath(), "pixelbridge-test.h264"));
        await using var fs = File.Create(outPath);
        int frames = 0, keyframes = 0, configs = 0; long bytes = 0; long firstTs = 0, lastTs = 0;
        var arrivals = new List<double>();
        var sw = Stopwatch.StartNew();
        JsonObject? cfg = null;
        string? streamError = null;
        session.VideoConfig += c => cfg = c;
        session.StreamEvent += (t, j) => { if (t == MsgType.StreamError && (string?)j["kind"] == "video") streamError = $"{j["code"]}: {j["message"]}"; };
        var snapshot = Opt("snapshot", "");
        using var decoder = new PixelBridge.Media.H264Decoder();
        using var feed = args.Contains("--feed") || args.Contains("--vcam") ? new PixelBridge.Media.VirtualCamFeed() : null;
        if (args.Contains("--vcam"))
        {
            Console.WriteLine($"VCAM registered={PixelBridge.Media.VirtualCamera.IsSourceRegistered} start=0x{PixelBridge.Media.VirtualCamera.Start():X8}");
        }
        int decoded = 0; bool snapped = false;
        session.VideoFrame += f =>
        {
            lock (fs) fs.Write(f.Payload);
            lock (decoder)
            {
                decoder.Decode(f.Payload, f.TimestampNs);
                if (decoder.TakeLatest() is { } nv)
                {
                    decoded++;
                    feed?.Write(nv);
                    if (snapshot.Length > 0 && !snapped && sw.Elapsed.TotalSeconds > seconds / 2.0)
                    {
                        var bgra = new byte[nv.Width * nv.Height * 4];
                        nv.ToBgra(bgra, nv.Width * 4);
                        PixelBridge.Media.Bmp.Write(snapshot, bgra, nv.Width, nv.Height);
                        snapped = true;
                        Console.WriteLine($"SNAPSHOT {nv.Width}x{nv.Height} -> {snapshot}");
                    }
                }
            }
            if (f.Flags.HasFlag(FrameFlags.CodecConfig)) { configs++; return; }
            frames++; bytes += f.Payload.Length;
            if (f.Flags.HasFlag(FrameFlags.Keyframe)) keyframes++;
            if (firstTs == 0) firstTs = f.TimestampNs;
            lastTs = f.TimestampNs;
            lock (arrivals) arrivals.Add(sw.Elapsed.TotalMilliseconds);
        };
        var opts = new { cameraId = Opt("camera", "0"), width = int.Parse(Opt("width", "1280")), height = int.Parse(Opt("height", "720")),
            fps = int.Parse(Opt("fps", "30")), bitrate = int.Parse(Opt("bitrate", "4000000")) };
        session.StartStream("video", opts);
        await Task.Delay(seconds * 1000);
        session.StopStream("video");
        await Task.Delay(500);
        var span = (lastTs - firstTs) / 1e9;
        Console.WriteLine($"VIDEO_CONFIG {cfg?.ToJsonString()}");
        if (streamError != null) Console.WriteLine($"STREAM_ERROR {streamError}");
        Console.WriteLine($"VIDEO frames={frames} keyframes={keyframes} codecConfig={configs} bytes={bytes} " +
            $"fps(device pts)={(span > 0 ? (frames - 1) / span : 0):0.0} bitrate={bytes * 8 / (double)seconds / 1e6:0.00}Mbps file={outPath}");
        if (arrivals.Count > 2)
        {
            var gaps = arrivals.Zip(arrivals.Skip(1), (a, b) => b - a).OrderBy(x => x).ToList();
            Console.WriteLine($"ARRIVAL gap ms p50={gaps[gaps.Count / 2]:0.0} p95={gaps[(int)(gaps.Count * 0.95)]:0.0} max={gaps[^1]:0.0}");
        }
        Console.WriteLine($"DECODED frames={decoded} decodeErrors={decoder.DecodeErrors}");
        if (feed != null) Console.WriteLine($"VCAM_FEED section={feed.OpenedName ?? "not-open"} framesWritten={feed.FramesWritten} consumerActive={feed.ConsumerActive}");
        if (args.Contains("--vcam"))
        {
            feed?.SetState(PixelBridge.Media.VirtualCamFeed.FeedState.Offline);
            int holdAfter = int.Parse(Opt("hold", "0"));
            if (holdAfter > 0) { Console.WriteLine($"VCAM holding offline placeholder for {holdAfter}s"); await Task.Delay(Math.Min(holdAfter, 30) * 1000); }
            Console.WriteLine($"VCAM stop=0x{PixelBridge.Media.VirtualCamera.Stop():X8}");
        }
        if (frames == 0 || configs == 0 || decoded == 0) rc = 1;
        break;
    }

    case "audio":
    {
        var outPath = Opt("out", Path.Combine(Path.GetTempPath(), "pixelbridge-test.wav"));
        var pcm = new MemoryStream();
        int frames = 0; double sumSq = 0; long n = 0; int peak = 0;
        JsonObject? cfg = null; string? streamError = null;
        session.AudioConfig += c => cfg = c;
        session.StreamEvent += (t, j) => { if (t == MsgType.StreamError && (string?)j["kind"] == "audio") streamError = $"{j["code"]}: {j["message"]}"; };
        session.AudioFrame += f =>
        {
            lock (pcm) pcm.Write(f.Payload);
            frames++;
            for (int i = 0; i + 1 < f.Payload.Length; i += 2)
            {
                short s = BitConverter.ToInt16(f.Payload, i);
                sumSq += s * (double)s; n++; peak = Math.Max(peak, Math.Abs((int)s));
            }
        };
        session.StartStream("audio", new { rateHz = 48000 });
        await Task.Delay(seconds * 1000);
        session.StopStream("audio");
        await Task.Delay(300);
        Wav.Write(outPath, pcm.ToArray(), 48000, 1);
        var rms = n > 0 ? Math.Sqrt(sumSq / n) : 0;
        Console.WriteLine($"AUDIO_CONFIG {cfg?.ToJsonString()}");
        if (streamError != null) Console.WriteLine($"STREAM_ERROR {streamError}");
        Console.WriteLine($"AUDIO frames={frames} ({frames * 20 / 1000.0:0.0}s of audio in {seconds}s) rms={20 * Math.Log10(Math.Max(rms, 1) / 32768):0.0}dBFS peak={20 * Math.Log10(Math.Max(peak, 1) / 32768.0):0.0}dBFS file={outPath}");
        if (frames == 0) rc = 1;
        break;
    }

    case "location":
    {
        // Prints fix quality only; coordinates are redacted to 2 decimals (~1 km) unless --precise.
        int n = 0; LocationFix? last = null; string? err = null;
        session.Location += f => { n++; last = f; };
        session.StreamEvent += (t, j) => { if (t == MsgType.StreamError && (string?)j["kind"] == "location") err = (string?)j["message"]; };
        session.StartStream("location", new { intervalMs = 1000 });
        await Task.Delay(seconds * 1000);
        session.StopStream("location");
        if (err != null) Console.WriteLine($"STREAM_ERROR {err}");
        if (last is { } l)
        {
            var fmt = args.Contains("--precise") ? "0.000000" : "0.00";
            Console.WriteLine($"LOCATION fixes={n} provider={l.Provider} stale={l.Stale} lat~{l.Lat.ToString(fmt)} lon~{l.Lon.ToString(fmt)} acc={l.AccM:0.0}m alt={l.AltM:0}m sats used/visible={l.SatellitesUsed}/{l.SatellitesVisible} cn0={l.MeanCn0:0.0}dBHz");
            Console.WriteLine("NMEA sample (redacted): " + System.Text.RegularExpressions.Regex.Replace(l.ToNmea().Split('\n')[0], @",\d{4,5}\.\d{4},", ",####.####,"));
        }
        else rc = 1;
        Console.WriteLine($"LOCATION_FIXES {n}");
        break;
    }

    case "radio":
    {
        // pbctl radio <op> [--args '{"json":1}'] [--seconds N]  (events are printed for N seconds)
        var op = args.Length > 1 ? args[1] : "caps";
        var a = JsonNode.Parse(Opt("args", "{}"));
        int events = 0;
        session.RadioEvent += e => { events++; if (events <= 40) Console.WriteLine($"EVENT {e.ToJsonString()}"); };
        var resp = await session.RadioAsync(op, a, TimeSpan.FromSeconds(20));
        Console.WriteLine($"RESPONSE {resp.ToJsonString()}");
        if (args.Contains("--seconds")) await Task.Delay(seconds * 1000);
        Console.WriteLine($"EVENTS {events}");
        if (resp["ok"]?.GetValue<bool>() != true) rc = 1;
        break;
    }

    default:
        Console.WriteLine("usage: pbctl status|pair|sensors|video|audio|radio [--seconds N] [--out path] [--camera 0|1] [--verbose]");
        rc = 64;
        break;
}
return rc;

static class Wav
{
    public static void Write(string path, byte[] pcm, int rate, int channels)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
    }
}
