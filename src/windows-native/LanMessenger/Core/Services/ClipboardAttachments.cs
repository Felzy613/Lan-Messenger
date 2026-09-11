namespace LanMessenger.Core.Services;

/// <summary>What a paste into the composer should do.</summary>
public enum PasteAction
{
    /// The clipboard carries files — send them as attachments.
    AttachFiles,
    /// The clipboard carries a bitmap and no text — write it to disk and send it.
    AttachImage,
    /// Ordinary text paste; hand back to the TextBox.
    InsertText,
}

/// <summary>
/// Decides what Ctrl+V in the composer means, and names the files produced when
/// a pasted bitmap has to be written to disk.
///
/// Deliberately free of WinRT types so the precedence rules can be compiled and
/// tested away from a Windows UI host — the mistake this guards against is
/// silent (every text paste turning into a file send), not a crash.
/// </summary>
public static class ClipboardAttachments
{
    /// <summary>
    /// Precedence: real files beat a bitmap, and a bitmap only wins when there
    /// is no text to paste instead.
    ///
    /// That last rule carries the weight. Copying from a browser, Word, or
    /// Outlook puts a bitmap on the clipboard *alongside* the text; treating
    /// those as image attachments would stop Ctrl+V pasting text at all from
    /// half the apps on the system.
    /// </summary>
    public static PasteAction Decide(bool hasStorageItems, bool hasBitmap, bool hasText)
    {
        if (hasStorageItems) return PasteAction.AttachFiles;
        if (hasBitmap && !hasText) return PasteAction.AttachImage;
        return PasteAction.InsertText;
    }

    /// <summary>
    /// <c>Pasted image 2026-09-11 at 14.05.09.png</c> — matches the screenshot
    /// naming convention, and avoids <c>:</c> and <c>\</c> so it is a legal
    /// Windows filename.
    /// </summary>
    public static string PastedImageFileName(DateTime now) =>
        $"Pasted image {TimestampComponent(now)}.png";

    public static string TimestampComponent(DateTime now) =>
        now.ToString("yyyy-MM-dd 'at' HH.mm.ss",
            System.Globalization.CultureInfo.InvariantCulture);
}
