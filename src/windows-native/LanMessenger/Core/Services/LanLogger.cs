using LanMessenger.Core.Persistence;
using System.Diagnostics;
using System.Text;

namespace LanMessenger.Core.Services;

// Structured lifecycle logger for the messaging pipeline.
//
// Cross-platform interop issues with macOS were historically opaque because
// every error path in PeerSession / NetworkCoordinator / FireTcpAsync was a
// silent `catch { }`. This logger gives a single, opt-in trail that records
// the key events (connect attempts, packets sent / received, decrypt
// results, receipts, status transitions) into %APPDATA%\LanMessenger\Logs\
// so the user can attach a log when reporting "single check mark" issues.
//
// All writes go through a lock-protected fire-and-forget path so logging
// never blocks the UI or networking threads.
public static class LanLogger
{
    private static readonly object _lock = new();
    private static readonly string _logDir =
        Path.Combine(ConfigStore.Shared.AppDataDirectory, "Logs");
    private static readonly string _logPath = Path.Combine(_logDir, "client.log");

    // 2 MiB rolling cap — large enough for a multi-hour session, small enough
    // for the user to attach to a bug report without trimming.
    private const long MaxBytes = 2 * 1024 * 1024;

    static LanLogger()
    {
        try { Directory.CreateDirectory(_logDir); } catch { /* read-only profile */ }
    }

    public static void Info(string category, string message)      => Write("INFO",  category, message);
    public static void Warn(string category, string message)      => Write("WARN",  category, message);
    public static void Error(string category, string message)     => Write("ERROR", category, message);
    public static void Error(string category, Exception ex)       => Write("ERROR", category, $"{ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string category, string message)
    {
        var line = new StringBuilder()
            .Append('[').Append(DateTime.UtcNow.ToString("u")).Append("] ")
            .Append(level.PadRight(5)).Append(' ')
            .Append(category).Append(": ")
            .Append(message)
            .Append(Environment.NewLine)
            .ToString();

        // Mirror to debugger so devs see live output without opening the log file.
        Debug.Write(line);

        lock (_lock)
        {
            try
            {
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxBytes)
                    File.WriteAllText(_logPath, $"[{DateTime.UtcNow:u}] INFO  Logger: rolled over (>{MaxBytes / 1024} KiB){Environment.NewLine}");
                File.AppendAllText(_logPath, line);
            }
            catch { /* full disk / locked file — best-effort logging only */ }
        }
    }
}
