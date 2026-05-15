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
    public event Action<IReadOnlyList<string>>?  FilesDropped;

    private DateTime _lastTypingSent = DateTime.MinValue;
    private bool     _typingActive;

    public ComposerControl() => InitializeComponent();

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
        Send?.Invoke(text);
        InputBox.Text = "";
        SetTyping(false);
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SetTyping(InputBox.Text.Length > 0);
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

    private async void AttachBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Application.Current is not App app || app.MainWindow is null) return;
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(app.MainWindow));
            picker.FileTypeFilter.Add("*");

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            FilesDropped?.Invoke(files.Select(f => f.Path).ToList());
        }
        catch { /* picker cancelled or window handle unavailable */ }
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
