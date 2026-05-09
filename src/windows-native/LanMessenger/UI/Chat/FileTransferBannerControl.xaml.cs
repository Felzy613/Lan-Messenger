using Microsoft.UI.Xaml.Controls;

namespace LanMessenger.UI.Chat;

public sealed partial class FileTransferBannerControl : UserControl
{
    public FileTransferBannerControl() => InitializeComponent();

    public void Update(string label, long bytes, long total)
    {
        LabelText.Text = label;
        BytesText.Text = $"{FormatBytes(bytes)} / {FormatBytes(total)}";
        Progress.Value = total > 0 ? (double)bytes / total : 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)             return $"{bytes} B";
        if (bytes < 1024 * 1024)      return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
