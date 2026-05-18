using LanMessenger.Core.Persistence;
using LanMessenger.Core.Services;
using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace LanMessenger.UI.Settings;

public sealed partial class SettingsPage : Page
{
    private AppModel? _model;
    public AppModel? Model
    {
        get => _model;
        set
        {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = value;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
            RefreshUpdateUI();
        }
    }

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var cfg = ConfigStore.Shared.Config;
        UsernameBox.Text   = cfg.Username;
        InboxBox.Text      = ConfigStore.Shared.InboxDirectory;
        UpdateRepoBox.Text = cfg.UpdateRepo;
        CloseToTrayToggle.IsOn = cfg.CloseToTray;
        VersionText.Text   = $"LAN Messenger v{UpdateService.Shared.CurrentVersion}";
        RefreshUpdateUI();
    }

    private void OnModelChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.AvailableUpdate) or nameof(AppModel.UpdateProgress))
            DispatcherQueue.TryEnqueue(RefreshUpdateUI);
    }

    private void RefreshUpdateUI()
    {
        if (_model is null) return;
        var info = _model.AvailableUpdate;
        if (info is null)
        {
            UpdatePanel.Visibility = Visibility.Collapsed;
            return;
        }

        UpdatePanel.Visibility    = Visibility.Visible;
        UpdateAvailableText.Text  = $"Version {info.Version} available";
        UpdateNotesText.Text      = string.IsNullOrWhiteSpace(info.Notes) ? "" : info.Notes;

        var progress = _model.UpdateProgress;
        switch (progress.State)
        {
            case UpdateProgressState.Idle:
                InstallNowBtn.IsEnabled = true;
                UpdateProgressBar.Visibility = Visibility.Collapsed;
                UpdateProgressText.Visibility = Visibility.Collapsed;
                break;
            case UpdateProgressState.Downloading:
                InstallNowBtn.IsEnabled = false;
                UpdateProgressBar.Visibility = Visibility.Visible;
                UpdateProgressBar.Value = progress.Fraction;
                UpdateProgressText.Visibility = Visibility.Visible;
                UpdateProgressText.Text = $"Downloading… {(int)(progress.Fraction * 100)}%";
                break;
            case UpdateProgressState.Verifying:
                InstallNowBtn.IsEnabled = false;
                UpdateProgressBar.Visibility = Visibility.Visible;
                UpdateProgressBar.Value = progress.Fraction;
                UpdateProgressText.Visibility = Visibility.Visible;
                UpdateProgressText.Text = "Verifying integrity…";
                break;
            case UpdateProgressState.Installing:
                InstallNowBtn.IsEnabled = false;
                UpdateProgressBar.Visibility = Visibility.Visible;
                UpdateProgressBar.Value = 1;
                UpdateProgressText.Visibility = Visibility.Visible;
                UpdateProgressText.Text = "Installing — the app will restart when complete.";
                break;
            case UpdateProgressState.Failed:
                InstallNowBtn.IsEnabled = true;
                UpdateProgressBar.Visibility = Visibility.Collapsed;
                UpdateProgressText.Visibility = Visibility.Visible;
                UpdateProgressText.Text = $"Failed: {progress.Message}";
                break;
        }
    }

    private void UsernameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = UsernameBox.Text.Trim();
        if (v.Length == 0) { UsernameBox.Text = ConfigStore.Shared.Config.Username; return; }
        ConfigStore.Shared.Config.Username = v;
        ConfigStore.Shared.Save();
    }

    private void UpdateRepoBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = UpdateRepoBox.Text.Trim();
        if (string.IsNullOrEmpty(v)) v = "felzy613/lan-messenger";
        ConfigStore.Shared.Config.UpdateRepo = v;
        ConfigStore.Shared.Save();
    }

    private void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ConfigStore.Shared.Config.CloseToTray = CloseToTrayToggle.IsOn;
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
        CheckUpdatesBtn.IsEnabled = false;
        try
        {
            var info = _model is not null
                ? await _model.CheckForUpdatesAsync(silent: false)
                : await UpdateService.Shared.CheckAsync(ConfigStore.Shared.Config.UpdateRepo);
            UpdateStatusText.Text = info is null
                ? "You're up to date."
                : $"Update available: v{info.Version}";
            RefreshUpdateUI();
        }
        finally { CheckUpdatesBtn.IsEnabled = true; }
    }

    private void InstallNow_Click(object sender, RoutedEventArgs e)
    {
        _model?.InstallUpdate();
        RefreshUpdateUI();
    }
}
