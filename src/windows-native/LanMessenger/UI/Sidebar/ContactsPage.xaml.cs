using LanMessenger.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI.Sidebar;

public sealed class ContactRowViewModel
{
    public string PublicKeyB64 { get; init; } = "";
    public string Username     { get; init; } = "";
    public string LastIP       { get; init; } = "";
}

public sealed partial class ContactsPage : Page
{
    public AppModel? Model { get; set; }

    public ContactsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var rows = ConfigStore.Shared.Config.Contacts.Select(c => new ContactRowViewModel
        {
            PublicKeyB64 = c.PublicKeyB64,
            Username     = c.Username,
            LastIP       = c.LastIP,
        }).ToList();

        ContactsList.ItemsSource = rows;
        EmptyState.Visibility    = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string keyB64) return;
        ConfigStore.Shared.Config.Contacts.RemoveAll(c => c.PublicKeyB64 == keyB64);
        ConfigStore.Shared.Save();
        Refresh();
    }
}
