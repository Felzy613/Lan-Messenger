using System.Runtime.Versioning;
using LanMessenger.Core.Persistence;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LanMessenger.Core.Services;

/// <summary>
/// Writes a bitmap taken off the clipboard to a PNG on disk so it can go
/// through the ordinary attachment pipeline — the composer then calls
/// AppModel.SendFile() with the returned path, identical to a drag-drop or the
/// file picker.
///
/// Files land in the configured screenshot folder (default
/// %USERPROFILE%\Downloads\LAN Messenger Screenshots) rather than %TEMP%: chat
/// history stores the absolute path of every sent file, and a temp path is
/// swept out from under it, leaving "File no longer available" in the thread.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PastedImageWriter
{
    /// <summary>
    /// Decodes the clipboard bitmap and re-encodes it as PNG. Returns the
    /// absolute path, or null when the clipboard content could not be decoded.
    /// </summary>
    public static async Task<string?> SavePngAsync(RandomAccessStreamReference reference,
                                                   string? directory = null,
                                                   DateTime? now = null)
    {
        var dir = string.IsNullOrEmpty(directory) ? ConfigStore.Shared.ScreenshotDirectory : directory;
        try
        {
            using var inStream = await reference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(inStream);
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            Directory.CreateDirectory(dir);
            var folder = await StorageFolder.GetFolderFromPathAsync(dir);
            // GenerateUniqueName because the filename only has one-second
            // resolution: two pastes inside the same second would otherwise
            // overwrite each other, retroactively changing what an
            // already-sent bubble points at.
            var file = await folder.CreateFileAsync(
                ClipboardAttachments.PastedImageFileName(now ?? DateTime.Now),
                CreationCollisionOption.GenerateUniqueName);

            using (var outStream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    decoder.PixelWidth,
                    decoder.PixelHeight,
                    decoder.DpiX,
                    decoder.DpiY,
                    pixels.DetachPixelData());
                await encoder.FlushAsync();
            }

            LanLogger.Info("Paste", $"saved pasted image to {file.Path}");
            return file.Path;
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Paste", $"could not save pasted image: {ex.Message}");
            return null;
        }
    }
}
