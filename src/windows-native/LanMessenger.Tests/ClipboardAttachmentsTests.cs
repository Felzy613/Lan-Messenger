using LanMessenger.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Guards what Ctrl+V in the composer means. See ClipboardAttachments.cs.
//
// The failure mode this protects against is silent rather than loud: get the
// precedence wrong and the composer stops pasting text from every app that also
// puts a bitmap on the clipboard (browsers, Word, Outlook), sending a screenshot
// of the copied content instead. Nothing throws; the user just can't paste.
[TestClass]
public class ClipboardAttachmentsTests
{
    [TestMethod]
    public void FilesWinOverEverything()
    {
        Assert.AreEqual(PasteAction.AttachFiles,
            ClipboardAttachments.Decide(hasStorageItems: true, hasBitmap: true, hasText: true));
        Assert.AreEqual(PasteAction.AttachFiles,
            ClipboardAttachments.Decide(hasStorageItems: true, hasBitmap: false, hasText: false));
    }

    // Copy in Explorer's preview pane, Snipping Tool, Print Screen: a bitmap
    // and nothing else.
    [TestMethod]
    public void BitmapAloneIsAttached()
    {
        Assert.AreEqual(PasteAction.AttachImage,
            ClipboardAttachments.Decide(hasStorageItems: false, hasBitmap: true, hasText: false));
    }

    // The regression this rule exists for.
    [TestMethod]
    public void BitmapAlongsideTextStaysATextPaste()
    {
        Assert.AreEqual(PasteAction.InsertText,
            ClipboardAttachments.Decide(hasStorageItems: false, hasBitmap: true, hasText: true));
    }

    [TestMethod]
    public void PlainTextIsATextPaste()
    {
        Assert.AreEqual(PasteAction.InsertText,
            ClipboardAttachments.Decide(hasStorageItems: false, hasBitmap: false, hasText: true));
    }

    // Nothing usable on the clipboard — hand back to the TextBox rather than
    // swallowing the keystroke.
    [TestMethod]
    public void EmptyClipboardIsATextPaste()
    {
        Assert.AreEqual(PasteAction.InsertText,
            ClipboardAttachments.Decide(hasStorageItems: false, hasBitmap: false, hasText: false));
    }

    // The timestamp goes into a filename, so it must avoid the characters
    // Windows rejects in one.
    [TestMethod]
    public void PastedImageFileNameIsFilenameSafe()
    {
        var name = ClipboardAttachments.PastedImageFileName(new DateTime(2026, 9, 11, 14, 5, 9));
        Assert.AreEqual("Pasted image 2026-09-11 at 14.05.09.png", name);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            Assert.IsFalse(name.Contains(c), $"filename contains invalid char '{c}'");
        }
    }

    // Same second, same name: the writer relies on CreationCollisionOption
    // .GenerateUniqueName to keep a second paste from overwriting the first,
    // which would retroactively change what an already-sent bubble points at.
    [TestMethod]
    public void PastedImageFileNameHasOnlySecondResolution()
    {
        var a = ClipboardAttachments.PastedImageFileName(new DateTime(2026, 9, 11, 14, 5, 9, 100));
        var b = ClipboardAttachments.PastedImageFileName(new DateTime(2026, 9, 11, 14, 5, 9, 900));
        Assert.AreEqual(a, b);
    }
}
