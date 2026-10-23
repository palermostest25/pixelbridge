using System.Windows;
using System.Windows.Controls;

namespace PixelBridge.App.Pages;

public partial class NotificationsPage : UserControl, IPage
{
    private readonly AppController _c;

    public NotificationsPage(AppController c)
    {
        _c = c;
        InitializeComponent();
        List.ItemsSource = c.Notifications;
        Enable.IsChecked = c.Settings.NotificationsEnabled;
        ContentSwitch.IsChecked = c.Settings.NotificationShowContent;
    }

    public void Tick() => Empty.Visibility = _c.Notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Enable_Click(object sender, RoutedEventArgs e) { _c.Settings.NotificationsEnabled = Enable.IsChecked == true; _c.Settings.Save(); }
    private void Content_Click(object sender, RoutedEventArgs e) { _c.Settings.NotificationShowContent = ContentSwitch.IsChecked == true; _c.Settings.Save(); }
    private void Clear_Click(object sender, RoutedEventArgs e) => _c.Notifications.Clear();

    private async void OpenAccess_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Session.Adb is null || !_c.Session.IsConnected) { MainWindow.Current?.ShowToast("Connect the phone first."); return; }
        await _c.Session.Adb.ShellAsync(_c.Session.Serial, "am start -a android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS");
        _c.LaunchScrcpy(false);
    }
}
