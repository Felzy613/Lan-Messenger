using LanMessenger.Core.Persistence;
using LanMessenger.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LanMessenger.UI.Sidebar;

// Single persistent ContentDialog for the whole Contacts flow (list, find/add,
// name, edit, delete-confirm). WinUI 3 allows only one ContentDialog open per
// XamlRoot at a time; the previous version of this flow used four separate
// ContentDialogs that had to be hidden and reopened around each other, which
// caused a class of crashes (0x80000019) and refresh-storm bugs patched three
// separate times in git history. Swapping Content/Title/buttons inside one
// dialog instance removes the problem at the root instead of continuing to
// patch around it — no visible change to the "overlay dialog on top of chat"
// look this already had.
public sealed class ContactsDialog : ContentDialog
{
    private enum State { List, Find, Name, Edit, Delete }

    private readonly AppModel _model;
    private readonly ContactsPage _listPage;
    private State _state = State.List;

    private FindContactsPanel? _findPanel;
    private List<PeerInfo> _namingQueue = [];
    private PeerInfo? _naming;
    private NameContactPanel? _namePanel;

    private string? _editingKeyB64;
    private ContactEditorPanel? _editorPanel;

    private string? _deletingKeyB64;

    public ContactsDialog(AppModel model)
    {
        _model = model;
        _listPage = new ContactsPage { Model = model };
        _listPage.SearchLanRequested    += EnterFindState;
        _listPage.EditContactRequested  += EnterEditState;
        _listPage.DeleteContactRequested += EnterDeleteState;

        PrimaryButtonClick += OnPrimaryButtonClick;
        CloseButtonClick   += OnCloseButtonClick;
        // Release the list page's model subscription (and stop any in-flight
        // scan timer) once the whole flow is done, so a closed dialog doesn't
        // linger forever pinned alive by AppModel.PropertyChanged.
        Closed += (_, _) =>
        {
            _listPage.Model = null;
            _findPanel?.Detach();
        };

        ShowListState();
    }

    // MARK: - State transitions

    private void ShowListState()
    {
        _state  = State.List;
        Title   = "Contacts";
        Content = _listPage;
        PrimaryButtonText      = "Done";
        CloseButtonText        = "";
        IsPrimaryButtonEnabled = true;
        DefaultButton          = ContentDialogButton.Primary;
    }

    private void EnterFindState()
    {
        _state  = State.Find;
        Title   = "Find Contacts";
        _findPanel = new FindContactsPanel(_model);
        _findPanel.SelectionChanged += hasSelection => IsPrimaryButtonEnabled = hasSelection;
        Content = _findPanel;
        PrimaryButtonText      = "Save";
        CloseButtonText        = "Cancel";
        IsPrimaryButtonEnabled = false;
        DefaultButton          = ContentDialogButton.Primary;
    }

    private void EnterNameState()
    {
        _naming = _namingQueue[0];
        _namingQueue.RemoveAt(0);
        _state  = State.Name;
        Title   = "Name contact";
        _namePanel = new NameContactPanel(_naming);
        Content = _namePanel;
        PrimaryButtonText      = "Save";
        CloseButtonText        = $"Use \"{_naming.Username}\"";
        IsPrimaryButtonEnabled = true;
        DefaultButton          = ContentDialogButton.Primary;
    }

