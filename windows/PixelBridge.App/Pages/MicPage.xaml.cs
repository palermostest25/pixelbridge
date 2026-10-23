using System.Windows;
using System.Windows.Controls;
using PixelBridge.Media;

namespace PixelBridge.App.Pages;

public partial class MicPage : UserControl, IPage
{
    private readonly AppController _c;
    private bool _loading = true;

    public MicPage(AppController c)
    {
        _c = c;
        InitializeComponent();
        Gain.Value = c.Settings.MicGain;
        Jitter.Value = c.Settings.MicJitterMs;
        Mute.IsChecked = c.Audio.Muted;
        LoadDevices();
        _loading = false;
        GainText.Text = $"{Gain.Value:0.0}×";
        JitterText.Text = $"{Jitter.Value:0} ms";
    }

    private void LoadDevices()
    {
        try
        {
            var outs = AudioPipeline.PlaybackEndpoints();
            var none = new AudioEndpoint("", "Don't send");
            var cableList = new[] { none }.Concat(outs).ToList();
            CableSelect.ItemsSource = cableList;
            CableSelect.SelectedItem = cableList.FirstOrDefault(o => o.Id == _c.Settings.MicOutputDevice) ?? none;
            var monList = new[] { new AudioEndpoint("", "Off") }.Concat(outs.Where(o => !o.IsVirtualCableInput)).ToList();
            MonitorSelect.ItemsSource = monList;
            MonitorSelect.SelectedIndex = 0;
            var cable = outs.FirstOrDefault(o => o.IsVirtualCableInput);
            CableInfo.Visibility = cable is null ? Visibility.Visible : Visibility.Collapsed;
            CableInfoText.Text = "Windows needs a virtual audio cable to turn the phone into a microphone apps can pick. Install VB-CABLE (free, from vb-audio.com, needs admin), then choose 'CABLE Input' below. Until then you can listen and record here.";
        }
        catch (Exception e) { CableInfoText.Text = "Couldn't list audio devices: " + e.Message; }
    }

    public void Tick()
    {
        var a = _c.Audio;
        bool live = a.IsLive;
        MicState.Text = _c.PrivacyMode ? "Privacy mode is on" : live ? (a.Muted ? "Live, muted" : "Live") : _c.MicError ?? (_c.MicRequested ? "Starting…" : "Microphone is off");
        double level = live ? Math.Clamp((a.LevelDbfs + 60) / 60, 0, 1) : 0;
        if (LevelFill.Parent is FrameworkElement track) LevelFill.Width = track.ActualWidth * level;
        LevelText.Text = live ? $"{a.LevelDbfs:0} dBFS  peak {a.PeakDbfs:0}" : "";
        StartStopText.Text = _c.MicRequested ? "Stop microphone" : "Start microphone";
        StartStopGlyph.Text = _c.MicRequested ? "" : "";
        StartStop.IsEnabled = _c.Session.IsConnected && !_c.PrivacyMode;
        RecordButton.Content = a.IsRecording ? "Stop" : "Record";
    }

    private void StartStop_Click(object sender, RoutedEventArgs e) { if (_c.MicRequested) _c.StopMic(); else _c.StartMic(); }

    private void Mute_Click(object sender, RoutedEventArgs e) { _c.Audio.Muted = Mute.IsChecked == true; ((App)Application.Current).UpdateTray(); }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainText != null) GainText.Text = $"{Gain.Value:0.0}×";
        if (_loading) return;
        _c.Audio.Gain = (float)Gain.Value; _c.Settings.MicGain = (float)Gain.Value; _c.Settings.Save();
    }

    private void Jitter_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (JitterText != null) JitterText.Text = $"{Jitter.Value:0} ms";
        if (_loading) return;
        _c.Audio.JitterMs = (int)Jitter.Value; _c.Settings.MicJitterMs = (int)Jitter.Value; _c.Settings.Save();
    }

    private void Cable_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CableSelect.SelectedItem is not AudioEndpoint ep) return;
        var id = ep.Id.Length == 0 ? null : ep.Id;
        _c.Audio.SetCableOutput(id);
        _c.Settings.MicOutputDevice = id; _c.Settings.Save();
    }

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MonitorSelect.SelectedItem is not AudioEndpoint ep) return;
        _c.Audio.SetMonitorOutput(ep.Id.Length == 0 ? null : ep.Id);
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Audio.IsRecording) { _c.Audio.StopRecording(); MainWindow.Current?.ShowToast($"Saved {_c.Audio.RecordingPath}"); return; }
        if (!_c.Audio.IsLive) { MainWindow.Current?.ShowToast("Start the microphone first."); return; }
        MainWindow.Current?.ShowToast($"Recording up to 60 s to {_c.Audio.StartRecording(60)}");
    }
}
