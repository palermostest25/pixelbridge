using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PixelBridge.Core;

namespace PixelBridge.App.Pages;

public partial class SensorsPage : UserControl, IPage
{
    private readonly AppController _c;
    private readonly ConcurrentQueue<SensorSample> _queue = new();
    private readonly QuaternionRotation3D _rotation = new();
    private readonly DispatcherTimer _plotTimer;
    private bool _loading = true;

    public SensorsPage(AppController c)
    {
        _c = c;
        InitializeComponent();
        BuildPhone();
        Rate.SelectedIndex = c.Settings.SensorRateHz <= 5 ? 0 : c.Settings.SensorRateHz >= 100 ? 2 : 1;
        Osc.IsChecked = c.Settings.OscEnabled;
        ApiUrl.Text = $"ws://127.0.0.1:{c.Settings.SensorWebSocketPort}/sensors?token={c.Settings.ApiToken}";
        c.SensorReceived += s => { if (_queue.Count < 4000) _queue.Enqueue(s); };
        _plotTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Drain(), Dispatcher);
        Loaded += (_, _) => _plotTimer.Start();
        Unloaded += (_, _) => { _plotTimer.Stop(); _queue.Clear(); };
        _loading = false;
    }

    private void Drain()
    {
        SensorSample? rot = null;
        bool any = false;
        while (_queue.TryDequeue(out var s))
        {
            any = true;
            switch (s.Type)
            {
                case SensorTypes.Accelerometer: AccelPlot.Add(s.Values); break;
                case SensorTypes.Gyroscope: GyroPlot.Add(s.Values); break;
                case SensorTypes.MagneticField: MagPlot.Add(s.Values); break;
                case SensorTypes.RotationVector: rot = s; break;
            }
        }
        if (!any) return;
        AccelPlot.InvalidateVisual(); GyroPlot.InvalidateVisual(); MagPlot.InvalidateVisual();
        if (rot is { Values.Length: >= 3 } r)
        {
            double x = r.Values[0], y = r.Values[1], z = r.Values[2];
            double w = r.Values.Length >= 4 ? r.Values[3] : Math.Sqrt(Math.Max(0, 1 - x * x - y * y - z * z));
            _rotation.Quaternion = new Quaternion(x, y, z, w);
            double pitch = Math.Atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y)) * 180 / Math.PI;
            double roll = Math.Asin(Math.Clamp(2 * (w * y - z * x), -1, 1)) * 180 / Math.PI;
            double yaw = Math.Atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z)) * 180 / Math.PI;
            EulerText.Text = $"yaw {yaw:0}°  ·  pitch {pitch:0}°  ·  roll {roll:0}°";
        }
    }

    public void Tick()
    {
        var rows = new List<object>();
        lock (_c.LatestSensors)
            foreach (var (t, s) in _c.LatestSensors.OrderBy(k => k.Key))
                rows.Add(new { Name = Pretty(t), Value = $"{string.Join("  ", s.Values.Take(4).Select(v => v.ToString("0.00").PadLeft(7)))} {SensorTypes.Unit(t)}" });
        SensorList.ItemsSource = rows;
        ExportButton.Content = _c.SensorExportActive ? "Stop" : "Export";
        if (!_c.Tilt.Enabled && Tilt.IsChecked == true) Tilt.IsChecked = false;
    }

    private static string Pretty(int t) => t switch
    {
        SensorTypes.Accelerometer => "Accelerometer", SensorTypes.Gyroscope => "Gyroscope", SensorTypes.MagneticField => "Magnetometer",
        SensorTypes.Gravity => "Gravity", SensorTypes.LinearAcceleration => "Linear acceleration", SensorTypes.RotationVector => "Rotation vector",
        SensorTypes.GameRotationVector => "Game rotation vector", SensorTypes.Light => "Ambient light", SensorTypes.Pressure => "Barometer",
        SensorTypes.Proximity => "Proximity", _ => SensorTypes.Name(t),
    };

    private void BuildPhone()
    {
        // 0.72 × 1.5 × 0.08 box in Android device axes (X right, Y up the screen, Z out of the screen).
        double hx = 0.36, hy = 0.75, hz = 0.045;
        var screen = new MeshGeometry3D();
        var body = new MeshGeometry3D();
        void Quad(MeshGeometry3D m, Point3D a, Point3D b, Point3D c, Point3D d)
        {
            int i = m.Positions.Count;
            foreach (var p in new[] { a, b, c, d }) m.Positions.Add(p);
            foreach (var k in new[] { 0, 1, 2, 0, 2, 3 }) m.TriangleIndices.Add(i + k);
        }
        Quad(screen, new(-hx + .03, -hy + .03, hz + .001), new(hx - .03, -hy + .03, hz + .001), new(hx - .03, hy - .03, hz + .001), new(-hx + .03, hy - .03, hz + .001));
        Quad(body, new(-hx, -hy, hz), new(hx, -hy, hz), new(hx, hy, hz), new(-hx, hy, hz));
        Quad(body, new(hx, -hy, -hz), new(-hx, -hy, -hz), new(-hx, hy, -hz), new(hx, hy, -hz));
        Quad(body, new(-hx, hy, hz), new(hx, hy, hz), new(hx, hy, -hz), new(-hx, hy, -hz));
        Quad(body, new(-hx, -hy, -hz), new(hx, -hy, -hz), new(hx, -hy, hz), new(-hx, -hy, hz));
        Quad(body, new(hx, -hy, hz), new(hx, -hy, -hz), new(hx, hy, -hz), new(hx, hy, hz));
        Quad(body, new(-hx, -hy, -hz), new(-hx, -hy, hz), new(-hx, hy, hz), new(-hx, hy, -hz));
        // Camera bar on the back (Pixel 7 visor) so front and back are distinguishable.
        Quad(body, new(-hx, hy - .38, -hz - .03), new(hx, hy - .38, -hz - .03), new(hx, hy - .22, -hz - .03), new(-hx, hy - .22, -hz - .03));
        var accent = (SystemParameters.WindowGlassBrush as SolidColorBrush)?.Color ?? Color.FromRgb(0x3E, 0x7B, 0xFA);
        var phone = new Model3DGroup();
        phone.Children.Add(new GeometryModel3D(body, new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE2)))) { BackMaterial = new DiffuseMaterial(Brushes.Gray) });
        phone.Children.Add(new GeometryModel3D(screen, new DiffuseMaterial(new LinearGradientBrush(Color.FromRgb(0x10, 0x12, 0x18), Color.FromArgb(0xFF, (byte)(accent.R / 3), (byte)(accent.G / 3), (byte)(accent.B / 3)), 90))));
        phone.Transform = new RotateTransform3D(_rotation);
        Scene.Children.Add(phone);
    }

    private void Rate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Rate.SelectedItem is not ComboBoxItem i) return;
        _c.SetSensorRate(int.Parse((string)i.Tag));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_c.SensorExportActive) { _c.StopSensorExport(); MainWindow.Current?.ShowToast("Export saved."); return; }
        MainWindow.Current?.ShowToast($"Exporting 60 s to {_c.StartSensorExport(60)}");
    }

    private void Tilt_Click(object sender, RoutedEventArgs e)
    {
        if (Tilt.IsChecked == true && MessageBox.Show(Window.GetWindow(this), "Let the phone's gyroscope move your mouse pointer?\n\nPress Ctrl+Alt+P at any time to stop. It also stops on disconnect and in privacy mode.",
                "Tilt to move the mouse", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        { Tilt.IsChecked = false; return; }
        _c.Tilt.Enabled = Tilt.IsChecked == true;
    }

    private void Osc_Click(object sender, RoutedEventArgs e) { _c.Settings.OscEnabled = Osc.IsChecked == true; _c.Settings.Save(); }

    private void CopyApi_Click(object sender, RoutedEventArgs e) { Clipboard.SetText(ApiUrl.Text); MainWindow.Current?.ShowToast("API URL copied."); }
}