    private void EnterEditState(string publicKeyB64)
    {
        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == publicKeyB64);
        if (contact is null) { ShowListState(); return; }
        _editingKeyB64 = publicKeyB64;
        _state  = State.Edit;
        Title   = "Edit Contact";
        _editorPanel = new ContactEditorPanel(contact);
        Content = _editorPanel;
        PrimaryButtonText      = "Save";
        CloseButtonText        = "Cancel";
        IsPrimaryButtonEnabled = true;
        DefaultButton          = ContentDialogButton.Primary;
    }

    private void EnterDeleteState(string publicKeyB64)
    {
        var contact = ConfigStore.Shared.Config.Contacts.FirstOrDefault(c => c.PublicKeyB64 == publicKeyB64);
        _deletingKeyB64 = publicKeyB64;
        _state  = State.Delete;
        Title   = "Remove contact?";
        Content = new TextBlock
        {
            Text = $"Remove {contact?.Username ?? "contact"} and delete the conversation?",
            TextWrapping = TextWrapping.Wrap,
        };
        PrimaryButtonText      = "Remove";
        CloseButtonText        = "Cancel";
        IsPrimaryButtonEnabled = true;
        // Default to Close (Cancel) here specifically so pressing Enter can't
        // accidentally delete a contact.
        DefaultButton = ContentDialogButton.Close;
    }

    // MARK: - Button dispatch
    //
    // Every intermediate state (everything except List) sets args.Cancel = true
    // so the click transitions Content in place instead of closing the whole
    // dialog. Only "Done" from the List state is allowed to actually close it.

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        switch (_state)
        {
            case State.List:
                break; // "Done" — let the dialog close normally.

            case State.Find:
                args.Cancel = true;
                _namingQueue = [.. _findPanel!.SelectedPeers];
                _findPanel.Detach();
                _findPanel = null;
                AdvanceNamingOrReturnToList();
                break;

            case State.Name:
                args.Cancel = true;
                var entered = _namePanel!.NameValue.Trim();
                var finalName = entered.Length > 0 ? entered : _naming!.Username;
                _model.AddContact(_naming!.PublicKeyB64, finalName, _naming.IP);
                AdvanceNamingOrReturnToList();
                break;

            case State.Edit:
                args.Cancel = true;
                if (_editingKeyB64 is not null)
                    _model.UpdateContact(_editingKeyB64, _editorPanel!.NameValue, _editorPanel!.PhotoB64Value);
                ShowListState();
                break;

            case State.Delete:
                args.Cancel = true;
                if (_deletingKeyB64 is not null) _model.DeleteContact(_deletingKeyB64);
                ShowListState();
                break;
        }
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        switch (_state)
        {
            case State.List:
                break; // CloseButtonText is empty in this state — button is hidden.

            case State.Find:
                args.Cancel = true;
                _findPanel?.Detach();
                _findPanel = null;
                ShowListState();
                break;

            case State.Name:
                args.Cancel = true;
                // "Use "<default>"" — accept the broadcast username as-is.
                _model.AddContact(_naming!.PublicKeyB64, _naming.Username, _naming.IP);
                AdvanceNamingOrReturnToList();
                break;

            case State.Edit:
                args.Cancel = true;
                ShowListState();
                break;

            case State.Delete:
                args.Cancel = true;
                ShowListState();
                break;
        }
    }

    private void AdvanceNamingOrReturnToList()
    {
        _naming    = null;
        _namePanel = null;
        if (_namingQueue.Count > 0) { EnterNameState(); return; }
        ShowListState();
    }
}

// MARK: - Content panel: name one freshly-discovered peer

// Hosted as ContactsDialog content (not its own ContentDialog — see above).
public sealed class NameContactPanel : StackPanel
{
    private readonly TextBox _nameBox;
    public string NameValue => _nameBox.Text;

    public NameContactPanel(PeerInfo peer)
    {
        Spacing = 16;
        Width   = 360;

        _nameBox = new TextBox
        {
            Text            = peer.Username,
            Header          = "Display name",
            PlaceholderText = "Contact name",
            MinWidth        = 280,
        };

        var avatar = new AvatarControl { Width = 64, Height = 64, NameText = peer.Username };
        _nameBox.TextChanged += (_, _) =>
            avatar.NameText = string.IsNullOrWhiteSpace(_nameBox.Text) ? peer.Username : _nameBox.Text;

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = peer.Username, Style = SidebarStyles.TryGetStyle("BodyStrongTextBlockStyle") });
        info.Children.Add(new TextBlock { Text = "Detected nearby", Opacity = 0.6, FontSize = 11 });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        header.Children.Add(avatar);
        header.Children.Add(info);

        Children.Add(header);
        Children.Add(_nameBox);
    }
}

// MARK: - Content panel: edit an existing contact's name/photo

public sealed class ContactEditorPanel : StackPanel
{
    private readonly TextBox _nameBox;
    private readonly AvatarControl _avatar;
    private string? _currentPhotoB64;

    public string NameValue => _nameBox.Text.Trim();
    public string? PhotoB64Value => _currentPhotoB64;

