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
            File.AppendAllText(logPath, $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }

        var detail = logPath.Length > 0 ? $"\n\nDetails saved to:\n{logPath}" : "";
        MessageBox(0,
            $"LAN Messenger failed to start:\n\n{ex.Message}{detail}",
            "LAN Messenger – Startup Error",
            0x10 /* MB_ICONERROR */);
    }
}
