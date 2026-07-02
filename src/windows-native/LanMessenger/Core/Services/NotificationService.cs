using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace LanMessenger.Core.Services;

// Sends Windows toast notifications for incoming messages and file arrivals.
//
// Registration strategy
// ---------------------
// Unpackaged WinUI 3 apps require three things before the notification platform
// will show popup toasts:
//
// 1. SetCurrentProcessExplicitAppUserModelID stakes the AUMID for this process.
//
// 2. HKCU\Software\Classes\AppUserModelId\{AUMID} must have both DisplayName AND
//    IconUri. Without IconUri, AppNotificationManager silently drops notifications
//    on Windows 11 even when Register() succeeds.
//
// 3. A Start Menu shortcut (.lnk) with System.AppUserModel.ID set to the AUMID.
//    Without this, ToastNotificationManager.CreateToastNotifier(aumId) — the
//    classic fallback — silently discards every notification on Windows 10 and
//    pre-22H2 Windows 11. The Inno installer creates a shortcut but historically
//    omitted the AppUserModel.ID property; EnsureStartMenuShortcut() fills that
//    gap for both installed and portable runs.
//
// 4. We attempt AppNotificationManager.Default.Register() (Windows App SDK path).
//    On any failure we fall back to the classic ToastNotificationManager, which
//    only needs the AUMID and shortcut described above. The fallback is transparent
//    to callers.
public sealed class NotificationService
{
    public static NotificationService Shared { get; } = new();

    private const string AumId = "LanMessenger.DesktopApp";

