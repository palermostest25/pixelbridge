using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PixelBridge.Core;

namespace PixelBridge.Linux;

public partial class App : Application
{
    public AppModel Model { get; private set; } = null!;
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.Info($"PixelBridge (Linux) starting on {Environment.OSVersion}");
            Model = new AppModel();
            Model.Start();
            _window = new MainWindow(Model);
            if (!desktop.Args?.Contains("--minimized") ?? true) _window.Show();
            var ri = Array.IndexOf(desktop.Args ?? [], "--render-pages");
            if (ri >= 0 && ri + 1 < desktop.Args!.Length) _ = _window.RenderAllPagesAsync(desktop.Args[ri + 1]);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Tray_Clicked(object? sender, EventArgs e) => ShowWindow();
    private void Show_Click(object? sender, EventArgs e) => ShowWindow();
    private void Privacy_Click(object? sender, EventArgs e) => Model.SetPrivacy(!Model.Privacy);

    private async void Quit_Click(object? sender, EventArgs e)
    {
        _window?.AllowClose();
        await Model.DisposeAsync();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void ShowWindow() { _window ??= new MainWindow(Model); _window.Show(); _window.Activate(); }
}
