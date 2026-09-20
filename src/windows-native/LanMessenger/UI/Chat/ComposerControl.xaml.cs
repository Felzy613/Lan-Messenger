using LanMessenger.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace LanMessenger.UI.Chat;

public sealed partial class ComposerControl : UserControl
{
    public event Action<string>?                 Send;
    public event Action<bool>?                   TypingChanged;
    public event Action?                         AttachRequested;
    /// Raised when Ctrl+V carried files or a bitmap rather than text.
    /// File *drops* are handled by ChatPage, which covers the whole thread.
    public event Action<IReadOnlyList<string>>?  FilesPasted;
    public event Action?                         ScreenshotRequested;
    /// Raised on Escape — ChatPage uses it to back out of edit/reply mode.
    public event Action?                         CancelRequested;

    private DateTime         _lastTypingSent = DateTime.MinValue;
    private bool             _typingActive;
    // Reuse a single DispatcherTimer instead of allocating one per keystroke.
    // TextChanged fires on every character; allocating + GC-ing a timer there
    // is a measurable contributor to UI hitches while typing.
    private DispatcherTimer? _typingIdleTimer;
    private bool             _isAttachmentPickerOpen;

    // Backing text for the composer input — used to save/restore per-conversation
    // drafts when the user switches peers without sending.
    public string Text
    {
        get => InputBox.Text;
        set => InputBox.Text = value;
    }

    public bool IsAttachmentPickerOpen
    {
        get => _isAttachmentPickerOpen;
        set
        {
            _isAttachmentPickerOpen = value;
            AttachBtn.IsEnabled = !value;
        }
    }

    /// True while the composer is editing an already-sent message. Swaps the
    /// send glyph for a checkmark so the button doesn't read as "send a new
    /// message" while it is actually saving an edit.
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            SendIcon.Glyph = value ? "\uE73E" : "\uE74A";   // Checkmark : Send
            ToolTipService.SetToolTip(SendBtn, value ? "Save edit" : "Send");
        }
    }

    public ComposerControl()
    {
        InitializeComponent();
        _typingIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _typingIdleTimer.Tick += OnTypingTimerTick;
        // Matches macOS: the send button starts disabled/gray until there's a draft.
        SendBtn.IsEnabled = false;
    }

    private void SendBtn_Click(object sender, RoutedEventArgs e) => DoSend();

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Escape backs out of edit or reply mode. Editing shows an already-sent
        // message in the composer, so there has to be a way out that doesn't send.
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CancelRequested?.Invoke();
            return;
        }
        if (e.Key != VirtualKey.Enter) return;

        var shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        e.Handled = true;  // always consume Enter so TextBox never inserts a literal newline

        if (shift)
        {
            // AcceptsReturn is false, so we insert the newline ourselves.
            var start = InputBox.SelectionStart;
            var selLen = InputBox.SelectionLength;
            var old = InputBox.Text;
            InputBox.Text = old[..start] + "\r\n" + old[(start + selLen)..];
            InputBox.SelectionStart = start + 2;
            return;
        }

        DoSend();
    }

    private void DoSend()
    {
        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _typingIdleTimer?.Stop();
        Send?.Invoke(text);
        InputBox.Text = "";
        SetTyping(false);
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Matches macOS: greyed out and inert until there's a non-blank draft.
        SendBtn.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);

        _typingIdleTimer?.Stop();
        if (InputBox.Text.Length > 0)
        {
            SetTyping(true);
            _typingIdleTimer?.Start();
        }
        else
        {
            SetTyping(false);
        }
    }

    private void OnTypingTimerTick(object? sender, object e)
    {
        _typingIdleTimer?.Stop();
        SetTyping(false);
    }

    private void SetTyping(bool active)
    {
        // Throttle to once per 1.5 s
        var now = DateTime.UtcNow;
        if (active == _typingActive && (now - _lastTypingSent).TotalSeconds < 1.5) return;
        _lastTypingSent = now;
        _typingActive   = active;
        TypingChanged?.Invoke(active);
    }

    private void AttachBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isAttachmentPickerOpen) return;
        AttachRequested?.Invoke();
    }

    /// <summary>
    /// Toggles the busy indicator on the screenshot button.  Called by ChatPage
    /// while the capture / file transfer enqueue is in flight so the user can
    /// see that the action is being processed.
    /// </summary>
    public bool IsScreenshotBusy
    {
        get => ScreenshotProgress.Visibility == Visibility.Visible;
        set
        {
            ScreenshotProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ScreenshotIcon.Visibility     = value ? Visibility.Collapsed : Visibility.Visible;
            ScreenshotBtn.IsEnabled       = !value;
        }
    }

    private void ScreenshotBtn_Click(object sender, RoutedEventArgs e)
    {
        if (IsScreenshotBusy) return;
        ScreenshotRequested?.Invoke();
    }

    /// <summary>
    /// Ctrl+V with files or a screenshot on the clipboard sends them as
    /// attachments instead of pasting a path (or nothing at all) into the draft.
    /// Plain text falls through to the TextBox untouched — see
    /// <see cref="ClipboardAttachments.Decide"/> for the precedence rules.
    /// </summary>
    private async void InputBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView view;
        try
        {
            view = Clipboard.GetContent();
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; let the TextBox try.
            LanLogger.Warn("Paste", $"clipboard read failed: {ex.Message}");
            return;
        }

        var hasStorageItems = view.Contains(StandardDataFormats.StorageItems);
        var hasBitmap       = view.Contains(StandardDataFormats.Bitmap);
        var hasText         = view.Contains(StandardDataFormats.Text);
        var action = ClipboardAttachments.Decide(hasStorageItems, hasBitmap, hasText);
        if (action == PasteAction.InsertText) return;

        // Must be set before the first await: once this handler yields, the
        // TextBox has already decided whether to insert the clipboard text.
        e.Handled = true;

        try
        {
            if (action == PasteAction.AttachFiles)
            {
                var items = await view.GetStorageItemsAsync();
                var paths = items.OfType<Windows.Storage.IStorageFile>()
                                 .Select(f => f.Path)
                                 .Where(p => !string.IsNullOrEmpty(p))
                                 .ToList();
                if (paths.Count > 0) FilesPasted?.Invoke(paths);
            }
            else
            {
                var reference = await view.GetBitmapAsync();
                var path = await PastedImageWriter.SavePngAsync(reference);
                if (path is not null) FilesPasted?.Invoke(new[] { path });
            }
        }
        catch (Exception ex)
        {
            LanLogger.Warn("Paste", $"pasting attachment failed: {ex.Message}");
        }
    }
}