    // Required for unpackaged apps: stake an AUMID so the notification
    // platform knows which process owns these toasts.
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    // -----------------------------------------------------------------------
    // COM interfaces needed to create a Start Menu shortcut with AUMID set.
    // All methods must be declared in vtable order even if unused, because the
    // CLR COM marshaler resolves calls by positional vtable offset.
    // -----------------------------------------------------------------------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                     int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
                            int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
                                 int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
                          int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                             int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatID;
        public int  PropertyID;
    }

    // Minimal PROPVARIANT layout sufficient for VT_LPWSTR (type 31) string values.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr PwszVal;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        // All methods return HRESULT with no [out retval] — declare void so the
        // CLR marshaler checks the HRESULT and throws on failure rather than
        // appending a phantom [out retval] parameter to the native call.
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant propvar);
        void Commit();
    }

    // System.AppUserModel.ID — the property that links a shortcut to its AUMID.
    private static readonly PropertyKey PkeyAppUserModelId = new()
    {
        FormatID   = new Guid("{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}"),
        PropertyID = 5
    };

    // -----------------------------------------------------------------------

    private bool _registered;
    private bool _useClassicToast;

    private NotificationService() { }

    public void Register()
    {
        if (_registered) return;
        try
        {
            // Step 0: ensure registry and shortcut are in place so the toast
            // platform can resolve an icon and route activations.
            EnsureAumidRegistered();
            EnsureStartMenuShortcut();

            // Step 1: claim the AUMID for this process.
            var aumidResult = SetCurrentProcessExplicitAppUserModelID(AumId);
            if (aumidResult != 0)
                LanLogger.Warn("Notifications",
                    $"SetCurrentProcessExplicitAppUserModelID returned HRESULT 0x{aumidResult:X8}.");

            // Step 2: register with the Windows App SDK notification manager.
            AppNotificationManager.Default.Register();
            _registered = true;
            LanLogger.Info("Notifications", "AppNotificationManager registered successfully.");
        }
        catch (Exception ex)
        {
            // Common failure mode for unpackaged apps: no COM activator registered.
            // The classic ToastNotificationManager path only needs the AUMID + shortcut.
            LanLogger.Warn("Notifications",
                $"AppNotificationManager.Register failed ({ex.GetType().Name}: {ex.Message}); " +
                "falling back to classic ToastNotificationManager.");
            _useClassicToast = true;
            _registered = true;
        }
    }

    public void ShowMessage(string from, string text)
    {
        if (!_registered) return;
        var body = text.Length > 120 ? text[..120] + "…" : text;
        if (_useClassicToast) { ShowClassic(from, body); return; }
        try
        {
            var builder = MakeBuilder().AddText(from).AddText(body);
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
            var builder = MakeBuilder().AddText(from).AddText(body);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Notifications", $"ShowFileReceived via AppNotificationManager failed: {ex.Message}");
            _useClassicToast = true;
            ShowClassic(from, body);
        }
    }

    // "Urgent" is the only scenario that both (a) pops the banner even while
    // our own window has focus and (b) breaks through Focus Assist/Do Not
    // Disturb, without the caveats of "reminder" (silently dropped unless it
    // has a background-activating button) or "alarm" (loops ringtone audio).
    // See toast schema docs for the scenario attribute.
    private static AppNotificationBuilder MakeBuilder()
    {
        var builder = new AppNotificationBuilder();
        if (AppNotificationBuilder.IsUrgentScenarioSupported())
            builder.SetScenario(AppNotificationScenario.Urgent);
        return builder;
    }

    // Writes the registry key that both notification managers need to resolve
    // a display name and icon for this unpackaged app.  IconUri is required —
    // omitting it causes AppNotificationManager to silently drop toasts.
    private static void EnsureAumidRegistered()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + AumId);
            if (key.GetValue("DisplayName") is null)
                key.SetValue("DisplayName", "LAN Messenger");
            if (key.GetValue("IconUri") is null && !string.IsNullOrEmpty(exePath))
            {
                // Prefer the bundled .ico file; fall back to the exe itself.
                var iconPath = Path.Combine(
                    Path.GetDirectoryName(exePath) ?? "", "Assets", "icon.ico");
                key.SetValue("IconUri", File.Exists(iconPath) ? iconPath : exePath);
            }
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Notifications", $"AUMID registry write failed: {ex.Message}");
        }
    }

    // Creates a per-user Start Menu shortcut with System.AppUserModel.ID set so
    // ToastNotificationManager.CreateToastNotifier(AumId) can route toasts on
    // Windows 10 and pre-22H2 Windows 11.  Skipped when the shortcut already
    // exists (idempotent across launches).
    private static void EnsureStartMenuShortcut()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "LAN Messenger.lnk");

            if (File.Exists(shortcutPath)) return;

            var shellLink = (IShellLinkW)new CShellLink();
            shellLink.SetPath(exePath);
            shellLink.SetDescription("LAN Messenger");

            // QI for IPropertyStore and stamp the AUMID so the notification platform
            // can correlate toasts with this shortcut.
            var propStore = (IPropertyStore)shellLink;
            var pv = new PropVariant
            {
                Vt      = 31 /* VT_LPWSTR */,
                PwszVal = Marshal.StringToCoTaskMemUni(AumId)
            };
            try
            {
                var propKey = PkeyAppUserModelId;
                propStore.SetValue(ref propKey, ref pv);
                propStore.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(pv.PwszVal);
            }

            var persistFile = (IPersistFile)shellLink;
            persistFile.Save(shortcutPath, false);
            LanLogger.Info("Notifications", $"Created Start Menu shortcut at {shortcutPath}");
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Notifications", $"Start Menu shortcut creation failed: {ex.Message}");
        }
    }

    // Classic Windows.UI.Notifications path — works without COM activator
    // registration; requires AUMID (set above) and the Start Menu shortcut.
    private static void ShowClassic(string heading, string body)
    {
        try
        {
            var xml   = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var nodes = xml.GetElementsByTagName("text");
            nodes[0].AppendChild(xml.CreateTextNode(heading));
            nodes[1].AppendChild(xml.CreateTextNode(body));
            // Same reasoning as MakeBuilder(): force the popup even while our
            // window has focus and break through Focus Assist.
            var toastElement = (XmlElement)xml.GetElementsByTagName("toast")[0]!;
            toastElement.SetAttribute("scenario", "urgent");
            ToastNotificationManager.CreateToastNotifier(AumId)
                                    .Show(new ToastNotification(xml));
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Notifications", $"Classic toast failed: {ex.Message}");
        }
    }
}
