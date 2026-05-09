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
        if (shift) return;

        e.Handled = true;
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
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        FilesDropped?.Invoke(files.Select(f => f.Path).ToList());
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
