using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PixelBridge.Core;

namespace PixelBridge.Linux;

public partial class App : Application
{
    public AppModel Model { get; private set; } = null!;
    private MainWindow? _window;
    private CancellationTokenSource? _ringCancellation;
    private System.Diagnostics.Process? _ringProcess;
    private NativeMenuItem SharingMenu => TrayIcon.GetIcons(this)!.Single().Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "USB internet sharing");
    private NativeMenuItem PowerMenu => TrayIcon.GetIcons(this)!.Single().Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Save power (15 fps)");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.Info($"PixelBridge (Linux) starting on {Environment.OSVersion}");
            Model = new AppModel();
            Model.Phone.RingingChanged += call => Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                SilenceCallRing();
                if (call == null) return;
                ShowWindow(); _window?.Navigate("calls");
                _ringCancellation = new CancellationTokenSource();
                _ = RingAsync(_ringCancellation.Token);
            });
            Model.Session.PhoneAttached += () => { if (Model.Settings.ShowOnConnect) Avalonia.Threading.Dispatcher.UIThread.Post(ShowWindow); };
            Model.Start();
            PowerMenu.IsChecked = Model.Settings.SavePower;
            Model.UsbSharing.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { SharingMenu.IsChecked = Model.UsbSharing.Enabled; SharingMenu.IsEnabled = Model.Session.IsConnected && !Model.UsbSharing.Busy; });
            _window = new MainWindow(Model);
            if (!desktop.Args?.Contains("--minimized") ?? true) _window.Show();
            var ri = Array.IndexOf(desktop.Args ?? [], "--render-pages");
            if (ri >= 0 && ri + 1 < desktop.Args!.Length) _ = RenderAndExitAsync(desktop.Args[ri + 1]);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task RenderAndExitAsync(string dir) { await _window!.RenderAllPagesAsync(dir); await Model.DisposeAsync(); (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(); }

    private void Tray_Clicked(object? sender, EventArgs e) => ShowWindow();
    private void Show_Click(object? sender, EventArgs e) => ShowWindow();
    private async void Sharing_Click(object? sender, EventArgs e) { await Model.UsbSharing.SetAsync(!Model.UsbSharing.Enabled); if (Model.UsbSharing.Message.Length > 0) _window?.ShowToast(Model.UsbSharing.Message); }
    private void Power_Click(object? sender, EventArgs e) { Model.Settings.SavePower = !Model.Settings.SavePower; Model.Settings.Save(); PowerMenu.IsChecked = Model.Settings.SavePower; if (Model.CameraOn) Model.StartCamera(); }

    private void Privacy_Click(object? sender, EventArgs e) => Model.SetPrivacy(!Model.Privacy);

    private async void Quit_Click(object? sender, EventArgs e)
    {
        SilenceCallRing();
        _window?.AllowClose();
        await Model.DisposeAsync();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void ShowWindow() { _window ??= new MainWindow(Model); _window.Show(); _window.Activate(); }
    private void SilenceCallRing()
    {
        _ringCancellation?.Cancel(); _ringCancellation?.Dispose(); _ringCancellation = null;
        try { if (_ringProcess is { HasExited: false } process) process.Kill(); } catch { }
    }
    private async Task RingAsync(CancellationToken cancellation)
    {
        try {
            while (!cancellation.IsCancellationRequested) {
                var start = new System.Diagnostics.ProcessStartInfo("paplay") { UseShellExecute = false, RedirectStandardError = true };
                start.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "CallRing.wav"));
                using var process = new System.Diagnostics.Process { StartInfo = start };
                try {
                    _ringProcess = process; process.Start();
                    await process.WaitForExitAsync(cancellation);
                    if (process.ExitCode != 0) throw new IOException("Desktop sound output is unavailable");
                } finally { if (ReferenceEquals(_ringProcess, process)) _ringProcess = null; }
            }
        } catch (OperationCanceledException) { }
        catch { _window?.ShowToast("Incoming call. Install pulseaudio-utils / enable desktop audio to hear the ringtone."); }
    }
}