    public ContactEditorPanel(ContactConfig contact)
    {
        _currentPhotoB64 = contact.PhotoB64;
        Spacing = 16;
        Width   = 380;

        _avatar = new AvatarControl
        {
            Width = 88, Height = 88,
            NameText = contact.Username,
            PhotoB64 = contact.PhotoB64,
        };
        _nameBox = new TextBox
        {
            Text = contact.Username, Header = "Display name",
            PlaceholderText = "Contact name", MinWidth = 260,
        };
        _nameBox.TextChanged += (_, _) => _avatar.NameText = _nameBox.Text;

        var choose = new Button { Content = "Choose Photo…" };
        choose.Click += async (_, _) => await PickPhotoAsync();
        var remove = new Button { Content = "Remove Photo" };
        remove.Click += (_, _) => { _currentPhotoB64 = null; _avatar.PhotoB64 = null; };

        var photoButtons = new StackPanel { Spacing = 8, Orientation = Orientation.Vertical };
        photoButtons.Children.Add(choose);
        photoButtons.Children.Add(remove);

        var avatarRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        avatarRow.Children.Add(_avatar);
        avatarRow.Children.Add(photoButtons);

        var deviceIdLabel = new TextBlock
        {
            Text = $"Device ID: {(contact.PublicKeyB64.Length > 16 ? contact.PublicKeyB64[..16] + "…" : contact.PublicKeyB64)}",
            FontSize = 11, Opacity = 0.6,
        };
        var ipLabel = new TextBlock
        {
            Text = $"Last IP: {(string.IsNullOrEmpty(contact.LastIP) ? "—" : contact.LastIP)}",
            FontSize = 11, Opacity = 0.6,
        };

        Children.Add(avatarRow);
        Children.Add(_nameBox);
        Children.Add(deviceIdLabel);
        Children.Add(ipLabel);
    }

    private async Task PickPhotoAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(((global::LanMessenger.App)Application.Current).MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            using var src = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(src);

            const double targetMax = 256.0;
            double srcW = decoder.PixelWidth, srcH = decoder.PixelHeight;
            double scale = Math.Min(1.0, targetMax / Math.Max(srcW, srcH));
            uint newW = (uint)Math.Max(1, Math.Round(srcW * scale));
            uint newH = (uint)Math.Max(1, Math.Round(srcH * scale));

            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth  = newW,
                ScaledHeight = newH,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
            };
            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var pixels = pixelData.DetachPixelData();

            using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, output);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                newW, newH, 96, 96, pixels);
            await encoder.FlushAsync();
            output.Seek(0);
            using var ms = new MemoryStream();
            await output.AsStreamForRead().CopyToAsync(ms);
            _currentPhotoB64 = Convert.ToBase64String(ms.ToArray());
            _avatar.PhotoB64 = _currentPhotoB64;
        }
        catch { /* swallow — keep existing photo */ }
    }
}

// MARK: - Content panel: find & select nearby peers to add

public sealed class FindContactsPanel : Grid
{
    private readonly AppModel _model;
    private readonly HashSet<string> _selectedKeys = [];
    private readonly StackPanel _scanningView;
    private readonly StackPanel _emptyView;
    private readonly ListView _resultsList;
    private bool _scanning;
    private DispatcherTimer? _scanTimer;

    public event Action<bool>? SelectionChanged;
    public IReadOnlyList<PeerInfo> SelectedPeers { get; private set; } = [];

