using LanMessenger.UI;
using LanMessenger.UI.Chat;
using LanMessenger.UI.Settings;
using LanMessenger.UI.Sidebar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger;

public sealed partial class MainWindow : Window
{
    public AppModel Model { get; }

    // Cached so we don't blow away scroll position / TextBox focus every time the
    // user clicks a conversation or a peer goes online/offline.
    private ChatPage?     _chatPage;
    private ContactsPage? _contactsPage;
    private SettingsPage? _settingsPage;

    public MainWindow()
    {
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

        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.ShowMigrationPrompt) &&
                Model.ShowMigrationPrompt)
                ShowMigrationDialog();
        };
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

    private void ShowContactsPage()
    {
        if (_contactsPage is null) _contactsPage = new ContactsPage { Model = Model };
        if (!ReferenceEquals(ContentFrame.Content, _contactsPage))
            ContentFrame.Content = _contactsPage;
    }

    private void ShowSettingsPage()
    {
        if (_settingsPage is null) _settingsPage = new SettingsPage { Model = Model };
        if (!ReferenceEquals(ContentFrame.Content, _settingsPage))
            ContentFrame.Content = _settingsPage;
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
}
