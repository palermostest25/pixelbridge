using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PixelBridge.Core.Protocol;

namespace PixelBridge.Core;

public static class SensorTypes
{
    public const int Accelerometer = 1, MagneticField = 2, Gyroscope = 4, Light = 5, Pressure = 6, Proximity = 8,
        Gravity = 9, LinearAcceleration = 10, RotationVector = 11, GameRotationVector = 15;

    public static string Name(int t) => t switch
    {
        Accelerometer => "accelerometer", MagneticField => "magnetic_field", Gyroscope => "gyroscope", Light => "light",
        Pressure => "pressure", Proximity => "proximity", Gravity => "gravity", LinearAcceleration => "linear_acceleration",
        RotationVector => "rotation_vector", GameRotationVector => "game_rotation_vector", _ => $"type_{t}",
    };

    public static string Unit(int t) => t switch
    {
        Accelerometer or Gravity or LinearAcceleration => "m/s²", Gyroscope => "rad/s", MagneticField => "µT",
        Light => "lx", Pressure => "hPa", Proximity => "cm", _ => "",
    };
}

public sealed record SensorSample(int Type, int Accuracy, float[] Values, long TimestampNs)
{
    /// <summary>Decode a SENSOR_SAMPLE payload (protocol section 5). Rejects truncated payloads and non-finite values.</summary>
    public static SensorSample Parse(Frame f)
    {
        var p = f.Payload.AsSpan();
        if (p.Length < 8) throw new ProtocolException("sensor payload too short");
        int type = BinaryPrimitives.ReadInt32BigEndian(p);
        int accuracy = (sbyte)p[4];
        int n = p[5];
        if (n > 16 || p.Length != 8 + 4 * n) throw new ProtocolException($"sensor payload length {p.Length} for n={n}");
        var values = new float[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleBigEndian(p[(8 + 4 * i)..]);
            if (!float.IsFinite(values[i])) throw new ProtocolException("non-finite sensor value");
        }
        return new SensorSample(type, accuracy, values, f.TimestampNs);
    }
}

public sealed record BatteryStatus(int Percent, double? TempC, bool Charging, string Plugged, int VoltageMv, int? CurrentMa, string Health)
{
    public static BatteryStatus Parse(string json)
    {
        var j = JsonNode.Parse(json)!.AsObject();
        return new BatteryStatus((int)j["percent"]!, (double?)j["tempC"], (bool)j["charging"]!, (string?)j["plugged"] ?? "",
            (int?)j["voltageMv"] ?? -1, (int?)j["currentMa"], (string?)j["health"] ?? "unknown");
    }
}

public static class HostClock
{
    public static long NowNs() => (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));
}

/// <summary>Device elapsedRealtimeNanos ↔ host monotonic offset from HEARTBEAT/PONG, using the min-RTT of the last 16 samples.</summary>
public sealed class ClockSync
{
    private readonly Queue<(long rtt, long offset)> _samples = new();
    public long OffsetNs { get; private set; }
    public double LastRttMs { get; private set; }
    public double MinRttMs { get; private set; }
    public bool HasEstimate => _samples.Count > 0;

    public void Add(long hostSentNs, long deviceNs, long hostRecvNs)
    {
        long rtt = hostRecvNs - hostSentNs;
        if (rtt < 0) return;
        _samples.Enqueue((rtt, deviceNs - (hostSentNs + hostRecvNs) / 2));
        while (_samples.Count > 16) _samples.Dequeue();
        var best = _samples.MinBy(s => s.rtt);
        OffsetNs = best.offset;
        LastRttMs = rtt / 1e6;
        MinRttMs = best.rtt / 1e6;
    }

    /// <summary>Host monotonic ns at which a device-timestamped event happened.</summary>
    public long ToHostNs(long deviceNs) => deviceNs - OffsetNs;
}
