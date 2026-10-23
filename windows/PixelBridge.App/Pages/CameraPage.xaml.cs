using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PixelBridge.Core;
using PixelBridge.Media;

namespace PixelBridge.App.Pages;

public partial class CameraPage : UserControl, IPage
{
    private readonly AppController _c;
    private WriteableBitmap? _bitmap;
    private byte[] _bgra = [];
    private int _busy;
    private volatile bool _visible;
    private bool _loading = true;

    public CameraPage(AppController c)
    {
        _c = c;
        InitializeComponent();
        (c.Settings.CameraId == "1" ? FrontRadio : RearRadio).IsChecked = true;
        Resolution.SelectedIndex = c.Settings.VideoWidth >= 1920 ? 1 : 0;
        Bitrate.SelectedIndex = c.Settings.VideoBitrate <= 2_000_000 ? 0 : c.Settings.VideoBitrate >= 8_000_000 ? 2 : 1;
        Mirror.IsChecked = c.Settings.PreviewMirror;
        c.PreviewFrame += OnFrame;
        _loading = false;
    }

    private void OnFrame(Nv12Frame f)
    {
        if (!_visible || Interlocked.Exchange(ref _busy, 1) == 1) return; // drop when the UI is behind
        int stride = f.Width * 4;
        if (_bgra.Length != stride * f.Height) _bgra = new byte[stride * f.Height];
        f.ToBgra(_bgra, stride, _c.Settings.PreviewMirror);
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_bitmap == null || _bitmap.PixelWidth != f.Width || _bitmap.PixelHeight != f.Height)
                {
                    _bitmap = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
                    Preview.Source = _bitmap;
                }
                _bitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), _bgra, stride, 0);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }, DispatcherPriority.Render);
    }

    public void Tick()
    {
        _visible = MainWindow.Current?.IsPageVisible(this) == true;
        var v = _c.Video;
        bool live = v.IsLive;
        if (!live && _bitmap != null) { Preview.Source = null; _bitmap = null; } // never leave a stale frame on screen
        Placeholder.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        LiveChip.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        LiveText.Text = $"LIVE  ·  {v.Fps:0} fps  ·  {v.Config?["facing"]}";
        (PlaceholderGlyph.Text, PlaceholderText.Text) =
            _c.PrivacyMode ? ("", "Privacy mode is on")
            : !_c.Session.IsConnected ? ("", "Phone not connected")
            : v.Paused ? ("", "Video paused")
            : _c.CameraError is { } err ? ("", err)
            : _c.CameraRequested ? ("", "Starting camera…")
            : ("", "Camera is off");
        StartStopText.Text = _c.CameraRequested ? "Stop camera" : "Start camera";
        StartStopGlyph.Text = _c.CameraRequested ? "" : "";
        StartStop.IsEnabled = _c.Session.IsConnected && !_c.PrivacyMode;
        RecordText.Text = _c.VideoRecordingActive ? "Stop recording" : "Record 60 s";
        Stats.Text = v.Config is null || !live ? "" :
            $"{v.Config["width"]}×{v.Config["height"]} H.264   {v.BitrateMbps:0.0} Mbps   latency {v.LatencyP50Ms:0} ms (p95 {v.LatencyP95Ms:0})   decoded {v.FramesDecoded}   dropped {v.FramesDropped}";
        VcamState.Text = _c.VirtualCameraActive
            ? (v.Feed.ConsumerActive ? "In use by an app" : "Ready as “PixelBridge Camera”")
            : "Not available";
        VcamDetail.Text = _c.VirtualCameraActive
            ? "Choose PixelBridge Camera in Zoom, Teams, Discord, OBS or your browser."
            : _c.VirtualCameraState;
    }

    private void StartStop_Click(object sender, RoutedEventArgs e) { if (_c.CameraRequested) _c.StopCamera(); else _c.StartCamera(); }

    private void Camera_Click(object sender, RoutedEventArgs e) => _c.SwitchCamera((string)((FrameworkElement)sender).Tag);

    private void Resolution_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Resolution.SelectedItem is not ComboBoxItem i) return;
        var p = ((string)i.Tag).Split('x');
        _c.Settings.VideoWidth = int.Parse(p[0]); _c.Settings.VideoHeight = int.Parse(p[1]); _c.Settings.Save();
        if (_c.CameraRequested) _c.StartCamera();
    }

    private void Bitrate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Bitrate.SelectedItem is not ComboBoxItem i) return;
        _c.Settings.VideoBitrate = int.Parse((string)i.Tag); _c.Settings.Save();
        if (_c.CameraRequested) _c.StartCamera();
    }

    private void Mirror_Click(object sender, RoutedEventArgs e) { _c.Settings.PreviewMirror = Mirror.IsChecked == true; _c.Settings.Save(); }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _c.Video.Paused = Pause.IsChecked == true;
        if (_c.Video.Paused) _c.Video.GoOffline(paused: true);
    }

    private void OpenCamera_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true });

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        var f = _c.Video.Latest;
        if (f is null) { MainWindow.Current?.ShowToast("Start the camera first."); return; }
        Directory.CreateDirectory(AppPaths.RecordingsDir);
        var path = Path.Combine(AppPaths.RecordingsDir, $"pixel-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        var bgra = new byte[f.Width * f.Height * 4];
        f.ToBgra(bgra, f.Width * 4, false);
        using (var fs = File.Create(path))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null, bgra, f.Width * 4)));
            enc.Save(fs);
        }
        MainWindow.Current?.ShowToast($"Saved to {path}");
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_c.VideoRecordingActive) { _c.StopVideoRecording(); MainWindow.Current?.ShowToast("Recording saved."); return; }
        if (!_c.Video.IsLive) { MainWindow.Current?.ShowToast("Start the camera first."); return; }
        MainWindow.Current?.ShowToast($"Recording up to 60 s to {_c.StartVideoRecording(60)}");
    }
}
