using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text;
using LanMessenger.Core.Persistence;

namespace LanMessenger.Core.Services;

/// <summary>
/// Captures the primary display or a specific window, writes a PNG to a stable
/// temp location, and returns the absolute path so the caller can route it
/// through the existing FileTransferService.
///
/// This service never touches messaging or transfer pipelines directly — it
/// only produces a file on disk.  The composer then calls AppModel.SendFile()
/// with the returned path, which is identical to how drag-drop and the file
/// picker submit attachments.
///
/// Threading
/// ---------
/// • CapturePrimaryDisplayAsync / CaptureWindowAsync run the BitBlt/PrintWindow
///   capture and PNG encode on a background Task.  The UI thread never blocks.
/// • GetVisibleWindows is synchronous and may be called from any thread.
///
/// Permissions
/// -----------
/// • Windows does not gate ordinary user-mode screen captures of the primary
///   display, so no runtime permission check is needed.  Capturing protected
///   content (DRM video, secure desktops) returns a black image — we surface
///   that as a generic capture failure rather than pretending it worked.
/// • PrintWindow with PW_RENDERFULLCONTENT works on Windows 10 1903+ and
///   captures hardware-accelerated content.  On older builds or DRM-protected
///   apps the captured image may be blank.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenshotService
{
    public sealed class ScreenshotException : Exception
    {
        public ScreenshotException(string message) : base(message) { }
        public ScreenshotException(string message, Exception inner) : base(message, inner) { }
    }

    // Metadata for a single enumerated top-level window.
    public sealed class WindowInfo
    {
        public IntPtr Hwnd  { get; init; }
        public string Title { get; init; } = "";
    }

    // ── Primary-display capture (existing behaviour) ─────────────────────────

    /// <summary>
    /// Captures the primary display and saves it as a PNG.
    /// Returns the absolute file path on success or throws ScreenshotException.
    /// </summary>
    public static async Task<string> CapturePrimaryDisplayAsync()
    {
        var startedAt = DateTime.UtcNow;
        LanLogger.Screenshot("request", permission: "granted");

        return await Task.Run(() =>
        {
            try
            {
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
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size,
                                            CopyPixelOperation.SourceCopy);
                }

                var dir  = EnsureTempDirectory();
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

    // ── Per-window capture (new) ─────────────────────────────────────────────

    /// <summary>
    /// Captures a specific window using PrintWindow with PW_RENDERFULLCONTENT,
    /// which renders hardware-accelerated content (e.g. browsers, games) on
    /// Windows 10 1903+.  Saves as PNG and returns the file path.
    /// </summary>
    public static async Task<string> CaptureWindowAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return await CapturePrimaryDisplayAsync();

        LanLogger.Screenshot("request", display: "window", permission: "granted");
        return await Task.Run(() =>
        {
            try
            {
                if (!GetWindowRect(hwnd, out var rect))
                    throw new ScreenshotException("Could not determine window bounds.");

                var width  = rect.Right  - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0)
                    throw new ScreenshotException("Window has no visible area.");

                using var bitmap   = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var hdc = graphics.GetHdc();
                    try
                    {
                        // PW_RENDERFULLCONTENT (0x2) captures DirectX/GPU content
                        // that BitBlt misses on Windows 10 1903+.
                        if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
                            throw new ScreenshotException("Windows could not capture the selected window.");
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }

                var dir  = EnsureTempDirectory();
                var name = $"Screenshot {FilenameTimestamp()}.png";
                var path = Path.Combine(dir, name);
                bitmap.Save(path, ImageFormat.Png);

                LanLogger.Screenshot("captured", display: "window",
                    widthPx: width, heightPx: height, path: path);
                return path;
            }
            catch (ScreenshotException) { throw; }
            catch (Exception ex)
            {
                LanLogger.Screenshot("failed", reason: $"{ex.GetType().Name}: {ex.Message}");
                throw new ScreenshotException($"Window capture failed: {ex.Message}", ex);
            }
        }).ConfigureAwait(false);
    }

    // ── Window enumeration (new) ─────────────────────────────────────────────

    /// <summary>
    /// Returns all visible, non-minimised top-level windows with non-empty
    /// titles, filtered to exclude system chrome (taskbar, desktop, etc.).
    /// Safe to call from any thread.
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var results    = new List<WindowInfo>();
        var titleBuf   = new StringBuilder(512);
        var classBuf   = new StringBuilder(256);

        // Keep the delegate alive for the duration of the synchronous call.
        EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

            titleBuf.Clear();
            if (GetWindowText(hwnd, titleBuf, titleBuf.Capacity) == 0) return true;
            var title = titleBuf.ToString().Trim();
            if (string.IsNullOrEmpty(title)) return true;

            classBuf.Clear();
            GetClassName(hwnd, classBuf, classBuf.Capacity);
            if (ExcludedClassNames.Contains(classBuf.ToString())) return true;

            // Skip tiny tool windows / splash screens.
            if (!GetWindowRect(hwnd, out var r)) return true;
            if (r.Right - r.Left < 80 || r.Bottom - r.Top < 40) return true;

            results.Add(new WindowInfo { Hwnd = hwnd, Title = title });
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return results;
    }

    private static readonly HashSet<string> ExcludedClassNames = [
        "Shell_TrayWnd",
        "Progman",
        "WorkerW",
        "SHELLDLL_DefView",
        "DV2ControlHost",
        "Windows.UI.Core.CoreWindow",
    ];

    // ── Shared helpers ───────────────────────────────────────────────────────

    private static string EnsureTempDirectory()
    {
        var dir = ConfigStore.Shared.ScreenshotDirectory;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FilenameTimestamp() =>
        DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss",
            System.Globalization.CultureInfo.InvariantCulture);

    // ── P/Invoke — screen metrics ────────────────────────────────────────────
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // ── P/Invoke — window capture ────────────────────────────────────────────
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // ── P/Invoke — window enumeration ────────────────────────────────────────
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);
}
