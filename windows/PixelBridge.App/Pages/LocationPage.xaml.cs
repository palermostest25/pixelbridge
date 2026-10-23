using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PixelBridge.App.Pages;

public partial class LocationPage : UserControl, IPage
{
    private readonly AppController _c;
    private bool _hide;

    public LocationPage(AppController c)
    {
        _c = c;
        InitializeComponent();
        Nmea.IsChecked = c.Settings.NmeaEnabled;
        ApiLoc.IsChecked = c.Settings.ApiIncludeLocation;
        NmeaCard.Description = $"Streams $GPGGA/$GPRMC to tcp://127.0.0.1:{c.Settings.NmeaPort} for GPS software";
    }

    public void Tick()
    {
        var f = _c.Location;
        bool fresh = f != null && (DateTime.UtcNow - _c.LastLocationUtc).TotalSeconds < 30;
        StartStopText.Text = _c.LocationRequested ? "Stop location" : "Start location";
        StartStop.IsEnabled = _c.Session.IsConnected && !_c.PrivacyMode;
        MapButton.IsEnabled = CopyButton.IsEnabled = fresh;
        if (f is null)
        {
            FixTitle.Text = _c.PrivacyMode ? "Privacy mode is on" : _c.LocationError ?? (_c.LocationRequested ? "Waiting for a fix…" : "Location is off");
            Coords.Text = ""; FixDetail.Text = _c.LocationRequested ? "Indoors, satellites can take a minute or more. Network location usually arrives first." : "Uses the Pixel's GPS and network location.";
            Acc.Text = Alt.Text = Speed.Text = Source.Text = "—";
            SatCount.Text = "0";
            return;
        }
        var inv = CultureInfo.InvariantCulture;
        string fmt = _hide ? "0.00" : "0.000000";
        FixTitle.Text = f.Stale ? "Last known position" : f.Provider == "gps" ? "GPS fix" : "Approximate position";
        Coords.Text = $"{f.Lat.ToString(fmt, inv)}, {f.Lon.ToString(fmt, inv)}";
        FixDetail.Text = $"Updated {Math.Max(0, (DateTime.UtcNow - _c.LastLocationUtc).TotalSeconds):0} s ago · fix time {f.TimeUtc.ToLocalTime():HH:mm:ss}{(fresh ? "" : " · stale")}";
        Acc.Text = f.AccM is { } a ? $"±{a:0} m" : "—";
        Alt.Text = f.AltM is { } al ? $"{al:0} m" : "—";
        Speed.Text = f.SpeedMps is { } sp ? $"{sp * 3.6:0.0} km/h" : "—";
        Source.Text = f.Provider == "gps" ? "GPS" : f.Provider == "network" ? "Network" : f.Provider;
        SatCount.Text = f.SatellitesUsed.ToString();
        SatLabel.Text = f.SatellitesVisible > 0 ? $"in use of {f.SatellitesVisible}" : "satellites";
    }

    private void StartStop_Click(object sender, RoutedEventArgs e) { if (_c.LocationRequested) _c.StopLocation(); else _c.StartLocation(); }

    private void Map_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Location is not { } f) return;
        var inv = CultureInfo.InvariantCulture;
        // Opens the browser only on explicit click.
        Process.Start(new ProcessStartInfo($"https://www.openstreetmap.org/?mlat={f.Lat.ToString(inv)}&mlon={f.Lon.ToString(inv)}#map=16/{f.Lat.ToString(inv)}/{f.Lon.ToString(inv)}") { UseShellExecute = true });
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Location is not { } f) return;
        Clipboard.SetText($"{f.Lat.ToString(CultureInfo.InvariantCulture)}, {f.Lon.ToString(CultureInfo.InvariantCulture)}");
        MainWindow.Current?.ShowToast("Coordinates copied.");
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => _hide = Hide.IsChecked == true;
    private void Nmea_Click(object sender, RoutedEventArgs e) => _c.SetNmea(Nmea.IsChecked == true);
    private void ApiLoc_Click(object sender, RoutedEventArgs e) { _c.Settings.ApiIncludeLocation = ApiLoc.IsChecked == true; _c.Settings.Save(); }
}
