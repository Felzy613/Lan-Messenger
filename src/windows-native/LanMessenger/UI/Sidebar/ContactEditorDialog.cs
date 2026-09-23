using LanMessenger.UI;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI.Sidebar;

// "New message" picker — shows the user's saved contacts and lets them pick one to
// open a thread with. Returns ContentDialogResult.Primary if the user hits "Add Contact"
// (from the persistent button or the empty state) so the host can swap to the
// contacts dialog.
//
// Content is NewMessagePage, which is ContactsPage's search bar and row style
// reused verbatim — the two sheets are the same shape as macOS's ContactsView /
// NewMessageView. This is a standalone flow (starting a conversation), not part
// of the Contacts editing chain — see ContactsDialog.cs for the Contacts/Find
// Contacts/Name/Edit/Delete flow, which is a single persistent ContentDialog for
// the reasons documented there.
public sealed class NewMessageDialog : ContentDialog
{
    private readonly AppModel _model;
    private readonly NewMessagePage _page;

    // ContentDialog.Hide() always reports ContentDialogResult.None — only an
    // actual PrimaryButtonClick reports Primary — so the empty state's inline
    // "Add a contact" button (not a dialog footer button) can't signal
    // "switch to contacts" through the awaited ShowAsync() result. This event
    // is the second channel; MainWindow.NewMessageBtn_Click listens on both it
    // and the ShowAsync() result before deciding whether to open ContactsDialog.
    public event Action? AddContactRequested;

    public NewMessageDialog(AppModel model)
    {
        _model = model;
        Title             = "New Message";
        PrimaryButtonText = "Add Contact";
        CloseButtonText   = "Cancel";
        DefaultButton     = ContentDialogButton.Close;
        GlassDialog.Apply(this);

        _page = new NewMessagePage { Model = model };
        _page.ContactSelected += publicKeyB64 =>
        {
            _model.StartConversation(publicKeyB64);
            Hide();
        };
        _page.AddContactRequested += () =>
        {
            Hide();
            AddContactRequested?.Invoke();
        };
        Content = _page;

        Closed += (_, _) => _page.Model = null;
    }
}