    public FindContactsPanel(AppModel model)
    {
        _model = model;
        MinWidth  = 420;
        MinHeight = 320;
        MaxHeight = 420;

        _scanningView = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        _scanningView.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        _scanningView.Children.Add(new TextBlock
        {
            Text = "Scanning for peers…",
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var scanAgainBtn = new Button { Content = "Scan Again", HorizontalAlignment = HorizontalAlignment.Center };
        scanAgainBtn.Click += (_, _) => TriggerScan();

        _emptyView = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _emptyView.Children.Add(new FontIcon
        {
            FontFamily = SidebarStyles.TryGetFontFamily("SymbolThemeFontFamily"),
            Glyph      = "",
            FontSize   = 40,
            Foreground = SidebarStyles.TryGetBrush("TextFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _emptyView.Children.Add(new TextBlock
        {
            Text  = "No peers found",
            Style = SidebarStyles.TryGetStyle("SubtitleTextBlockStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _emptyView.Children.Add(new TextBlock
        {
            Text = "Make sure other devices are on the same network and running LAN Messenger.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 260,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _emptyView.Children.Add(scanAgainBtn);

        _resultsList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            Visibility    = Visibility.Collapsed,
        };

        Children.Add(_scanningView);
        Children.Add(_emptyView);
        Children.Add(_resultsList);

        _model.PropertyChanged += OnModelChanged;
        TriggerScan();
    }

    // Must be called when this panel stops being the dialog's active content —
    // otherwise the AppModel.PropertyChanged subscription keeps it (and the
    // scan timer) alive indefinitely.
    public void Detach()
    {
        _model.PropertyChanged -= OnModelChanged;
        _scanTimer?.Stop();
        _scanTimer = null;
    }

    private void OnModelChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.Peers)) RefreshRows();
    }

    private void TriggerScan()
    {
        _scanning = true;
        _model.Coordinator.Discovery.SendBeacon();
        RefreshRows();

        _scanTimer?.Stop();
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _scanTimer.Tick += (_, _) =>
        {
            _scanTimer?.Stop();
            _scanning = false;
            RefreshRows();
        };
        _scanTimer.Start();
    }

    private void RefreshRows()
    {
        var savedKeys = ConfigStore.Shared.Config.Contacts.Select(c => c.PublicKeyB64).ToHashSet();
        var rows = _model.Peers.Values
            .Where(p => !savedKeys.Contains(p.PublicKeyB64))
            .OrderBy(p => p.Username)
            .ToList();

        if (_scanning)
        {
            _scanningView.Visibility = Visibility.Visible;
            _emptyView.Visibility    = Visibility.Collapsed;
            _resultsList.Visibility  = Visibility.Collapsed;
            return;
        }

        if (rows.Count == 0)
        {
            _scanningView.Visibility = Visibility.Collapsed;
            _emptyView.Visibility    = Visibility.Visible;
            _resultsList.Visibility  = Visibility.Collapsed;
            return;
        }

        _scanningView.Visibility = Visibility.Collapsed;
        _emptyView.Visibility    = Visibility.Collapsed;
        _resultsList.Visibility  = Visibility.Visible;

        _resultsList.Items.Clear();
        foreach (var peer in rows)
        {
            var capturedPeer = peer;
            var check = new CheckBox
            {
                IsChecked = _selectedKeys.Contains(peer.PublicKeyB64),
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.Tapped    += (_, e) => e.Handled = true;
            check.Checked   += (_, _) => { _selectedKeys.Add(capturedPeer.PublicKeyB64); NotifySelection(); };
            check.Unchecked += (_, _) => { _selectedKeys.Remove(capturedPeer.PublicKeyB64); NotifySelection(); };

            var avatar = new AvatarControl { Width = 36, Height = 36, NameText = peer.Username };
            var name   = new TextBlock { Text = peer.Username, Style = SidebarStyles.TryGetStyle("BodyStrongTextBlockStyle") };
            var status = new TextBlock { Text = peer.IP, Opacity = 0.6, FontSize = 11 };
            var info   = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(name);
            info.Children.Add(status);

            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 6, 8, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(check, 0);
            Grid.SetColumn(avatar, 1);
            Grid.SetColumn(info, 2);
            row.Children.Add(check);
            row.Children.Add(avatar);
            row.Children.Add(info);

            row.Tapped += (_, _) => { check.IsChecked = !(check.IsChecked ?? false); };

            _resultsList.Items.Add(row);
        }
    }

    private void NotifySelection()
    {
        SelectedPeers = _model.Peers.Values.Where(p => _selectedKeys.Contains(p.PublicKeyB64)).ToList();
        SelectionChanged?.Invoke(_selectedKeys.Count > 0);
    }
}

// MARK: - Shared theme-resource lookup helpers (safe against missing keys)

internal static class SidebarStyles
{
    public static Style? TryGetStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Style : null;

    public static Brush? TryGetBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;

    public static FontFamily TryGetFontFamily(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is FontFamily f
            ? f
            : new FontFamily("Segoe Fluent Icons");
}
