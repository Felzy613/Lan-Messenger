using H.NotifyIcon;
using LanMessenger.Core.Persistence;
using LanMessenger.UI;
using LanMessenger.UI.Chat;
using LanMessenger.UI.Settings;
using LanMessenger.UI.Sidebar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using System.Windows.Input;
using WinRT.Interop;

namespace LanMessenger;

public sealed partial class MainWindow : Window
{
    public AppModel Model { get; }

    // Cached so we don't blow away scroll position / TextBox focus every time the
    // user clicks a conversation or a peer goes online/offline.
    private ChatPage?       _chatPage;
    private ArchivedPage?   _archivedPage;
    private ContentDialog?  _activeDialog;

    // True after the user has explicitly requested Quit (tray menu or AppDelegate); causes
    // closing the window to actually exit the process rather than hide.
    private bool _allowExit;

    public ICommand ShowFromTrayCommand { get; }

    public MainWindow()
    {
        InitializeComponent();
        ShowFromTrayCommand = new DelegateCommand(() => ShowWindowFromTray());
        Title = "LAN Messenger";
        Model = new AppModel(DispatcherQueue.GetForCurrentThread());

        var appWindow = AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(960, 700));

        TrayIcon.GeneratedIcon = new GeneratedIcon
        {
            Text = "LM",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 211, 102)),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.Bold,
        };

        Sidebar.Model = Model;
        Sidebar.ConversationSelected += ip =>
        {
            Model.SelectedPeerIP = ip;
            ShowChatPage();
        };
        Sidebar.SettingsRequested += ShowSettingsPage;
        Sidebar.ArchivedRequested += ShowArchivedPage;

        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.ShowMigrationPrompt) &&
                Model.ShowMigrationPrompt)
                ShowMigrationDialog();
        };

        // Intercept window close so we hide-to-tray instead of exiting (unless the
        // user has explicitly asked to quit). Hooking AppWindow.Closing is the
        // unpackaged-WinUI3 way to do this — the regular Window.Closed event fires
        // too late to cancel.
        appWindow.Closing += OnAppWindowClosing;
    }

    // Reuse a single ChatPage instance — re-binding `Model` would also re-fire OnPropertyChanged,
    // so we only assign it once. ChatPage observes SelectedPeerIP itself.
    private void ShowChatPage()
    {
        if (_chatPage is null)
        {
            _chatPage = new ChatPage { Model = Model };
        }
        if (!ReferenceEquals(ContentFrame.Content, _chatPage))
            ContentFrame.Content = _chatPage;
    }

    private async void ShowContactsPage()
    {
        if (_activeDialog is not null) return;
        var dialog = new ContentDialog
        {
            Title           = "Contacts",
            CloseButtonText = "Done",
            Content         = new ContactsPage { Model = Model },
            XamlRoot        = Content.XamlRoot,
        };
        _activeDialog = dialog;
        try { await dialog.ShowAsync(); }
        finally { _activeDialog = null; }
    }

    private async void ShowSettingsPage()
    {
        if (_activeDialog is not null) return;
        var dialog = new ContentDialog
        {
            Title           = "Settings",
            CloseButtonText = "Done",
            Content         = new SettingsPage { Model = Model },
            XamlRoot        = Content.XamlRoot,
        };
        _activeDialog = dialog;
        try { await dialog.ShowAsync(); }
        finally { _activeDialog = null; }
    }

    private void ShowArchivedPage()
    {
        if (_archivedPage is null) _archivedPage = new ArchivedPage { Model = Model };
        if (!ReferenceEquals(ContentFrame.Content, _archivedPage))
            ContentFrame.Content = _archivedPage;
    }

    private async void ShowMigrationDialog()
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Import Existing Account?",
            PrimaryButtonText = "Import Key",
            SecondaryButtonText = "Start Fresh",
            Content = new TextBlock
            {
                Text = "A LAN Messenger account was found. Import your existing key and chat " +
                       "history, or start with a fresh identity?",
                TextWrapping = TextWrapping.Wrap
            },
            XamlRoot = Content.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            Model.AcceptMigrationWithExistingKey();
        else
            Model.AcceptMigrationWithFreshKey();
    }

    private void ContactsBtn_Click(object sender, RoutedEventArgs e) => ShowContactsPage();
    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

    // MARK: - Tray / background lifecycle

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowExit) return;
        if (!ConfigStore.Shared.Config.CloseToTray) return;
        // Cancel the close, hide the window. The TaskbarIcon keeps the app alive.
        args.Cancel = true;
        HideWindow();
    }

    private void HideWindow()
    {
        AppWindow.Hide();
    }

    public void ShowWindowFromTray()
    {
        var aw = AppWindow;
        aw.Show();
        // Bring to foreground.
        var hwnd = WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowWindowFromTray();

    private void TrayQuit_Click(object sender, RoutedEventArgs e)
    {
        _allowExit = true;
        Application.Current.Exit();
    }

    // P/Invoke wrappers for restoring a minimized/hidden window.
    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    // Minimal ICommand for the tray icon left-click binding.
    private sealed class DelegateCommand : ICommand
    {
        private readonly Action _action;
        public DelegateCommand(Action action) => _action = action;
        public event System.EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }
}
