using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PixelBridge.App.Pages;
using PixelBridge.Core;

namespace PixelBridge.App;

/// <summary>A page hosted in the shell; Tick runs at 4 Hz while the page is visible.</summary>
public interface IPage
{
    void Tick();
}

public partial class MainWindow : Window
{
    private sealed record NavEntry(string Key, string Glyph, string Label, Func<AppController, FrameworkElement> Create);

    private static readonly NavEntry[] MainItems =
    [
        new("home", "", "Home", c => new HomePage(c)),
        new("camera", "", "Camera", c => new CameraPage(c)),
        new("mic", "", "Microphone", c => new MicPage(c)),
        new("sensors", "", "Motion sensors", c => new SensorsPage(c)),
        new("location", "\uE81D", "Location", c => new LocationPage(c)),
        new("network", "", "Internet sharing", c => new NetworkPage(c)),
        new("bluetooth", "", "Bluetooth", c => new BluetoothPage(c)),
        new("notifications", "", "Notifications", c => new NotificationsPage(c)),
        new("phone", "", "Phone screen", c => new PhonePage(c)),
    ];

    private static readonly NavEntry[] FooterItems =
    [
        new("diagnostics", "", "Diagnostics", c => new DiagnosticsPage(c)),
        new("settings", "", "Settings", c => new SettingsPage(c)),
    ];

    private readonly AppController _c;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly DispatcherTimer _tick, _toastTimer;
    private bool _allowClose, _switching;

    public static MainWindow? Current { get; private set; }

    public MainWindow(AppController controller)
    {
        _c = controller;
        Current = this;
        InitializeComponent();
        Nav.ItemsSource = MainItems.Select(MakeNavContent).ToList();
        FooterNav.ItemsSource = FooterItems.Select(MakeNavContent).ToList();
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer!.Stop(); }, Dispatcher);
        _c.Toast += msg => Dispatcher.BeginInvoke(() => ShowToast(msg));
        Nav.SelectedIndex = 0;
        _tick.Start();
        SourceInitialized += (_, _) => RegisterSafetyHotkey();
    }

    private static FrameworkElement MakeNavContent(NavEntry e)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Tag = e };
        sp.Children.Add(new TextBlock { Text = e.Glyph, Style = (Style)Application.Current.FindResource("Glyph"), FontSize = 16, Width = 20 });
        sp.Children.Add(new TextBlock { Text = e.Label, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || sender is not ListBox lb || lb.SelectedItem is not FrameworkElement { Tag: NavEntry entry }) return;
        _switching = true;
        (lb == Nav ? FooterNav : Nav).SelectedIndex = -1;
        _switching = false;
        Navigate(entry);
    }

    private void Navigate(NavEntry entry)
    {
        if (!_pages.TryGetValue(entry.Key, out var page)) _pages[entry.Key] = page = entry.Create(_c);
        PageHost.Content = page;
        PageScroller.ScrollToTop();
        (page as IPage)?.Tick();
    }

    public void NavigateTo(string key)
    {
        var all = MainItems.Concat(FooterItems).ToList();
        int i = Array.FindIndex(MainItems, n => n.Key == key);
        if (i >= 0) Nav.SelectedIndex = i;
        else FooterNav.SelectedIndex = Array.FindIndex(FooterItems, n => n.Key == key);
    }

    public bool IsPageVisible(FrameworkElement page) => IsVisible && WindowState != WindowState.Minimized && PageHost.Content == page;

    private void Tick()
    {
        var s = _c.Session;
        bool connected = s.IsConnected;
        NavStatusDot.Fill = connected ? (Brush)FindResource("SystemFillColorSuccessBrush")
            : s.State == LinkState.Connecting ? (Brush)FindResource("SystemFillColorCautionBrush") : (Brush)FindResource("SystemFillColorNeutralBrush");
        NavStatusText.Text = _c.PrivacyMode ? "Privacy mode" : connected ? $"{s.DeviceProps.GetValueOrDefault("ro.product.model", "Pixel")} connected" : StateLabel(s.State);
        if (IsVisible && WindowState != WindowState.Minimized) (PageHost.Content as IPage)?.Tick();
    }

    public static string StateLabel(LinkState s) => s switch
    {
        LinkState.Connected => "Connected", LinkState.Disconnected => "Not connected", LinkState.Connecting => "Connecting…",
        LinkState.NotPaired => "Not paired", LinkState.ApkMissing => "Phone app missing", LinkState.ApkOutdated => "Phone app outdated",
        LinkState.Unauthorized => "USB debugging not allowed", LinkState.NoAdb => "adb not found", LinkState.WrongDevice => "Unknown phone",
        LinkState.Offline => "Phone offline", _ => "Error",
    };

    public void ShowToast(string msg)
    {
        ToastText.Text = msg;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>
    /// Test/diagnostic aid (--render-pages DIR): renders every page offscreen to PNG with RenderTargetBitmap,
    /// so UI can be reviewed even when the desktop is locked or no display is attached.
    /// </summary>
    public async Task RenderAllPagesAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var root = (FrameworkElement)Content;
        var bg = (Brush)FindResource("SolidBackgroundFillColorBaseBrush");
        foreach (var (entry, i) in MainItems.Concat(FooterItems).Select((e, i) => (e, i)))
        {
            NavigateTo(entry.Key);
            for (int k = 0; k < 8; k++) { (PageHost.Content as IPage)?.Tick(); await Task.Delay(250); }
            var size = new Size(Width, Height - 32);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            double scale = 1.25;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(bg, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"{i:00}-{entry.Key}.png"));
            enc.Save(fs);
        }
        Log.Info($"rendered {MainItems.Length + FooterItems.Length} pages to {dir}");
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true; // closing the window keeps the tray agent running
            Hide();
            ((App)Application.Current).UpdateTray();
        }
        base.OnClosing(e);
    }

    // Ctrl+Alt+P: global safety hotkey that stops tilt-to-mouse.
    private const int HotkeyId = 0x5042;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hWnd, int id, uint mods, uint vk);

    private void RegisterSafetyHotkey()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (!RegisterHotKey(h, HotkeyId, 0x0001 | 0x0002, 0x50)) Log.Warn("Ctrl+Alt+P hotkey unavailable");
        HwndSource.FromHwnd(h)?.AddHook((nint hwnd, int msg, nint wp, nint lp, ref bool handled) =>
        {
            if (msg == 0x0312 && wp == HotkeyId) { _c.Tilt.Enabled = false; ShowToast("Tilt-to-mouse stopped (Ctrl+Alt+P)."); handled = true; }
            return 0;
        });
    }
}
