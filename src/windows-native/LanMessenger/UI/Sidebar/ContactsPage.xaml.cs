using LanMessenger.Core.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace LanMessenger.UI.Sidebar;

public sealed class ContactRowViewModel : INotifyPropertyChanged
{
    public string PublicKeyB64 { get; init; } = "";

    private string _username = "";
    public string Username
    {
        get => _username;
        set { if (_username != value) { _username = value; Notify(nameof(Username)); } }
    }

    private string _lastIP = "";
    public string LastIP
    {
        get => _lastIP;
        set { if (_lastIP != value) { _lastIP = value; Notify(nameof(LastIP)); } }
    }

    private string? _photoB64;
    public string? PhotoB64
    {
        get => _photoB64;
        set { if (_photoB64 != value) { _photoB64 = value; Notify(nameof(PhotoB64)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class ContactsPage : Page
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
        }
    }

    public ContactsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void OnModelChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Conversations) or nameof(AppModel.Peers))
            Refresh();
    }

    private void Refresh()
    {
        var rows = ConfigStore.Shared.Config.Contacts.Select(c => new ContactRowViewModel
        {
            PublicKeyB64 = c.PublicKeyB64,
            Username     = c.Username,
            LastIP       = c.LastIP,
            PhotoB64     = c.PhotoB64,
        }).ToList();

        ContactsList.ItemsSource = rows;
        EmptyState.Visibility    = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string keyB64) return;
        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == keyB64);
        var dialog = new ContentDialog
        {
            Title = "Remove contact?",
            Content = $"Remove {contact?.Username ?? "contact"} and delete the conversation?",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _model?.DeleteContact(keyB64);
            Refresh();
        }
    }

    private async void EditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (sender is not FrameworkElement el || el.Tag is not string keyB64) return;
        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == keyB64);
        if (contact is null) return;
        var editor = new ContactEditorDialog(contact) { XamlRoot = this.XamlRoot };
        var result = await editor.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _model.UpdateContact(keyB64, editor.NameValue, editor.PhotoB64Value);
            Refresh();
        }
    }

    private async void AddFromPeers_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var picker = new PeerPickerDialog(_model) { XamlRoot = this.XamlRoot };
        await picker.ShowAsync();
        Refresh();
    }
}
