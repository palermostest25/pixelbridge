using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PixelBridge.Core;

namespace PixelBridge.App.Pages;

public partial class DiagnosticsPage : UserControl, IPage
{
    private readonly AppController _c;

    public DiagnosticsPage(AppController c)
    {
        _c = c;
        InitializeComponent();
        LogBox.Text = string.Join(Environment.NewLine, Log.Snapshot().TakeLast(500));
        Log.Line += l => Dispatcher.BeginInvoke(() =>
        {
            if (LogBox.LineCount > 2000) LogBox.Clear();
            LogBox.AppendText(Environment.NewLine + l);
            LogBox.ScrollToEnd();
        });
        Loaded += (_, _) => LogBox.ScrollToEnd();
    }

    public void Tick() { }

    private async void Doctor_Click(object sender, RoutedEventArgs e)
    {
        DoctorCard.Visibility = Visibility.Visible;
        DoctorText.Text = "Running checks…";
        DoctorText.Text = await Doctor.RunAsync(_c);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.RecordingsDir);
        var zip = Path.Combine(AppPaths.RecordingsDir, $"pixelbridge-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var report = await Doctor.RunAsync(_c);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, string text) { using var st = archive.CreateEntry(name).Open(); st.Write(Encoding.UTF8.GetBytes(text)); }
            Add("doctor.txt", report);
            Add("recent-log.txt", string.Join(Environment.NewLine, Log.Snapshot()));
            var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(_c.Settings))!.AsObject();
            node.Remove("ApiToken"); // never export secrets
            Add("settings.json", node.ToJsonString());
        }
        MainWindow.Current?.ShowToast($"Saved {zip}");
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogDir));
    }
}
