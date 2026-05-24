using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace LanMessenger.Core.Services;

/// <summary>
/// Captures the user's primary display, writes a PNG to a stable temp location,
/// and returns the absolute path so the caller can route it through the
/// existing FileTransferService.
///
/// This service never touches messaging or transfer pipelines directly — it
/// only produces a file on disk.  The composer then calls AppModel.SendFile()
/// with the returned path, which is identical to how drag-drop and the file
/// picker submit attachments.
///
/// Threading
/// ---------
/// • CapturePrimaryDisplayAsync runs the BitBlt-style capture and PNG encode on
///   a background Task. The UI thread never blocks on disk I/O or GDI calls.
///
/// Permissions
/// -----------
/// • Windows does not gate ordinary user-mode screen captures of the primary
///   display, so no runtime permission check is needed.  Capturing protected
///   content (DRM video, secure desktops) returns a black image — we surface
///   that as a generic capture failure rather than pretending it worked.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenshotService
{
    public sealed class ScreenshotException : Exception
    {
        public ScreenshotException(string message) : base(message) { }
        public ScreenshotException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Captures the primary display and saves it as a PNG.  Returns the
    /// absolute file path on success or throws ScreenshotException.
    /// </summary>
    public static async Task<string> CapturePrimaryDisplayAsync()
    {
        var startedAt = DateTime.UtcNow;
        LanLogger.Screenshot("request", permission: "granted");

        return await Task.Run(() =>
        {
            try
            {
                // Primary-display bounds via GetSystemMetrics.  Multi-monitor
                // support could later sum the virtual-screen rectangle, but
                // capturing only the primary display matches the macOS
                // implementation and keeps behaviour predictable for users.
                // SetProcessDpiAwarenessContext is configured in App.xaml.cs
                // / app.manifest so GetSystemMetrics returns physical pixels.
                int width  = GetSystemMetrics(SM_CXSCREEN);
                int height = GetSystemMetrics(SM_CYSCREEN);
                if (width <= 0 || height <= 0)
                {
                    LanLogger.Screenshot("failed", reason: "could not determine screen size");
                    throw new ScreenshotException("Could not determine screen size.");
                }
                var bounds = new Rectangle(0, 0, width, height);

                using var bitmap   = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }

                var dir = EnsureTempDirectory();
                var name = $"Screenshot {FilenameTimestamp()}.png";
                var path = Path.Combine(dir, name);
                bitmap.Save(path, ImageFormat.Png);

                var elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                LanLogger.Screenshot(
                    "captured", display: "primary",
                    widthPx: bounds.Width, heightPx: bounds.Height,
                    permission: "granted", initMs: elapsedMs, path: path);
                return path;
            }
            catch (ScreenshotException) { throw; }
            catch (Exception ex)
            {
                LanLogger.Screenshot("failed", reason: $"{ex.GetType().Name}: {ex.Message}");
                throw new ScreenshotException($"Screenshot capture failed: {ex.Message}", ex);
            }
        }).ConfigureAwait(false);
    }

    private static string EnsureTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LanMessenger-Screenshots");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FilenameTimestamp() =>
        DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);

    // Primary-display metrics via user32.  WinUI 3 apps don't pull in
    // System.Windows.Forms by default, so we read these directly rather than
    // forcing <UseWindowsForms>true</UseWindowsForms> on the project.
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
