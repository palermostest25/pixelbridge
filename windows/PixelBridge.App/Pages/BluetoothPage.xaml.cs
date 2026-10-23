using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PixelBridge.App.Pages;

public partial class BluetoothPage : UserControl, IPage
{
    private readonly AppController _c;
    private BleDevice? _selected;

    public BluetoothPage(AppController c)
    {
        _c = c;
        InitializeComponent();
        DeviceList.ItemsSource = c.Radio.Devices;
        BondedList.ItemsSource = c.Radio.Bonded;
        Loaded += async (_, _) => { if (_c.Session.IsConnected && _c.Radio.Bonded.Count == 0) await _c.Radio.LoadBondedAsync(); };
    }

    public void Tick()
    {
        var r = _c.Radio;
        bool on = _c.Session.IsConnected;
        ScanButton.IsEnabled = on && !_c.PrivacyMode;
        ScanText.Text = r.Scanning ? "Stop" : "Scan nearby";
        ScanProgress.Visibility = r.Scanning ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = r.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = r.Scanning ? "Looking for devices…" : "Press Scan nearby to look for Bluetooth LE devices.";
        NearbyTitle.Text = r.Devices.Count > 0 ? $"Nearby · {r.Devices.Count}" : "Nearby";

        var perms = r.Caps?["permissions"];
        string? banner = !on ? "Connect the phone to use its Bluetooth."
            : r.Caps != null && (bool?)r.Caps["bluetoothOn"] == false ? "Bluetooth is off on the phone."
            : perms != null && (bool?)perms["bluetoothConnect"] == false ? "Allow 'Nearby devices' for PixelBridge on the phone (Phone screen → Allow Bluetooth)."
            : r.Status.Length > 0 && !r.Scanning ? r.Status : null;
        Banner.Visibility = banner is null ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = banner ?? "";

        if (_selected is { } d)
        {
            DetailName.Text = d.Title;
            DetailInfo.Text = $"{d.Address} · {d.Detail} · {StateText(d.State)}";
            ConnectButton.Content = d.State is "connected" or "ready" or "connecting" ? "Disconnect" : "Connect";
            GattTitle.Visibility = d.Characteristics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static string StateText(string s) => s switch
    {
        "ready" => "connected", "connecting" => "connecting…", "connected" => "discovering services…", "failed" => "couldn't connect", "disconnected" => "disconnected", _ => "not connected",
    };

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Radio.Scanning) { await _c.Radio.StopBleAsync(); return; }
        _c.Radio.ClearScan();
        await _c.Radio.ScanBleAsync(15);
        if (_c.Radio.Caps is null) await _c.Radio.RefreshAsync();
    }

    private void Device_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is not BleDevice d) return;
        (lb == DeviceList ? BondedList : DeviceList).SelectedIndex = -1;
        _selected = d;
        NoSelection.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;
        CharList.ItemsSource = d.Characteristics;
        Tick();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } d) return;
        if (d.State is "connected" or "ready" or "connecting") await _c.Radio.DisconnectAsync(d);
        else
        {
            if (_c.Radio.Scanning) await _c.Radio.StopBleAsync(); // connecting while scanning is slow on Android
            await _c.Radio.ConnectAsync(d);
        }
    }

    private static GattCharacteristic? CharOf(object sender) => (sender as FrameworkElement)?.DataContext as GattCharacteristic;

    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } d && CharOf(sender) is { } ch) await _c.Radio.ReadAsync(d, ch);
    }

    private async void Notify_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } d && CharOf(sender) is { } ch) { await _c.Radio.NotifyAsync(d, ch, true); MainWindow.Current?.ShowToast("Notifications enabled for " + ch.ShortUuid); }
    }

    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } d || CharOf(sender) is not { } ch) return;
        var box = FindSibling<TextBox>((FrameworkElement)sender);
        var hex = new string((box?.Text ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length == 0 || hex.Length % 2 != 0) { MainWindow.Current?.ShowToast("Enter hex bytes, e.g. 01 FF"); return; }
        await _c.Radio.WriteAsync(d, ch, Convert.FromHexString(hex));
        MainWindow.Current?.ShowToast($"Wrote {hex.Length / 2} byte(s) to {ch.ShortUuid}");
    }

    private static T? FindSibling<T>(FrameworkElement e) where T : DependencyObject
    {
        if (VisualTreeHelper.GetParent(e) is not Panel p) return null;
        return p.Children.OfType<T>().FirstOrDefault();
    }
}
