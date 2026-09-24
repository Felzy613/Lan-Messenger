using LanMessenger.Core.Networking.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI.Chat;

// A remote-desktop audit record in the thread: the design system's
// RemoteAuditRow, and the Windows mirror of RemoteAuditRowView.swift.
//
// Everything it shows was decided when MessageRowViewModel.From built the row:
// the sentence written from this machine's chair (host or viewer, per the
// record's `viewing` flag), the duration once a session has ended, and the time.
// ChatPage picks this control over a bubble through MessageRowTemplateSelector.
public sealed partial class RemoteAuditRowControl : UserControl
{
    public static readonly DependencyProperty RowProperty =
        DependencyProperty.Register(nameof(Row), typeof(MessageRowViewModel),
            typeof(RemoteAuditRowControl),
            new PropertyMetadata(null, (d, _) => ((RemoteAuditRowControl)d).Refresh()));

    public MessageRowViewModel? Row
    {
        get => (MessageRowViewModel?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public RemoteAuditRowControl() => InitializeComponent();

    private void Refresh()
    {
        // Reset first: ListView recycles these controls across audit rows.
        LeadRun.Text = NameRun.Text = TailRun.Text = DurationRun.Text = "";
        WhenText.Text = "";
        if (Row is null) return;

        var audit = Row.Audit;
        EventIcon.Glyph = Glyph(audit?.Event);
        IconSlash.Visibility = audit?.Event == RemoteAuditEvent.ControlRevoked
            ? Visibility.Visible : Visibility.Collapsed;

        // The peer's name in semibold primary ink, the rest in secondary: the
        // trail is read weeks later to find out who, so the name carries it.
        string summary = Row.Text;
        string name = audit?.PeerName ?? "";
        int at = name.Length > 0 ? summary.IndexOf(name, StringComparison.Ordinal) : -1;
        if (at < 0)
        {
            LeadRun.Text = summary;
        }
        else
        {
            LeadRun.Text = summary[..at];
            NameRun.Text = name;
            TailRun.Text = summary[(at + name.Length)..];
        }

        if (audit?.DurationSummary is { } duration) DurationRun.Text = " " + duration;
        WhenText.Text = Row.Timestamp;
    }

    private static string Glyph(RemoteAuditEvent? e) => e switch
    {
        RemoteAuditEvent.SessionStarted => "\uE890",   // View: an eye
        RemoteAuditEvent.ControlGranted => "\uE7C9",   // TouchPointer: a hand
        RemoteAuditEvent.ControlRevoked => "\uE7C9",   // the same hand, slashed in XAML
        RemoteAuditEvent.SessionEnded   => "\uE71A",   // Stop
        // A record this build cannot read: the chat header's remote-desktop
        // glyph, which says what kind of event it was without claiming which.
        _                               => "\uE7F4",
    };
}
