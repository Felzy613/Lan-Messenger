using LanMessenger.Core.Persistence;
using LanMessenger.Core.Services;
using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

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
        ScreenshotDirBox.Text = ConfigStore.Shared.ScreenshotDirectory;
        ResetScreenshotDirBtn.Visibility = string.IsNullOrEmpty(cfg.ScreenshotDir)
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateRepoBox.Text = cfg.UpdateRepo;
        CloseToTrayToggle.IsOn = cfg.CloseToTray;
        VerboseLoggingToggle.IsOn = cfg.VerboseLogging;
        RelayEnabledToggle.IsOn = cfg.RelayEnabled;
        RelayUrlBox.Text = cfg.RelayWorkerUrl;
        RelayUrlBox.IsEnabled = cfg.RelayEnabled;
        VersionText.Text   = $"LAN Messenger v{UpdateService.Shared.CurrentVersion}";
        RefreshUpdateUI();
    }

    private void OnModelChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.AvailableUpdate) or nameof(AppModel.UpdateProgress))
            DispatcherQueue.TryEnqueue(RefreshUpdateUI);
    }

    private bool _notesExpanded;

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

        // Populate the RichTextBlock with Markdown-rendered release notes.
        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            var trimmed = MarkdownHelper.TrimNotes(info.Notes);
            MarkdownHelper.PopulateBlocks(UpdateNotesBlock, trimmed);
            UpdateNotesScroll.Visibility = Visibility.Visible;
            _notesExpanded = false;
            UpdateNotesScroll.MaxHeight = 120;

            var isLong = trimmed.Split('\n').Length > 8;
            NotesToggleBtn.Visibility = isLong ? Visibility.Visible : Visibility.Collapsed;
            NotesToggleBtn.Content    = "Show more";
        }
        else
        {
            UpdateNotesBlock.Blocks.Clear();
            UpdateNotesScroll.Visibility = Visibility.Collapsed;
            NotesToggleBtn.Visibility    = Visibility.Collapsed;
        }

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

    private void NotesToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _notesExpanded = !_notesExpanded;
        UpdateNotesScroll.MaxHeight    = _notesExpanded ? 360 : 120;
        // Reveal scrollbar in expanded state so the user can scroll long notes.
        UpdateNotesScroll.VerticalScrollBarVisibility = _notesExpanded
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Hidden;
        NotesToggleBtn.Content = _notesExpanded ? "Show less" : "Show more";
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

    private async void BrowseScreenshotDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        ConfigStore.Shared.Config.ScreenshotDir = folder.Path;
        ConfigStore.Shared.Save();
        ScreenshotDirBox.Text = folder.Path;
        ResetScreenshotDirBtn.Visibility = Visibility.Visible;
    }

    private void ResetScreenshotDir_Click(object sender, RoutedEventArgs e)
    {
        ConfigStore.Shared.Config.ScreenshotDir = "";
        ConfigStore.Shared.Save();
        ScreenshotDirBox.Text = ConfigStore.Shared.ScreenshotDirectory;
        ResetScreenshotDirBtn.Visibility = Visibility.Collapsed;
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

    private void VerboseLoggingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ConfigStore.Shared.Config.VerboseLogging = VerboseLoggingToggle.IsOn;
        ConfigStore.Shared.Save();
        LanLogger.Info("Settings", $"verbose logging {(VerboseLoggingToggle.IsOn ? "enabled" : "disabled")}");
    }

    private void RelayEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ConfigStore.Shared.Config.RelayEnabled = RelayEnabledToggle.IsOn;
        ConfigStore.Shared.Save();
        RelayUrlBox.IsEnabled = RelayEnabledToggle.IsOn;
    }

    private void RelayUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var url = RelayUrlBox.Text.Trim();
        if (!string.IsNullOrEmpty(url))
        {
            ConfigStore.Shared.Config.RelayWorkerUrl = url;
            ConfigStore.Shared.Save();
        }
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = LanLogger.LogsDirectory;
            Directory.CreateDirectory(dir);
            // Open in Explorer.  UseShellExecute lets us hand off the folder path.
            Process.Start(new ProcessStartInfo
            {
                FileName        = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogExportStatus.Text = $"Couldn't open folder: {ex.Message}";
        }
    }

    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        LogExportStatus.Text = "";
        var savePicker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            SuggestedFileName      = $"LanMessenger-Logs-{DateTime.Now:yyyy-MM-dd_HHmmss}",
        };
        savePicker.FileTypeChoices.Add("Zip archive", new List<string> { ".zip" });

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

        var target = await savePicker.PickSaveFileAsync();
        if (target is null) return;

        // The picker hands back a StorageFile already created at the chosen path —
        // delete it first so ExportLogBundle can write a fresh zip.
        var path = target.Path;
        try { File.Delete(path); } catch { /* may already be deleted */ }

        var ok = LanLogger.ExportLogBundle(path);
        LogExportStatus.Text = ok ? "Exported ✓" : "Export failed.";
    }
}
