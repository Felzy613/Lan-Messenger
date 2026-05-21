using LanMessenger.Core.Persistence;
using LanMessenger.UI;
using LanMessenger.UI.Chat;
using LanMessenger.UI.Settings;
using LanMessenger.UI.Sidebar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    public ICommand OpenFromTrayCommand { get; }
    public ICommand QuitFromTrayCommand { get; }

    public MainWindow()
    {
        ShowFromTrayCommand = new DelegateCommand(() => ShowWindowFromTray());
        OpenFromTrayCommand = new DelegateCommand(() => ShowWindowFromTray());
        QuitFromTrayCommand = new DelegateCommand(() => QuitFromTray());

        InitializeComponent();
        Title = "LAN Messenger";
        Model = new AppModel(DispatcherQueue.GetForCurrentThread());

        var appWindow = AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(960, 700));

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
        var page = new ContactsPage { Model = Model };
        var dialog = new ContentDialog
        {
            Title             = "Contacts",
            PrimaryButtonText = "Done",
            DefaultButton     = ContentDialogButton.Primary,
            Content           = page,
            XamlRoot          = Content.XamlRoot,
        };
        // The "Search LAN" flow can't open its own ContentDialog while this
        // one is up (WinUI 3 allows only one per XamlRoot). Hide ourselves,
        // run the picker + naming dialogs in sequence, then reopen Contacts
        // so the user lands back on the updated list.
        page.SearchLanRequested += () => RunSearchLanFlowAsync(dialog);
        page.EditContactRequested += key => RunEditContactFlowAsync(dialog, key);
        page.DeleteContactRequested += key => RunDeleteContactFlowAsync(dialog, key);
        _activeDialog = dialog;
        try { await dialog.ShowAsync(); }
        finally { if (ReferenceEquals(_activeDialog, dialog)) _activeDialog = null; }
    }

    private async void RunEditContactFlowAsync(ContentDialog contactsDialog, string publicKeyB64)
    {
        contactsDialog.Hide();
        _activeDialog = null;

        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == publicKeyB64);
        if (contact is null)
        {
            ShowContactsPage();
            return;
        }

        var editor = new ContactEditorDialog(contact) { XamlRoot = Content.XamlRoot };
        _activeDialog = editor;
        try
        {
            var result = await editor.ShowAsync();
            if (result == ContentDialogResult.Primary)
                Model.UpdateContact(publicKeyB64, editor.NameValue, editor.PhotoB64Value);
        }
        finally { if (ReferenceEquals(_activeDialog, editor)) _activeDialog = null; }

        ShowContactsPage();
    }

    private async void RunDeleteContactFlowAsync(ContentDialog contactsDialog, string publicKeyB64)
    {
        contactsDialog.Hide();
        _activeDialog = null;

        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == publicKeyB64);
        var dialog = new ContentDialog
        {
            Title             = "Remove contact?",
            Content           = $"Remove {contact?.Username ?? "contact"} and delete the conversation?",
            PrimaryButtonText = "Remove",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = Content.XamlRoot,
        };

        _activeDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                Model.DeleteContact(publicKeyB64);
        }
        finally { if (ReferenceEquals(_activeDialog, dialog)) _activeDialog = null; }

        ShowContactsPage();
    }

    private async void RunSearchLanFlowAsync(ContentDialog contactsDialog)
    {
        // Closing the outer dialog frees the XamlRoot for the picker. ShowAsync
        // on contactsDialog will resume on the awaiter in ShowContactsPage —
        // we deliberately don't reopen until the picker (and any subsequent
        // naming dialogs) have fully closed.
        contactsDialog.Hide();
        _activeDialog = null;

        var picker = new PeerPickerDialog(Model) { XamlRoot = Content.XamlRoot };
        _activeDialog = picker;
        IReadOnlyList<PeerInfo> selected;
        try
        {
            var result = await picker.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                _activeDialog = null;
                ShowContactsPage();
                return;
            }
            selected = picker.SelectedPeers;
        }
        finally { if (ReferenceEquals(_activeDialog, picker)) _activeDialog = null; }

        foreach (var peer in selected)
        {
            var nameDialog = new NameContactDialog(peer) { XamlRoot = Content.XamlRoot };
            _activeDialog = nameDialog;
            try
            {
                var nameResult = await nameDialog.ShowAsync();
                var finalName = peer.Username;
                if (nameResult == ContentDialogResult.Primary)
                {
                    var entered = nameDialog.NameValue;
                    if (!string.IsNullOrWhiteSpace(entered)) finalName = entered.Trim();
                }
                Model.AddContact(peer.PublicKeyB64, finalName, peer.IP);
            }
            finally { if (ReferenceEquals(_activeDialog, nameDialog)) _activeDialog = null; }
        }

        // Reopen the Contacts dialog so the user sees the new contacts inline.
        ShowContactsPage();
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
        if (_archivedPage is null)
        {
            _archivedPage = new ArchivedPage { Model = Model };
            _archivedPage.BackRequested += () =>
            {
                // Restore the previous view — the chat page if a peer is selected, otherwise blank.
                if (Model.SelectedPeerIP is not null) ShowChatPage();
                else ContentFrame.Content = null;
            };
            _archivedPage.ConversationOpened += _ => ShowChatPage();
        }
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
    private async void NewMessageBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDialog is not null) return;
        var dialog = new NewMessageDialog(Model) { XamlRoot = Content.XamlRoot };
        _activeDialog = dialog;
        try
        {
            var result = await dialog.ShowAsync();
            // Primary = "Add Contact" branch — open the contacts dialog for adding a peer.
            if (result == ContentDialogResult.Primary)
            {
                _activeDialog = null;
                ShowContactsPage();
                return;
            }
            // Selecting a contact within the dialog sets SelectedPeerIP; show the chat view.
            if (Model.SelectedPeerIP is not null) ShowChatPage();
        }
        finally
        {
            if (ReferenceEquals(_activeDialog, dialog)) _activeDialog = null;
        }
    }

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

    private void QuitFromTray()
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
