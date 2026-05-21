using System.Diagnostics;
using LanMessenger.Core.Services;

namespace LanMessenger.UI.Chat;

// Extension-based media classification. Detection is intentionally client-side and
// extension-based — there is no protocol change. Incoming files arrive through the
// normal FileTransferService pipeline; the UI inspects the saved filename to decide
// whether to render an inline image, an inline video, or the generic file bubble.
public enum MediaKind
{
    Image,
    Video,
    Other,
}

public static class MediaTypes
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".bmp", ".tiff",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi",
    };

    public static MediaKind Classify(string path)
    {
        if (string.IsNullOrEmpty(path)) return MediaKind.Other;
        var ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext)) return MediaKind.Image;
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        return MediaKind.Other;
    }
}

// Reveals a file in Windows Explorer with the file selected.  All blocking work
// runs on a background Task so the UI thread cannot stall waiting for explorer.exe.
// Returns a human-readable error string on failure, or null on success.
public static class FileReveal
{
    /// <summary>
    /// Opens File Explorer and selects the given file. Returns null on success
    /// or an error message describing why the reveal failed.
    /// </summary>
    public static async Task<string?> RevealAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    LanLogger.Warn("FileReveal", $"file missing at {path}");
                    return "File not found — it may have been moved or deleted.";
                }

                // Quote the path so spaces and special characters survive the shell parse.
                // /select,"<path>" is the documented Explorer command-line for "show me this file".
                var psi = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                };
                var proc = Process.Start(psi);
                if (proc is null)
                {
                    LanLogger.Error("FileReveal", $"Process.Start returned null for {path}");
                    return "Could not launch File Explorer.";
                }
                // explorer.exe returns immediately; do not wait for exit.
                LanLogger.Info("FileReveal", $"revealed {path}");
                return null;
            }
            catch (Exception ex)
            {
                LanLogger.Error("FileReveal", $"reveal failed for {path}", ex);
                return $"Could not open file location: {ex.Message}";
            }
        }).ConfigureAwait(false);
    }
}
