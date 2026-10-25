using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PixelBridge.Core;

namespace PixelBridge.App.Pages;

public partial class PhonePage : UserControl, IPage
{
    private readonly AppController _c;

    public PhonePage(AppController c)
    {
        _c = c;
        InitializeComponent();
    }

    public void Tick() =>
        AppCard.Description = $"PC expects version {DeviceSession.ExpectedAppVersion}" + (_c.Session.State == LinkState.ApkOutdated ? $" · {_c.Session.StateDetail}" : "");

    private void Mirror_Click(object sender, RoutedEventArgs e) => _c.LaunchScrcpy(false);
    private void VirtualDisplay_Click(object sender, RoutedEventArgs e) => _c.LaunchScrcpy(true);

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Session.Adb is null) return;
        var r = await _c.Session.Adb.ShellAsync(_c.Session.Serial, $"am start -n {DeviceSession.Package}/.MainActivity");
        MainWindow.Current?.ShowToast(r.Ok ? "Phone app brought to the front." : "Couldn't start the phone app: " + r.StdErr);
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallApkAsync(_c, Window.GetWindow(this));

    public static async Task InstallApkAsync(AppController c, Window? owner)
    {
        var dlg = new OpenFileDialog { Filter = "Android package (*.apk)|*.apk", Title = "Choose the PixelBridge APK" };
        var bundled = Path.Combine(AppContext.BaseDirectory, "PixelBridge.apk");
        if (File.Exists(bundled)) { dlg.InitialDirectory = AppContext.BaseDirectory; dlg.FileName = "PixelBridge.apk"; }
        if (dlg.ShowDialog(owner) != true) return;
        if (MessageBox.Show(owner!, $"Install {Path.GetFileName(dlg.FileName)} on {c.Session.Serial} only?", "Install phone app",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        MainWindow.Current?.ShowToast("Installing…");
        var r = await c.Session.InstallApkAsync(dlg.FileName);
        MainWindow.Current?.ShowToast(r is { Ok: true } ? "Phone app installed." : $"Install failed: {r?.StdErr}{r?.StdOut}");
    }

    private async void ImportKeys_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var picker = new OpenFileDialog { Title = "Choose adbkey (adbkey.pub must be beside it)", Filter = "ADB keys|adbkey;adbkey.pub|All files|*.*" };
        if (picker.ShowDialog(owner) != true) return;
        try {
            var plan = PixelBridge.Core.Adb.AdbKeyImport.Prepare(picker.FileName);
            if (_c.Session.Adb is not { } adb) throw new IOException("Install or locate ADB first.");
            if (MessageBox.Show(owner!, "Import this key pair?\nFingerprint: " + plan.Fingerprint + "\n\nCurrent ADB keys will be backed up. Restarting ADB briefly disconnects all ADB apps, including screen mirroring. The phone must already trust the imported keys, or you must approve its USB-debugging prompt.", "Import & reconnect", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var auto = _c.Settings.AutoConnect;
            await _c.Session.DisconnectAsync();
            try {
                plan.Install();
                var stopped = await adb.RunAsync("kill-server");
                if (!stopped.Ok) throw new IOException("Keys were saved, but ADB could not restart. Restart ADB manually.");
                var started = await adb.RunAsync("start-server");
                MainWindow.Current?.ShowToast(started.Ok ? "ADB keys imported. Previous keys are backed up in .android/pixelbridge-key-backups." : "Keys imported. Restart ADB manually to reconnect.");
            } finally { _c.Settings.AutoConnect = auto; if (auto) _c.Session.RetryNow(); }
        } catch (Exception error) { MainWindow.Current?.ShowToast(error.Message); }
    }

    private void Unpair_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "Remove this PC's pairing key? You'll need to pair again to reconnect.", "Unpair",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _c.Session.Unpair();
        _ = _c.Session.DisconnectAsync();
    }
}
