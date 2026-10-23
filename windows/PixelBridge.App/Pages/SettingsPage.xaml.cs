using System.Windows;
using System.Windows.Controls;
using PixelBridge.Core;

namespace PixelBridge.App.Pages;

public partial class SettingsPage : UserControl, IPage
{
    private readonly AppController _c;
    private bool _loading = true;

    public SettingsPage(AppController c)
    {
        _c = c;
        InitializeComponent();
        var s = c.Settings;
        Startup.IsChecked = AppController.IsStartAtLoginEnabled();
        AutoConnect.IsChecked = s.AutoConnect;
        Vcam.IsChecked = s.VirtualCameraEnabled;
        Api.IsChecked = s.SensorWebSocketEnabled;
        TempWarn.SelectedIndex = s.BatteryTempWarnC <= 38 ? 0 : s.BatteryTempWarnC >= 42 ? 2 : 1;
        Serial.Text = s.TargetSerial;
        AdbPath.Text = s.AdbPath ?? "";
        ScrcpyPath.Text = s.ScrcpyPath ?? "";
        About.Text = $"PixelBridge {typeof(App).Assembly.GetName().Version?.ToString(3)} · phone app {DeviceSession.ExpectedAppVersion} · settings in {AppPaths.DataDir}";
        _loading = false;
    }

    public void Tick() { }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _c.Settings;
        bool vcamBefore = s.VirtualCameraEnabled;
        s.AutoConnect = AutoConnect.IsChecked == true;
        s.VirtualCameraEnabled = Vcam.IsChecked == true;
        s.SensorWebSocketEnabled = Api.IsChecked == true;
        s.TargetSerial = Serial.Text.Trim().Length > 0 ? Serial.Text.Trim() : s.TargetSerial;
        s.AdbPath = AdbPath.Text.Trim() is { Length: > 0 } a ? a : null;
        s.ScrcpyPath = ScrcpyPath.Text.Trim() is { Length: > 0 } p ? p : null;
        s.Save();
        if (sender == Startup) AppController.SetStartAtLogin(Startup.IsChecked == true);
        if (vcamBefore != s.VirtualCameraEnabled) { if (s.VirtualCameraEnabled) _c.StartVirtualCamera(); else _c.StopVirtualCamera(); }
    }

    private void TempChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _c.Settings.BatteryTempWarnC = TempWarn.SelectedIndex switch { 0 => 38, 2 => 42, _ => 40 };
        _c.Settings.Save();
    }
}
