using LanMessenger.Core.Persistence;
using LanMessenger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI.Settings;

public sealed partial class SettingsPage : Page
{
    public AppModel? Model { get; set; }

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var cfg = ConfigStore.Shared.Config;
        UsernameBox.Text  = cfg.Username;
        InboxBox.Text     = ConfigStore.Shared.InboxDirectory;
        UpdateUrlBox.Text = cfg.UpdateServerURL;
        VersionText.Text  = $"LAN Messenger v{UpdateService.Shared.CurrentVersion}";
    }

    private void UsernameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = UsernameBox.Text.Trim();
        if (v.Length == 0) { UsernameBox.Text = ConfigStore.Shared.Config.Username; return; }
        ConfigStore.Shared.Config.Username = v;
        ConfigStore.Shared.Save();
    }

    private void UpdateUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ConfigStore.Shared.Config.UpdateServerURL = UpdateUrlBox.Text.Trim();
        ConfigStore.Shared.Save();
    }

    private async void BrowseInbox_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        ConfigStore.Shared.Config.InboxDir = folder.Path;
        ConfigStore.Shared.Save();
        InboxBox.Text = folder.Path;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Checking…";
        var url = ConfigStore.Shared.Config.UpdateServerURL;
        var latest = await UpdateService.Shared.CheckForUpdateAsync(url);
        UpdateStatusText.Text = latest is null
            ? "You're up to date."
            : $"Update available: v{latest}";
    }
}
