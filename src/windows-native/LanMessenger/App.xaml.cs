using LanMessenger.UI;
using Microsoft.UI.Xaml;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LanMessenger;

public partial class App : Application
{
    public MainWindow? MainWindow { get; private set; }

    private static readonly StringBuilder _diag = new();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);

    public App()
    {
        InitializeComponent();

        DebugSettings.IsBindingTracingEnabled = true;
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) =>
        {
            lock (_diag) _diag.AppendLine($"  XamlResourceReferenceFailed: {e.Message}");
        };
        DebugSettings.BindingFailed += (_, e) =>
        {
            lock (_diag) _diag.AppendLine($"  BindingFailed: {e.Message}");
        };

        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            LogAndAlert(e.Exception, startup: MainWindow is null);
        };

        // Application.UnhandledException only covers the XAML/UI thread. Faulted
        // fire-and-forget tasks and thread-pool callbacks otherwise die silently
        // (or, for unobserved task exceptions, at an unpredictable GC-triggered
        // moment). Log them so "the app just quit" reports leave a trail.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            LogException("UnobservedTaskException", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogException("AppDomainUnhandled", ex);
        };
    }

    private static void LogException(string source, Exception ex)
    {
        try
        {
            LanMessenger.Core.Services.LanLogger.Error("App", $"{source}: {ex.GetType().Name} {ex.Message}", ex);
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            LogAndAlert(ex);
        }
    }

    private static void LogAndAlert(Exception ex, bool startup = true)
    {
        var logPath = "";
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LanMessenger");
            Directory.CreateDirectory(logDir);
            logPath = Path.Combine(logDir, "crash.log");

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.UtcNow:u}] HResult: 0x{ex.HResult:X8}");
            sb.AppendLine(ex.ToString());
            var chain = ex.InnerException;
            int depth = 0;
            while (chain != null && depth++ < 5)
            {
                sb.AppendLine($"  -- Inner ({depth}): {chain.GetType().Name}: {chain.Message}");
                chain = chain.InnerException;
            }
            foreach (System.Collections.DictionaryEntry kv in ex.Data)
                sb.AppendLine($"  -- Data[{kv.Key}]: {kv.Value}");
            lock (_diag)
            {
                if (_diag.Length > 0)
                {
                    sb.AppendLine("  -- Trace:");
                    sb.Append(_diag);
                }
            }
            sb.AppendLine();
            File.AppendAllText(logPath, sb.ToString());
        }
        catch { }

        var inner = ex.InnerException != null ? $"\nCause: {ex.InnerException.Message}" : "";
        var hr    = $"\nHResult: 0x{ex.HResult:X8}";
        var detail = logPath.Length > 0 ? $"\n\nLog: {logPath}" : "";
        var lead    = startup ? "LAN Messenger failed to start:" : "LAN Messenger hit an unexpected error:";
        var caption = startup ? "LAN Messenger – Startup Error" : "LAN Messenger – Error";
        MessageBox(0,
            $"{lead}\n\n{ex.Message}{inner}{hr}{detail}",
            caption,
            0x10 /* MB_ICONERROR */);
    }
}
