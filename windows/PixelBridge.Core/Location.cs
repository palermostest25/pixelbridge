using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace PixelBridge.Core;

/// <summary>LOCATION_SAMPLE payload (protocol section 5a). Sensitive: never log coordinates.</summary>
public sealed record LocationFix(double Lat, double Lon, double? AltM, double? AccM, double? SpeedMps, double? BearingDeg,
    long TimeMs, string Provider, bool Stale, int SatellitesUsed, int SatellitesVisible, double MeanCn0)
{
    public DateTime TimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(TimeMs).UtcDateTime;

    public static LocationFix Parse(string json)
    {
        var j = JsonNode.Parse(json)!.AsObject();
        double lat = (double)j["lat"]!, lon = (double)j["lon"]!;
        if (!double.IsFinite(lat) || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
            throw new Protocol.ProtocolException("invalid coordinates");
        return new LocationFix(lat, lon, (double?)j["altM"], (double?)j["accM"], (double?)j["speedMps"], (double?)j["bearingDeg"],
            (long)j["timeMs"]!, (string?)j["provider"] ?? "unknown", (bool?)j["stale"] ?? false,
            (int?)j["satellitesUsed"] ?? 0, (int?)j["satellitesVisible"] ?? 0, (double?)j["meanCn0"] ?? 0);
    }

    /// <summary>NMEA 0183 $GPGGA + $GPRMC sentences for GPS tools that read a TCP NMEA feed.</summary>
    public string ToNmea()
    {
        var t = TimeUtc;
        string hms = t.ToString("HHmmss.ff", CultureInfo.InvariantCulture);
        string LatField() { double a = Math.Abs(Lat); int d = (int)a; return $"{d:00}{(a - d) * 60:00.0000},{(Lat >= 0 ? 'N' : 'S')}"; }
        string LonField() { double a = Math.Abs(Lon); int d = (int)a; return $"{d:000}{(a - d) * 60:00.0000},{(Lon >= 0 ? 'E' : 'W')}"; }
        var inv = CultureInfo.InvariantCulture;
        string hdop = AccM is { } acc ? (acc / 5.0).ToString("0.0", inv) : "";
        string gga = $"GPGGA,{hms},{LatField()},{LonField()},{(Stale ? 0 : 1)},{SatellitesUsed:00},{hdop},{(AltM ?? 0).ToString("0.0", inv)},M,0.0,M,,";
        string knots = ((SpeedMps ?? 0) * 1.943844).ToString("0.0", inv);
        string rmc = $"GPRMC,{hms},{(Stale ? 'V' : 'A')},{LatField()},{LonField()},{knots},{(BearingDeg ?? 0).ToString("0.0", inv)},{t:ddMMyy},,,A";
        return Wrap(gga) + Wrap(rmc);
        static string Wrap(string s) { byte c = 0; foreach (var ch in s) c ^= (byte)ch; return $"${s}*{c:X2}\r\n"; }
    }
}

/// <summary>Opt-in NMEA-over-TCP feed on 127.0.0.1 (off by default). Loopback only; no Windows Location driver.</summary>
public sealed class NmeaServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<NetworkStream> _clients = new();
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }
    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    public NmeaServer(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var c = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    lock (_lock) _clients.Add(c.GetStream());
                }
                catch { break; }
            }
        });
        Log.Info($"NMEA feed listening on 127.0.0.1:{port}");
    }

    public void Publish(LocationFix f)
    {
        var bytes = Encoding.ASCII.GetBytes(f.ToNmea());
        lock (_lock)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                try { _clients[i].Write(bytes); }
                catch { _clients[i].Dispose(); _clients.RemoveAt(i); }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_lock) { foreach (var c in _clients) c.Dispose(); _clients.Clear(); }
    }
}
