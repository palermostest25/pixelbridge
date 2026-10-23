using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PixelBridge.Core;

namespace PixelBridge.App.Pages;

public partial class HomePage : UserControl, IPage
{
    private readonly AppController _c;
    private readonly List<Tile> _tiles = new();

    private sealed class Tile
    {
        public required string Key;
        public required TextBlock State;
        public required Border Icon;
        public CheckBox? Switch;
    }

    public HomePage(AppController c)
    {
        _c = c;
        InitializeComponent();
        AddTile("camera", "", "Camera", toggle: on => { if (on) _c.StartCamera(); else _c.StopCamera(); });
        AddTile("mic", "", "Microphone", toggle: on => { if (on) _c.StartMic(); else _c.StopMic(); });
        AddTile("network", "", "Internet sharing");
        AddTile("location", "\uE81D", "Location", toggle: on => { if (on) _c.StartLocation(); else _c.StopLocation(); });
        AddTile("bluetooth", "", "Bluetooth");
        AddTile("sensors", "", "Motion sensors");
        _ = c.RefreshTetherAsync();
    }

    private void AddTile(string key, string glyph, string title, Action<bool>? toggle = null)
    {
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("SubtleFillColorSecondaryBrush"),
            Child = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 18 },
        };
        var state = new TextBlock { Style = (Style)FindResource("Secondary"), Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("BodyStrongTextBlockStyle") });
        text.Children.Add(state);
        var row = new DockPanel();
        CheckBox? sw = null;
        if (toggle != null)
        {
            sw = new CheckBox { Style = (Style)FindResource("ToggleSwitch") };
            sw.Click += (_, _) => toggle(sw.IsChecked == true);
            DockPanel.SetDock(sw, Dock.Right);
            row.Children.Add(sw);
        }
        row.Children.Add(icon);
        row.Children.Add(text);
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(16, 14, 16, 14), Child = row, Cursor = Cursors.Hand };
        card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not FrameworkElement fe || !IsInside<CheckBox>(fe)) MainWindow.Current?.NavigateTo(key); };
        Tiles.Children.Add(card);
        _tiles.Add(new Tile { Key = key, State = state, Icon = icon, Switch = sw });
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p)) if (p is T) return true;
        return false;
    }

    private int _tetherCountdown;

    public void Tick()
    {
        var s = _c.Session;
        bool on = s.IsConnected;
        DeviceName.Text = s.DeviceProps.GetValueOrDefault("ro.product.model") is { Length: > 0 } m ? m : "Pixel 7";
        DeviceStatus.Text = _c.PrivacyMode ? "Privacy mode is on. Camera, microphone, location and sensors are paused."
            : on ? "Connected over USB" : $"{MainWindow.StateLabel(s.State)} · {s.StateDetail}";
        ConnectButton.Content = on ? "Disconnect" : "Connect";
        PrivacySwitch.IsChecked = _c.PrivacyMode;
        HeroGlyph.Text = on ? "" : "";
        HeroGlyph.Foreground = on ? (Brush)FindResource("SystemFillColorSuccessBrush") : new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));

        Chips.Children.Clear();
        if (s.DeviceProps.Count > 0)
        {
            Chip($"Android {s.DeviceProps.GetValueOrDefault("ro.build.version.release")}");
            Chip(s.Serial);
        }
        if (_c.Battery is { } b && on) Chip($"{b.Percent}% battery");
        if (_c.Tether?.WindowsAdapterUp == true) Chip("Sharing internet");

        // Action bar for states the user must resolve.
        (string title, string text, string button)? action = s.State switch
        {
            LinkState.NotPaired => ("Pair this PC", "A one-time key is sent over the USB debugging connection you've already allowed.", "Pair"),
            LinkState.ApkMissing => ("Install the phone app", "PixelBridge needs its companion app on the Pixel.", "Install…"),
            LinkState.ApkOutdated => ("Update the phone app", s.StateDetail, "Update…"),
            LinkState.Unauthorized => ("Allow USB debugging", "Accept the prompt on the phone (use Phone screen to see it).", "Open phone screen"),
            LinkState.WrongDevice => ("Use this phone?", s.StateDetail + ". PixelBridge only connects to phones you approve.", "Use this phone"),
            _ => null,
        };
        ActionBar.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        if (action is { } a) { ActionTitle.Text = a.title; ActionText.Text = a.text; ActionButton.Content = a.button; }

        foreach (var t in _tiles)
        {
            var (state, active) = t.Key switch
            {
                "camera" => _c.Video.IsLive ? ($"Live · {_c.Video.Fps:0} fps{(_c.Video.Feed.ConsumerActive ? " · in use" : "")}", true) : (_c.CameraError ?? (_c.CameraRequested ? "Starting…" : "Off"), false),
                "mic" => _c.Audio.IsLive ? (_c.Audio.Muted ? "Live · muted" : "Live", true) : (_c.MicError ?? (_c.MicRequested ? "Starting…" : "Off"), false),
                "network" => _c.Tether is { WindowsAdapterUp: true } ts ? (ts.PhoneIsPreferred ? "On · PC uses phone" : "On · PC prefers its own", true) : ("Off", false),
                "location" => _c.Location is { } f && (DateTime.UtcNow - _c.LastLocationUtc).TotalSeconds < 30
                    ? ($"±{f.AccM:0} m · {f.SatellitesUsed} satellites", true) : (_c.LocationError ?? (_c.LocationRequested ? "Waiting for a fix…" : "Off"), false),
                "bluetooth" => _c.Radio.Scanning ? ("Scanning…", true) : _c.Radio.Devices.Count > 0 ? ($"{_c.Radio.Devices.Count} nearby", false) : ("Ready", false),
                "sensors" => on && (DateTime.UtcNow - _c.LastSensorUtc).TotalSeconds < 1.5 ? ($"Streaming · {_c.Settings.SensorRateHz} Hz", true) : ("Off", false),
                _ => ("", false),
            };
            t.State.Text = state;
            t.Icon.Background = active ? (Brush)FindResource("AccentFillColorDefaultBrush") : (Brush)FindResource("SubtleFillColorSecondaryBrush");
            ((TextBlock)t.Icon.Child).Foreground = active ? (Brush)FindResource("TextOnAccentFillColorPrimaryBrush") : (Brush)FindResource("TextFillColorPrimaryBrush");
            if (t.Switch != null)
            {
                t.Switch.IsEnabled = on && !_c.PrivacyMode;
                t.Switch.IsChecked = t.Key switch { "camera" => _c.CameraRequested, "mic" => _c.MicRequested, "location" => _c.LocationRequested, _ => false };
            }
        }

        if (_c.Battery is { } bat && on)
        {
            BatteryPercent.Text = $"{bat.Percent}%";
            BatteryTemp.Text = bat.TempC is { } tc ? $"{tc:0.0} °C" : "";
            BatteryBar.Value = bat.Percent;
            BatteryDetail.Text = $"{(bat.Charging ? "Charging" : "Not charging")} over {bat.Plugged} · {bat.VoltageMv / 1000.0:0.00} V{(bat.CurrentMa is { } ma ? $" · {ma} mA" : "")} · health {bat.Health}";
        }
        else { BatteryPercent.Text = "—"; BatteryTemp.Text = ""; BatteryBar.Value = 0; BatteryDetail.Text = "Connect the phone to see battery status."; }

        LinkSummary.Text = on ? $"USB · round trip {s.Clock.LastRttMs:0.0} ms" : "No active link";
        LinkDetail.Text = on ? string.Join("\n", s.PortMap.Select(kv => $"{kv.Key,-10} 127.0.0.1:{kv.Value}")) : "";
        OtherDevices.Text = s.OtherDevices.Count > 0 ? $"Ignoring other ADB devices: {string.Join(", ", s.OtherDevices.Select(d => $"{d.Serial} ({d.State})"))}" : "";

        if (--_tetherCountdown <= 0) { _tetherCountdown = 40; _ = _c.RefreshTetherAsync(); } // every ~10 s
    }

    private void Chip(string text)
    {
        Chips.Children.Add(new Border
        {
            Style = (Style)FindResource("Pill"), Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock { Text = text, Style = (Style)FindResource("CaptionTextBlockStyle") },
        });
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Session.IsConnected) await _c.Session.DisconnectAsync(); else _c.Session.RetryNow();
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        _c.SetPrivacy(PrivacySwitch.IsChecked == true);
        ((App)Application.Current).UpdateTray();
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        switch (_c.Session.State)
        {
            case LinkState.NotPaired:
                if (MessageBox.Show(Window.GetWindow(this), "Pair this PC with your Pixel?\n\nA random key is sent to the PixelBridge app over the USB debugging connection you've already authorised. It's stored encrypted on both sides.",
                        "Pair PixelBridge", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    await _c.Session.PairAsync();
                break;
            case LinkState.ApkMissing or LinkState.ApkOutdated:
                await PhonePage.InstallApkAsync(_c, Window.GetWindow(this));
                break;
            case LinkState.Unauthorized:
                _c.LaunchScrcpy(false);
                break;
            case LinkState.WrongDevice:
                _c.Session.ApproveCurrentDevice();
                break;
        }
    }
}
