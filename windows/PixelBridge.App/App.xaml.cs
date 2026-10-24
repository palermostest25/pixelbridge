using System.Drawing;
using System.Windows;
using PixelBridge.Core;
using Forms = System.Windows.Forms;

namespace PixelBridge.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayStatus, _trayPrivacy, _trayMute, _traySharing, _trayPower;
    public AppController Controller { get; private set; } = null!;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\PixelBridge.App.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("PixelBridge is already running (see the tray icon).", "PixelBridge");
            Shutdown();
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log.Error($"UI exception: {ex.Exception}"); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error($"fatal: {ex.ExceptionObject}");
        Log.Info($"PixelBridge {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");

        Controller = new AppController();
        Controller.Session.PhoneAttached += () => { if (Controller.Settings.ShowOnConnect) Dispatcher.BeginInvoke(ShowWindow); };
        Controller.Start();
        CreateTray();
        _window = new MainWindow(Controller);
        if (!e.Args.Contains("--minimized")) _window.Show();
        var ri = Array.IndexOf(e.Args, "--render-pages");
        if (ri >= 0 && ri + 1 < e.Args.Length)
        {
            var dir = e.Args[ri + 1];
            // Not tied to Loaded: WPF skips its render pass (and Loaded) while the desktop is locked.
            Dispatcher.BeginInvoke(async () => { await Task.Delay(5000); await _window.RenderAllPagesAsync(dir); await QuitAsync(); });
        }
        Controller.Session.StateChanged += (s, _) => Dispatcher.BeginInvoke(UpdateTray);
        Controller.Toast += msg => Dispatcher.BeginInvoke(() => _tray?.ShowBalloonTip(4000, "PixelBridge", msg, Forms.ToolTipIcon.Info));
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        _trayStatus = new Forms.ToolStripMenuItem("Starting…") { Enabled = false };
        menu.Items.Add(_trayStatus);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Show dashboard", null, (_, _) => ShowWindow());
        menu.Items.Add("Connect", null, (_, _) => { Controller.Session.RetryNow(); });
        menu.Items.Add("Disconnect", null, async (_, _) => await Controller.Session.DisconnectAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Start camera", null, (_, _) => Controller.StartCamera());
        menu.Items.Add("Stop camera", null, (_, _) => Controller.StopCamera());
        _trayMute = new Forms.ToolStripMenuItem("Mic mute", null, (_, _) => { Controller.Audio.Muted = !Controller.Audio.Muted; UpdateTray(); });
        menu.Items.Add(_trayMute);
        _traySharing = new Forms.ToolStripMenuItem("USB internet sharing", null, async (_, _) => { await Controller.UsbSharing.SetAsync(!Controller.UsbSharing.Enabled); UpdateTray(); });
        menu.Items.Add(_traySharing);
        _trayPower = new Forms.ToolStripMenuItem("Save power (15 fps)", null, (_, _) => { Controller.Settings.SavePower = !Controller.Settings.SavePower; Controller.Settings.Save(); if (Controller.CameraRequested) Controller.StartCamera(); UpdateTray(); });
        menu.Items.Add(_trayPower);
        menu.Opening += async (_, _) => { await Controller.UsbSharing.RefreshAsync(); UpdateTray(); };
        Controller.UsbSharing.Changed += () => Dispatcher.BeginInvoke(UpdateTray);
        _trayPrivacy = new Forms.ToolStripMenuItem("Privacy mode (pause all)", null, (_, _) => { Controller.SetPrivacy(!Controller.PrivacyMode); UpdateTray(); });
        menu.Items.Add(_trayPrivacy);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, async (_, _) => await QuitAsync());
        _tray = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "PixelBridge",
            Visible = true,
            ContextMenuStrip = menu,
        };
        try { _tray.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; } catch { }
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    public void UpdateTray()
    {
        if (_tray is null) return;
        var s = Controller.Session;
        var text = s.State == LinkState.Connected ? $"Connected — {s.DeviceProps.GetValueOrDefault("ro.product.model", "Pixel")}" : $"{s.State}";
        _trayStatus!.Text = text;
        _tray.Text = ("PixelBridge: " + text)[..Math.Min(63, ("PixelBridge: " + text).Length)];
        _trayPrivacy!.Checked = Controller.PrivacyMode;
        _trayMute!.Checked = Controller.Audio.Muted;
        _traySharing!.Checked = Controller.UsbSharing.Enabled;
        _traySharing.Enabled = Controller.Session.IsConnected && !Controller.UsbSharing.Busy;
        _traySharing.ToolTipText = Controller.UsbSharing.Message;
        _trayPower!.Checked = Controller.Settings.SavePower;
    }

    public void ShowWindow()
    {
        _window ??= new MainWindow(Controller);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public async Task QuitAsync()
    {
        Log.Info("quitting");
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _window?.AllowClose();
        _window?.Close();
        try { await Controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) { Log.Warn($"shutdown: {e.Message}"); }
        Shutdown();
    }
}
