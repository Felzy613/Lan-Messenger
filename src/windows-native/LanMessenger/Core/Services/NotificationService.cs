using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.Runtime.InteropServices;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace LanMessenger.Core.Services;

// Sends Windows toast notifications for incoming messages and file arrivals.
//
// Registration strategy
// ---------------------
// Unpackaged WinUI 3 apps require an explicit Application User Model ID (AUMID)
// before the notification platform can route activations back to this process.
//
// 1. SetCurrentProcessExplicitAppUserModelID stakes the AUMID for this process.
// 2. We attempt AppNotificationManager.Default.Register() (the Windows App SDK
//    unified path).  This can fail on certain Windows builds or when the COM
//    activator isn't registered (unpackaged requirement).
// 3. On any failure we fall back to the classic Windows.UI.Notifications
//    ToastNotificationManager, which only needs the AUMID — no COM activator
//    is required.  The fallback is transparent to callers.
public sealed class NotificationService
{
    public static NotificationService Shared { get; } = new();

    private const string AumId = "LanMessenger.DesktopApp";

    // Required for unpackaged apps: stake an AUMID so the notification
    // platform knows which process owns these toasts.
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    private bool _registered;
    private bool _useClassicToast;

    private NotificationService() { }

    public void Register()
    {
        if (_registered) return;
        try
        {
            // Step 0: ensure the AUMID is recorded in the registry so the toast
            // platform can resolve a display name for this unpackaged app. Without
            // this entry both AppNotificationManager and the classic
            // ToastNotificationManager silently drop notifications.
            EnsureAumidRegistered();

            // Step 1: claim an AUMID so every subsequent notification API call
            // (both AppNotificationManager and ToastNotificationManager) can
            // associate toasts with this process.
            var aumidResult = SetCurrentProcessExplicitAppUserModelID(AumId);
            if (aumidResult != 0)
                LanLogger.Warn("Notifications", $"SetCurrentProcessExplicitAppUserModelID returned HRESULT 0x{aumidResult:X8}.");

            // Step 2: register with the Windows App SDK notification manager.
            // This wires activation callbacks and creates the COM registration
            // entries needed for packaged-style delivery.
            AppNotificationManager.Default.Register();
            _registered = true;
            LanLogger.Info("Notifications", "AppNotificationManager registered successfully.");
        }
        catch (Exception ex)
        {
            // Common failure mode for unpackaged apps: the COM activator isn't
            // registered.  The classic ToastNotificationManager path doesn't
            // need it — only the AUMID, which we already set above.
            LanLogger.Warn("Notifications",
                $"AppNotificationManager.Register failed ({ex.GetType().Name}: {ex.Message}); " +
                "falling back to classic ToastNotificationManager.");
            _useClassicToast = true;
            _registered = true;   // allow ShowMessage to proceed via the fallback
        }
    }

    public void ShowMessage(string from, string text)
    {
        if (!_registered) return;
        var body = text.Length > 120 ? text[..120] + "…" : text;
        if (_useClassicToast) { ShowClassic(from, body); return; }
        try
        {
            var builder = new AppNotificationBuilder().AddText(from).AddText(body);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Notifications", $"ShowMessage via AppNotificationManager failed: {ex.Message}");
            _useClassicToast = true;
            ShowClassic(from, body);
        }
    }

    public void ShowFileReceived(string from, string filename)
    {
        if (!_registered) return;
        var body = $"Sent you a file: {filename}";
        if (_useClassicToast) { ShowClassic(from, body); return; }
        try
        {
            var builder = new AppNotificationBuilder().AddText(from).AddText(body);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Notifications", $"ShowFileReceived via AppNotificationManager failed: {ex.Message}");
            _useClassicToast = true;
            ShowClassic(from, body);
        }
    }

    // Writes the AUMID registry key that both the classic and AppNotification
    // managers need to resolve a display name for unpackaged apps. Idempotent —
    // skipped when the key already exists with a DisplayName value.
    private static void EnsureAumidRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + AumId);
            if (key.GetValue("DisplayName") is null)
                key.SetValue("DisplayName", "LAN Messenger");
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Notifications", $"AUMID registry write failed: {ex.Message}");
        }
    }

    // Classic Windows.UI.Notifications path — works without COM activator
    // registration; only requires AUMID to be set via
    // SetCurrentProcessExplicitAppUserModelID (done in Register() above).
    private static void ShowClassic(string heading, string body)
    {
        try
        {
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var nodes = xml.GetElementsByTagName("text");
            nodes[0].AppendChild(xml.CreateTextNode(heading));
            nodes[1].AppendChild(xml.CreateTextNode(body));
            ToastNotificationManager.CreateToastNotifier(AumId)
                                    .Show(new ToastNotification(xml));
        }
        catch (Exception ex)
        {
            // Last-resort path: if even the classic API fails, the LanLogger
            // entry is the only record — we never throw from notification code.
            LanLogger.Warn("Notifications", $"Classic toast also failed: {ex.Message}");
        }
    }
}
