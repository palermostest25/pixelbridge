using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PixelBridge.Core;
using PixelBridge.Linux.Media;

namespace PixelBridge.Linux;

public partial class MainWindow : Window
{
    private readonly AppModel _m;
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private int _busy;
    private bool _allowClose;
    private int _tetherCountdown;
    private bool _loadingCamera = true;
    private string _lensSignature = "";
    private DispatcherTimer? _zoomTimer;
    private readonly Dictionary<string, (TextBlock state, Border icon)> _tiles = new();

    public MainWindow() : this(new AppModel()) { }

    public MainWindow(AppModel m)
    {
        _m = m;
        InitializeComponent();
        foreach (var (key, title) in new[] { ("camera", "Camera"), ("mic", "Microphone"), ("network", "Internet sharing"), ("location", "Location"), ("bluetooth", "Bluetooth"), ("sensors", "Motion sensors") })
            AddTile(key, title);
        Nav.SelectedIndex = 0;
        Stabilization.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "auto", "optical", "electronic", "off" }, m.Settings.CameraStabilization));
        Orientation.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "auto", "0", "90", "180", "270" }, m.Settings.CameraOrientation));
        Mirror.IsChecked = m.Settings.PreviewMirror; SavePower.IsChecked = m.Settings.SavePower;
        _zoomTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _zoomTimer.Tick += (_, _) => { _zoomTimer.Stop(); _m.UpdateCameraControls(); };
        Zoom.PropertyChanged += (_, e) => { if (!_loadingCamera && e.Property == Slider.ValueProperty) { _m.Settings.CameraZoom = Zoom.Value; _zoomTimer.Stop(); _zoomTimer.Start(); } };
        _loadingCamera = false;
        SerialBox.Text = m.Settings.TargetSerial;
        AutoConnect.IsChecked = m.Settings.AutoConnect;
        Autostart.IsChecked = File.Exists(AutostartPath);
        ApiUrl.Text = $"ws://127.0.0.1:{m.Settings.SensorWebSocketPort}/sensors?token={m.Settings.ApiToken}";
        AboutText.Text = $"PixelBridge {typeof(App).Assembly.GetName().Version?.ToString(3)} · phone app {DeviceSession.ExpectedAppVersion} · settings in {AppPaths.DataDir}";
        m.Video.PreviewFrame += OnPreview;
        m.Toast += msg => Dispatcher.UIThread.Post(() => ShowToast(msg));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    private static string AutostartPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "pixelbridge.desktop");

    private void AddTile(string key, string title)
    {
        var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(8), Background = Brush("PbSubtle"),
            Child = new TextBlock { Text = title[..1], HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontWeight = FontWeight.SemiBold } };
        var state = new TextBlock { Classes = { "secondary" } };
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        text.Children.Add(state);
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        row.Children.Add(icon); row.Children.Add(text);
        var card = new Border { Classes = { "card" }, Margin = new Thickness(0, 0, 8, 8), Child = row, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        card.PointerReleased += (_, _) => Navigate(key);
        Tiles.Children.Add(card);
        _tiles[key] = (state, icon);
    }

    private IBrush Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var r) && r is IBrush b ? b : Brushes.Gray;

    private void Nav_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem { Tag: string key }) Show(key);
    }

    public void Navigate(string key)
    {
        foreach (ListBoxItem item in Nav.Items.OfType<ListBoxItem>()) if ((string?)item.Tag == key) Nav.SelectedItem = item;
    }

    private string _page = "home";

    private void Show(string key)
    {
        _page = key;
        PageHome.IsVisible = key == "home"; PageCamera.IsVisible = key == "camera"; PageMic.IsVisible = key == "mic";
        PageSensors.IsVisible = key == "sensors"; PageLocation.IsVisible = key == "location"; PageNetwork.IsVisible = key == "network";
        PageBluetooth.IsVisible = key == "bluetooth"; PagePhone.IsVisible = key == "phone"; PageSettings.IsVisible = key == "settings";
        Tick();
    }

    private void OnPreview(byte[] bgra, int w, int h)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _page != "camera" || Interlocked.Exchange(ref _busy, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
                {
                    _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    Preview.Source = _bitmap;
                }
                using (var fb = _bitmap.Lock()) Marshal.Copy(bgra, 0, fb.Address, Math.Min(bgra.Length, fb.RowBytes * h));
                Preview.InvalidateVisual();
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }, DispatcherPriority.Render);
    }

    private void Tick()
    {
        _m.Session.SetSensorDemand("page", IsVisible && WindowState != WindowState.Minimized && _page == "sensors");
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        var s = _m.Session;
        bool on = s.IsConnected;
        StatusDot.Fill = on ? Brush("PbSuccess") : s.State == LinkState.Connecting ? Brush("PbCaution") : Brushes.Gray;
        StatusText.Text = _m.Privacy ? "Privacy mode" : on ? $"{s.DeviceProps.GetValueOrDefault("ro.product.model", "Phone")} connected" : s.State.Label();

        // Home
        DeviceName.Text = s.DeviceProps.GetValueOrDefault("ro.product.model") is { Length: > 0 } mdl ? mdl : "Your phone";
        DeviceStatus.Text = _m.Privacy ? "Privacy mode is on. Camera, microphone, location and sensors are paused." : on ? "Connected over USB" : (s.StateDetail.Length > 0 ? s.StateDetail : s.State.Label());
        HeroMark.Text = on ? "✓" : "—";
        ConnectButton.Content = on ? "Disconnect" : "Connect";
        PrivacySwitch.IsChecked = _m.Privacy;
        Chips.Children.Clear();
        if (s.DeviceProps.TryGetValue("ro.build.version.release", out var rel)) Chip($"Android {rel}");
        if (_m.Battery is { } b0 && on) Chip($"{b0.Percent}% battery");
        if (_m.Tether?.WindowsAdapterUp == true) Chip("Sharing internet");
        (string t, string d, string btn)? action = s.State switch
        {
            LinkState.NotPaired => ("Pair this computer", "A one-time key is sent over the USB debugging connection you've already allowed.", "Pair"),
            LinkState.WrongDevice => ("Use this phone?", s.StateDetail, "Use this phone"),
            LinkState.ApkMissing or LinkState.ApkOutdated => ("Install the phone app", s.StateDetail + ". Run: adb install PixelBridge.apk", "Show phone screen"),
            _ => null,
        };
        ActionCard.IsVisible = action != null;
        if (action is { } a) { ActionTitle.Text = a.t; ActionText.Text = a.d; ActionButton.Content = a.btn; }
        SetTile("camera", _m.Video.IsLive ? "Live" : _m.CameraError ?? (_m.CameraOn ? "Starting…" : "Off"), _m.Video.IsLive);
        SetTile("mic", _m.Mic.IsLive ? (_m.Mic.Muted ? "Live · muted" : "Live") : _m.MicError ?? (_m.MicOn ? "Starting…" : "Off"), _m.Mic.IsLive);
        SetTile("network", _m.Tether?.WindowsAdapterUp == true ? $"On · {_m.Tether.AdapterName}" : "Off", _m.Tether?.WindowsAdapterUp == true);
        SetTile("location", _m.Location is { } lf ? $"±{lf.AccM:0} m" : _m.LocationOn ? "Waiting…" : "Off", _m.Location != null);
        SetTile("bluetooth", _m.Scanning ? "Scanning…" : $"{_m.BleDevices.Count} nearby", _m.Scanning);
        SetTile("sensors", on && (DateTime.UtcNow - _m.LastSensorUtc).TotalSeconds < 1.5 ? "Streaming" : "Off", on && (DateTime.UtcNow - _m.LastSensorUtc).TotalSeconds < 1.5);
        if (_m.Battery is { } b && on)
        {
            BatteryBig.Text = $"{b.Percent}%  {(b.TempC is { } tc ? $"{tc:0.0} °C" : "")}";
            BatteryBar.Value = b.Percent;
            BatteryDetail.Text = $"{(b.Charging ? "Charging" : "Not charging")} · {b.VoltageMv / 1000.0:0.00} V · health {b.Health}";
        }
        else { BatteryBig.Text = "—"; BatteryBar.Value = 0; BatteryDetail.Text = "Connect the phone to see battery status."; }
        LinkSummary.Text = on ? $"USB · round trip {s.Clock.LastRttMs:0.0} ms" : "No active link";
        LinkPorts.Text = string.Join("\n", s.PortMap.Select(kv => $"{kv.Key,-10} 127.0.0.1:{kv.Value}"));

        // Camera
        var controls = _m.Camera;
        var signature = string.Join("|", controls.Lenses);
        if (_lensSignature != signature)
        {
            _loadingCamera = true; _lensSignature = signature; Lens.ItemsSource = controls.Lenses;
            Lens.SelectedItem = controls.Lenses.FirstOrDefault(l => l.CameraId == _m.Settings.CameraId && (l.Title == "Ultra wide") == (_m.Settings.CameraZoom < .95)) ?? controls.Lenses.FirstOrDefault();
            Zoom.Minimum = controls.Current?.MinZoom ?? 1; Zoom.Maximum = controls.Current?.MaxZoom ?? 1; Zoom.Value = _m.Settings.CameraZoom; _loadingCamera = false;
        }
        ZoomText.Text = $"{_m.Settings.CameraZoom:0.00}×";
        Optical.IsEnabled = controls.Current?.Optical == true && _m.Settings.CameraZoom >= 1;
        Electronic.IsEnabled = controls.Current?.Electronic == true;
        StabilizationState.Text = CameraControls.StabilizationStatus(_m.VideoConfig);
        if (!_m.Video.IsLive && _bitmap != null) { Preview.Source = null; _bitmap = null; } // never a stale frame
        PreviewText.IsVisible = !_m.Video.IsLive;
        PreviewText.Text = _m.Privacy ? "Privacy mode is on" : !on ? "Phone not connected" : _m.CameraError ?? (_m.CameraOn ? "Starting camera…" : "Camera is off");
        LiveChip.IsVisible = _m.Video.IsLive;
        LiveText.Text = $"LIVE  ·  {(_m.Video.V4l2Device is { } dev ? $"also on {dev}" : "preview only")}";
        CamButton.Content = _m.CameraOn ? "Stop camera" : "Start camera";
        CamButton.IsEnabled = on && !_m.Privacy;
        var lb = FfmpegVideo.FindLoopback();
        LoopbackText.Text = !FfmpegVideo.Available ? "ffmpeg is required: sudo apt install ffmpeg"
            : lb != null ? $"Ready: apps can choose “PixelBridge Camera” ({lb})."
            : "Not set up. Loading the v4l2loopback module needs administrator rights once (you'll be asked for your password).";

        // Mic
        MicState.Text = _m.Mic.IsLive ? (_m.Mic.Muted ? "Live, muted" : "Live") : _m.MicError ?? (_m.MicOn ? "Starting…" : "Microphone is off");
        MicLevel.Value = _m.Mic.IsLive ? Math.Max(-60, _m.Mic.LevelDbfs) : -60;
        MicButton.Content = _m.MicOn ? "Stop microphone" : "Start microphone";
        MicButton.IsEnabled = on && !_m.Privacy;

        // Sensors
        if (_page == "sensors")
        {
            var sb = new StringBuilder();
            lock (_m.LatestSensors)
                foreach (var (t, smp) in _m.LatestSensors.OrderBy(k => k.Key))
                    sb.AppendLine($"{SensorTypes.Name(t),-22} {string.Join("  ", smp.Values.Take(4).Select(v => v.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(8)))} {SensorTypes.Unit(t)}");
            SensorTable.Text = sb.Length > 0 ? sb.ToString() : "No sensor data yet.";
        }

        // Location
        LocButton.Content = _m.LocationOn ? "Stop location" : "Start location";
        LocButton.IsEnabled = on && !_m.Privacy;
        if (_m.Location is { } f)
        {
            LocTitle.Text = f.Provider == "gps" ? "GPS fix" : "Approximate position";
            LocCoords.Text = $"{f.Lat.ToString("0.000000", CultureInfo.InvariantCulture)}, {f.Lon.ToString("0.000000", CultureInfo.InvariantCulture)}";
            LocDetail.Text = $"±{f.AccM:0} m · {f.SatellitesUsed}/{f.SatellitesVisible} satellites · {f.Provider}";
        }
        else { LocTitle.Text = _m.LocationOn ? "Waiting for a fix…" : "Location is off"; LocCoords.Text = ""; LocDetail.Text = "Uses the phone's GPS and network location. Never logged."; }

        // Network
        if (--_tetherCountdown <= 0) { _tetherCountdown = 40; _ = _m.RefreshTetherAsync(); }
        var tt = _m.Tether;
        TetherTitle.Text = tt is null ? "Checking…" : tt.WindowsAdapterUp ? (tt.PhoneIsPreferred ? "On · this computer uses the phone for internet" : "On · another connection is preferred") : "Off";
        TetherDetail.Text = tt is { WindowsAdapterUp: true } ? $"{tt.AdapterName} · {tt.WindowsIp} via {tt.Gateway}" : "Turn on USB tethering on the phone (Developer options → Default USB configuration → USB tethering keeps it on).";
        WifiText.Text = _m.Wifi is { } w ? ((bool?)w["connected"] == true ? $"{(string?)w["ssid"] ?? "Connected"} · {w["rssi"]} dBm · {w["linkMbps"]} Mbps" : "Not connected") : "—";

        // Bluetooth
        BleButton.Content = _m.Scanning ? "Scanning…" : "Scan nearby";
        BleButton.IsEnabled = on && !_m.Privacy && !_m.Scanning;
        if (_page == "bluetooth")
            lock (_m.BleDevices)
                BleList.Text = _m.BleDevices.Count == 0 ? (_m.Scanning ? "Looking for devices…" : _m.RadioStatus.Length > 0 ? _m.RadioStatus : "Press Scan nearby.")
                    : string.Join("\n", _m.BleDevices.OrderByDescending(d => (int?)d["rssi"] ?? -100).Select(d => $"{(int?)d["rssi"],4} dBm  {((string?)d["name"] ?? "(unnamed)"),-28} {d["address"]}"));

        PairText.Text = s.PairedUtc is { } p ? $"Paired {p.ToLocalTime():g}" : "Not paired";
    }

    private void SetTile(string key, string state, bool active)
    {
        var (t, icon) = _tiles[key];
        t.Text = state;
        var accent = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var c) && c is Color col ? col : Colors.DodgerBlue;
        icon.Background = active ? new SolidColorBrush(accent) : Brush("PbSubtle");
        if (icon.Child is TextBlock tb) tb.Foreground = active ? Brushes.White : Brush("PbText");
    }

    private void Chip(string text) => Chips.Children.Add(new Border
    {
        CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 6, 0), Background = Brush("PbSubtle"),
        Child = new TextBlock { Text = text, FontSize = 12 },
    });

    public void ShowToast(string msg)
    {
        ToastText.Text = msg;
        Toast.IsVisible = true;
        DispatcherTimer.RunOnce(() => Toast.IsVisible = false, TimeSpan.FromSeconds(5));
    }

    private async void Connect_Click(object? s, RoutedEventArgs e) { if (_m.Session.IsConnected) await _m.Session.DisconnectAsync(); else _m.Session.RetryNow(); }
    private void Privacy_Click(object? s, RoutedEventArgs e) => _m.SetPrivacy(PrivacySwitch.IsChecked == true);

    private async void Action_Click(object? s, RoutedEventArgs e)
    {
        switch (_m.Session.State)
        {
            case LinkState.NotPaired: await _m.Session.PairAsync(); break;
            case LinkState.WrongDevice: _m.Session.ApproveCurrentDevice(); break;
            default: _m.LaunchScrcpy(); break;
        }
    }

    private void Cam_Click(object? s, RoutedEventArgs e) { if (_m.CameraOn) _m.StopCamera(); else _m.StartCamera(); }
    private void Lens_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_loadingCamera || Lens.SelectedItem is not CameraLens lens) return;
        _m.SelectLens(lens); _loadingCamera = true;
        Zoom.Minimum = _m.Camera.Current?.MinZoom ?? 1; Zoom.Maximum = _m.Camera.Current?.MaxZoom ?? 1; Zoom.Value = _m.Settings.CameraZoom; _loadingCamera = false;
    }
    private void CameraControls_Changed(object? s, SelectionChangedEventArgs e) => ApplyCameraControls();
    private void CameraControls_Click(object? s, RoutedEventArgs e) => ApplyCameraControls();
    private void ApplyCameraControls()
    {
        if (_loadingCamera) return;
        _m.Settings.CameraStabilization = (string?)((ComboBoxItem?)Stabilization.SelectedItem)?.Tag ?? "auto";
        _m.Settings.CameraOrientation = (string?)((ComboBoxItem?)Orientation.SelectedItem)?.Tag ?? "auto";
        _m.Settings.PreviewMirror = Mirror.IsChecked == true;
        _m.UpdateCameraControls();
    }
    private void Power_Click(object? s, RoutedEventArgs e) { _m.Settings.SavePower = SavePower.IsChecked == true; _m.Settings.Save(); if (_m.CameraOn) _m.StartCamera(); }


    private async void SetupCam_Click(object? s, RoutedEventArgs e)
    {
        // One-time, explicit, admin-approved step via polkit: load v4l2loopback with a "PixelBridge Camera" label.
        var (code, output) = await Tools.RunAsync("pkexec", "modprobe", "v4l2loopback", "devices=1", "video_nr=42", "card_label=PixelBridge Camera", "exclusive_caps=1");
        ShowToast(code == 0 ? "PixelBridge Camera is ready (/dev/video42). Restart the camera to use it."
            : $"Couldn't load v4l2loopback (install v4l2loopback-dkms first): {output.Trim()}");
        if (code == 0 && _m.CameraOn) _m.StartCamera();
    }

    private async void Mic_Click(object? s, RoutedEventArgs e) { if (_m.MicOn) await _m.StopMicAsync(); else await _m.StartMicAsync(); }
    private void Mute_Click(object? s, RoutedEventArgs e) => _m.Mic.Muted = MuteSwitch.IsChecked == true;
    private void Loc_Click(object? s, RoutedEventArgs e) { if (_m.LocationOn) _m.StopLocation(); else _m.StartLocation(); }
    private async void Ble_Click(object? s, RoutedEventArgs e) => await _m.ScanBleAsync();
    private void Scrcpy_Click(object? s, RoutedEventArgs e) => _m.LaunchScrcpy();
    private async void Pair_Click(object? s, RoutedEventArgs e) => await _m.Session.PairAsync();

    private void Save_Click(object? s, RoutedEventArgs e)
    {
        _m.Settings.TargetSerial = (SerialBox.Text ?? "").Trim();
        _m.Settings.AutoConnect = AutoConnect.IsChecked == true;
        _m.Settings.Save();
        if (Autostart.IsChecked == true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartPath)!);
            File.WriteAllText(AutostartPath, $"[Desktop Entry]\nType=Application\nName=PixelBridge\nExec=\"{Environment.ProcessPath}\" --minimized\nX-GNOME-Autostart-enabled=true\n");
        }
        else File.Delete(AutostartPath);
        ShowToast("Settings saved.");
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    /// <summary>--render-pages DIR: offscreen PNG of every page (UI review/CI evidence).</summary>
    public async Task RenderAllPagesAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(4000);
        int i = 0;
        foreach (var key in new[] { "home", "camera", "mic", "sensors", "location", "network", "bluetooth", "phone", "settings" })
        {
            await Dispatcher.UIThread.InvokeAsync(() => Navigate(key));
            await Task.Delay(1200);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
                rtb.Render(this);
                rtb.Save(Path.Combine(dir, $"{i++:00}-{key}.png"));
            });
        }
        Log.Info($"rendered pages to {dir}");
    }
}
