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
    public event Action<IReadOnlyList<string>>?  FilesDropped;
    public event Action?                         ScreenshotRequested;

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

    public ComposerControl()
    {
        InitializeComponent();
        _typingIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _typingIdleTimer.Tick += OnTypingTimerTick;
    }

    private void SendBtn_Click(object sender, RoutedEventArgs e) => DoSend();

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.OfType<Windows.Storage.IStorageFile>().Select(f => f.Path).ToList();
        if (paths.Count > 0) FilesDropped?.Invoke(paths);
    }
}
