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

    private void Unpair_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "Remove this PC's pairing key? You'll need to pair again to reconnect.", "Unpair",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _c.Session.Unpair();
        _ = _c.Session.DisconnectAsync();
    }
}
