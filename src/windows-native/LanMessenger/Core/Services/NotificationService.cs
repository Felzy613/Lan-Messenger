using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace LanMessenger.Core.Services;

// Sends Windows toast notifications for incoming messages and file arrivals.
public sealed class NotificationService
{
    public static NotificationService Shared { get; } = new();

    private bool _registered;

    private NotificationService() { }

    public void Register()
    {
        if (_registered) return;
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch { }
    }

    public void ShowMessage(string from, string text)
    {
        if (!_registered) return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(from)
                .AddText(text.Length > 120 ? text[..120] + "…" : text);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch { }
    }

    public void ShowFileReceived(string from, string filename)
    {
        if (!_registered) return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(from)
                .AddText($"Sent you a file: {filename}");
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch { }
    }
}
