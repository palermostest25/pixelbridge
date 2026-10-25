using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixelBridge.Core;

namespace PixelBridge.App.Pages;

public partial class NetworkPage : UserControl, IPage
{
    private readonly AppController _c;
    private int _refreshCountdown;
    private bool _busy;
    private bool _hotspotBusy;
    private HotspotState? _hotspot;

    public NetworkPage(AppController c)
    {
        _c = c;
        InitializeComponent();
        WifiList.ItemsSource = c.Radio.WifiNetworks;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await _c.RefreshTetherAsync();
            if (_c.Session.IsConnected)
            {
                await _c.Radio.RefreshAsync();
                await LoadNetworksAsync();
                if (!_hotspotBusy) await HotspotAsync("status");
            }
            Render();
        }
        finally { _busy = false; }
    }

    private async Task LoadNetworksAsync()
    {
        JsonObject? r = null;
        try { r = await _c.Session.RadioAsync("net.status"); } catch { }
        var rows = new List<object>();
        foreach (var n in r?["result"]?["networks"]?.AsArray() ?? [])
        {
            var t = n!["transports"]!.AsArray().Select(x => (string)x!).ToList();
            var iface = (string?)n["interface"] ?? "";
            string title, glyph;
            if (iface.StartsWith("ncm") || iface.StartsWith("rndis")) { title = "USB tethering to this PC"; glyph = ""; }
            else if (t.Contains("vpn")) { title = "VPN"; glyph = ""; }
            else if (t.Contains("wifi")) { title = "Wi-Fi"; glyph = ""; }
            else if (t.Contains("cellular")) { title = iface.StartsWith("ipsec") ? "Mobile network (Wi-Fi calling)" : "Mobile network"; glyph = ""; }
            else { title = iface; glyph = ""; }
            int down = (int?)n["downKbps"] ?? 0;
            rows.Add(new
            {
                Glyph = glyph, Title = title,
                Detail = $"{iface}{((bool?)n["validated"] == true ? " · internet" : "")}{((bool?)n["metered"] == true ? " · metered" : "")}",
                Right = (bool?)n["default"] == true ? "Phone's default" : down > 0 ? $"~{down / 1000.0:0} Mbps" : "",
            });
        }
        Networks.ItemsSource = rows;
    }

    private void Render()
    {
        var t = _c.Tether;
        bool up = t?.WindowsAdapterUp == true;
        TetherIcon.Background = up ? (Brush)FindResource("AccentFillColorDefaultBrush") : (Brush)FindResource("SubtleFillColorSecondaryBrush");
        ((TextBlock)TetherIcon.Child).Foreground = up ? (Brush)FindResource("TextOnAccentFillColorPrimaryBrush") : (Brush)FindResource("TextFillColorPrimaryBrush");
        TetherTitle.Text = t is null ? "Checking…" : up ? (t.PhoneIsPreferred ? "On · this PC is using the Pixel for internet" : $"On · Windows prefers {t.OtherBestName}") : "Off";
        TetherDetail.Text = t is null ? "" : up
            ? $"Windows adapter “{t.AdapterName}” ({t.AdapterDescription}) · {t.WindowsIp} via {t.Gateway} · {t.LinkSpeedBps / 1e6:0} Mbps link"
            : "Turn on USB tethering on the phone, or set Developer options → Default USB configuration to USB tethering so it comes on whenever you plug in.";
        TetherChips.Children.Clear();
        if (t != null)
        {
            if (t.PersistentDefault) Chip("Turns on automatically after a restart");
            if (up && _c.Session.IsConnected) Chip("USB debugging still connected");
            if (t.PhoneMetric is { } pm) Chip($"Route metric {pm}{(t.OtherBestMetric is { } om ? $" vs {om}" : "")}");
        }
        RouteAuto.IsEnabled = RoutePhone.IsEnabled = RoutePc.IsEnabled = up;

        var w = _c.Radio.WifiStatus;
        if (w is null) { WifiTitle.Text = _c.Session.IsConnected ? "—" : "Phone not connected"; WifiDetail.Text = ""; }
        else if ((bool?)w["connected"] == true)
        {
            WifiTitle.Text = (string?)w["ssid"] ?? "Connected (name hidden: allow location on the phone to see it)";
            int f = (int?)w["frequencyMhz"] ?? 0;
            WifiDetail.Text = $"{w["rssi"]} dBm · {w["linkMbps"]} Mbps link · {(f >= 5925 ? "6 GHz" : f >= 4900 ? "5 GHz" : "2.4 GHz")} · Wi-Fi {StandardName((int?)w["standard"] ?? 0)}";
        }
        else { WifiTitle.Text = (bool?)w["enabled"] == true ? "Not connected" : "Wi-Fi is off on the phone"; WifiDetail.Text = ""; }
    }

    private static string StandardName(int s) => s switch { 4 => "4", 5 => "5", 6 => "6", 8 => "7", _ => "" };

    private void Chip(string text) => TetherChips.Children.Add(new Border
    {
        Style = (Style)FindResource("Pill"), Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, Style = (Style)FindResource("CaptionTextBlockStyle") },
    });

    public void Tick()
    {
        if (--_refreshCountdown <= 0) { _refreshCountdown = 40; _ = RefreshAsync(); }
        ScanButton.IsEnabled = _c.Session.IsConnected;
        HotspotToggle.IsEnabled = !_hotspotBusy && _c.Session.IsConnected && _hotspot?.Supported == true;
        HotspotToggle.Content = _hotspotBusy ? "Checking…" : _hotspot?.Enabled == true ? "Turn off hotspot" : "Turn on hotspot…";
        if (_c.Radio.WifiNetworks.Count > 0) WifiListCard.Visibility = Visibility.Visible;
        WifiStatus.Text = _c.Radio.Status;
    }

    private async void Hotspot_Click(object sender, RoutedEventArgs e)
    {
        if (_hotspotBusy) return;
        var action = _hotspot?.Enabled == true ? "off" : "on";
        if (action == "on" && MessageBox.Show(Window.GetWindow(this)!, HotspotControl.EnableNotice, "Turn on Wi-Fi hotspot?", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await HotspotAsync(action);
    }
    private async Task HotspotAsync(string action)
    {
        if (_hotspotBusy) return;
        _hotspotBusy = true; HotspotToggle.IsEnabled = false; HotspotMessage.Text = "";
        try {
            _hotspot = await HotspotControl.RunAsync(_c.Session, action);
            HotspotCard.Description = !_hotspot.Supported ? "Use Phone settings on this Android version. Direct controls require Android 16 or newer and authorized debugging." : _hotspot.Enabled ? "On · Uses the phone's existing hotspot name and password." : "Off · Share the phone's internet over Wi-Fi using its saved hotspot settings.";
            if (action != "status") HotspotMessage.Text = _hotspot.Enabled ? "Android confirmed: hotspot is on." : "Android confirmed: hotspot is off.";
        } catch (Exception error) { _hotspot = null; HotspotMessage.Text = error.Message; }
        finally { _hotspotBusy = false; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        WifiListCard.Visibility = Visibility.Visible;
        await _c.Radio.ScanWifiAsync();
        ScanButton.IsEnabled = true;
    }

    private async void Route_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Tether?.AdapterName is not { } name) return;
        var tag = (string)((FrameworkElement)sender).Tag;
        bool? pref = tag switch { "phone" => true, "pc" => false, _ => null };
        if (await TetherProbe.SetPreferenceAsync(name, pref)) MainWindow.Current?.ShowToast("Windows network preference updated.");
        else MainWindow.Current?.ShowToast("Network preference unchanged.");
        await RefreshAsync();
    }

    private async void OpenTether_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Session.Adb is null || !_c.Session.IsConnected) { MainWindow.Current?.ShowToast("Connect the phone first."); return; }
        await _c.Session.Adb.ShellAsync(_c.Session.Serial, "am start -n com.android.settings/.Settings\\$TetherSettingsActivity");
        _c.LaunchScrcpy(false);
    }
}
