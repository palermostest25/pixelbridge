namespace PixelBridge.Core;

public sealed record CompassReading(double Heading, double Pitch, double Roll, double? FieldMicrotesla, int Accuracy)
{
    public string Direction => new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)Math.Round(Heading / 45, MidpointRounding.AwayFromZero) % 8];
    public string Quality => Accuracy switch { >= 3 => "High accuracy", 2 => "Medium accuracy", 1 => "Low accuracy—move the phone in a figure eight", _ => "Unreliable—move away from magnets and calibrate" };
}
public static class CompassMath
{
    public static CompassReading? FromQuaternion(IReadOnlyList<float> q, int accuracy, double? field = null)
    {
        if (q.Count < 3 || q.Any(v => !float.IsFinite(v))) return null;
        double x = q[0], y = q[1], z = q[2], w = q.Count >= 4 ? q[3] : Math.Sqrt(Math.Max(0, 1 - x * x - y * y - z * z));
        double norm = Math.Sqrt(x * x + y * y + z * z + w * w); if (norm < 1e-8) return null;
        x /= norm; y /= norm; z /= norm; w /= norm;
        // Android SensorManager's rotation matrix: azimuth = atan2(R[1], R[4]).
        var heading = Normalize(Math.Atan2(2 * (x * y - z * w), 1 - 2 * (x * x + z * z)) * 180 / Math.PI);
        var pitch = Math.Asin(Math.Clamp(-2 * (y * z + x * w), -1, 1)) * 180 / Math.PI;
        var roll = Math.Atan2(-2 * (x * z - y * w), 1 - 2 * (x * x + y * y)) * 180 / Math.PI;
        return new(heading, pitch, roll, field, accuracy);
    }
    public static CompassReading? FromGravityAndMagnetic(IReadOnlyList<float> gravity, IReadOnlyList<float> magnetic, int accuracy)
    {
        if (gravity.Count < 3 || magnetic.Count < 3 || gravity.Any(v => !float.IsFinite(v)) || magnetic.Any(v => !float.IsFinite(v))) return null;
        double ax = gravity[0], ay = gravity[1], az = gravity[2], ex = magnetic[0], ey = magnetic[1], ez = magnetic[2];
        double hX = ey * az - ez * ay, hY = ez * ax - ex * az, hZ = ex * ay - ey * ax;
        var h = Math.Sqrt(hX * hX + hY * hY + hZ * hZ); var g = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (h < .1 || g < .1) return null;
        hX /= h; hY /= h; hZ /= h; ax /= g; ay /= g; az /= g;
        double mY = az * hX - ax * hZ;
        return new(Normalize(Math.Atan2(hY, mY) * 180 / Math.PI), Math.Asin(Math.Clamp(-ay, -1, 1)) * 180 / Math.PI, Math.Atan2(-ax, az) * 180 / Math.PI,
            Math.Sqrt(ex * ex + ey * ey + ez * ez), accuracy);
    }
    public static double Normalize(double degrees) => (degrees % 360 + 360) % 360;
    public static double ShortestDelta(double from, double to) => ((to - from + 540) % 360) - 180;
}
public sealed class CompassTracker
{
    private float[]? _gravity, _magnetic;
    private int _accuracy;
    private long _rotationAt, _lastPublish;
    public CompassReading? Reading { get; private set; }
    public void Reset() { _gravity = null; _magnetic = null; _rotationAt = 0; _lastPublish = 0; Reading = null; }
    public bool Accept(SensorSample sample)
    {
        if (sample.Type is SensorTypes.Gravity or SensorTypes.Accelerometer) _gravity = sample.Values;
        if (sample.Type == SensorTypes.MagneticField) { _magnetic = sample.Values; _accuracy = sample.Accuracy; }
        CompassReading? next = null;
        double? field = _magnetic?.Take(3).Sum(x => (double)x * x) is double squared ? Math.Sqrt(squared) : null;
        if (sample.Type == SensorTypes.RotationVector) { _rotationAt = sample.TimestampNs; next = CompassMath.FromQuaternion(sample.Values, sample.Accuracy, field); }
        else if (sample.TimestampNs - _rotationAt > 1_000_000_000 && _gravity != null && _magnetic != null) next = CompassMath.FromGravityAndMagnetic(_gravity, _magnetic, _accuracy);
        if (next == null || sample.TimestampNs - _lastPublish < 100_000_000) return false;
        _lastPublish = sample.TimestampNs; Reading = next; return true;
    }
}
