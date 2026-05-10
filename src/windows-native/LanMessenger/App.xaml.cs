using LanMessenger.UI;
using Microsoft.UI.Xaml;
using System.IO;
using System.Runtime.InteropServices;

namespace LanMessenger;

public partial class App : Application
{
    public MainWindow? MainWindow { get; private set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            LogAndAlert(e.Exception);
        };
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

    private static void LogAndAlert(Exception ex)
    {
        var logPath = "";
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LanMessenger");
            Directory.CreateDirectory(logDir);
            logPath = Path.Combine(logDir, "crash.log");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.UtcNow:u}] HResult: 0x{ex.HResult:X8}");
            sb.AppendLine(ex.ToString());
            var chain = ex.InnerException;
            int depth = 0;
            while (chain != null && depth++ < 5)
            {
                sb.AppendLine($"  -- Inner ({depth}): {chain.GetType().Name}: {chain.Message}");
                chain = chain.InnerException;
            }
            sb.AppendLine();
            File.AppendAllText(logPath, sb.ToString());
        }
        catch { }

        var inner = ex.InnerException != null ? $"\nCause: {ex.InnerException.Message}" : "";
        var hr    = $"\nHResult: 0x{ex.HResult:X8}";
        var detail = logPath.Length > 0 ? $"\n\nLog: {logPath}" : "";
        MessageBox(0,
            $"LAN Messenger failed to start:\n\n{ex.Message}{inner}{hr}{detail}",
            "LAN Messenger – Startup Error",
            0x10 /* MB_ICONERROR */);
    }
}
