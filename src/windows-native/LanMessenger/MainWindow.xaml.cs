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

    public MainWindow()
    {
        InitializeComponent();
        Title = "LAN Messenger";
        Model = new AppModel(DispatcherQueue.GetForCurrentThread());

        var appWindow = AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(900, 660));

        Sidebar.Model = Model;
        Sidebar.ConversationSelected += ip =>
        {
            Model.SelectedPeerIP = ip;
            var page = new ChatPage { Model = Model };
            ContentFrame.Content = page;
        };
        Sidebar.SettingsRequested += () =>
        {
            ContentFrame.Content = new SettingsPage { Model = Model };
        };

        // Navigate to chat if a peer is already selected
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.ShowMigrationPrompt) &&
                Model.ShowMigrationPrompt)
                ShowMigrationDialog();
        };
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

    private void ContactsBtn_Click(object sender, RoutedEventArgs e)
    {
        ContentFrame.Content = new ContactsPage { Model = Model };
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        ContentFrame.Content = new SettingsPage { Model = Model };
    }
}
