using System.Runtime.InteropServices;

namespace LanMessenger.Core.Services;

/// <summary>
/// Reliable Win32 multi-file open dialog for unpackaged WinUI 3 apps.
///
/// The WinRT <c>FileOpenPicker</c> delegates to a shell-broker COM surrogate
/// that can throw <c>COMException 0x80004005</c> (E_FAIL) in unpackaged
/// processes — particularly after the window comes out of the system tray or
/// when the COM apartment hasn't been primed by prior shell interactions.
///
/// <c>GetOpenFileNameW</c> bypasses that broker entirely: it shows the
/// Explorer-style Open dialog in-process and is reliable in every Win32
/// process regardless of package identity.
///
/// Threading
/// ---------
/// Must be called from an STA thread.  WinUI 3's UI thread is STA, so
/// calling from a button-click handler or any UI-thread method is correct.
/// Do NOT wrap in <c>Task.Run</c> — <c>GetOpenFileName</c> pumps its own
/// message loop while visible, which keeps WinUI's dispatcher alive.
/// </summary>
internal static class Win32FileDialog
{
    private const int OFN_ALLOWMULTISELECT = 0x00000200;
    private const int OFN_EXPLORER         = 0x00080000;
    private const int OFN_FILEMUSTEXIST    = 0x00001000;
    private const int OFN_PATHMUSTEXIST    = 0x00000800;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int    lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public IntPtr  lpstrCustomFilter;
        public int     nMaxCustFilter;
        public int     nFilterIndex;
        public IntPtr  lpstrFile;           // manually-managed buffer
        public int     nMaxFile;
        public IntPtr  lpstrFileTitle;
        public int     nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int     Flags;
        public short   nFileOffset;
        public short   nFileExtension;
        public string? lpstrDefExt;
        public IntPtr  lCustData;
        public IntPtr  lpfnHook;
        public IntPtr  lpTemplateName;
        public IntPtr  pvReserved;
        public int     dwReserved;
        public int     FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true,
               EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    /// <summary>
    /// Displays a Win32 multi-file open dialog owned by <paramref name="ownerHwnd"/>.
    /// Blocks the calling STA thread while the dialog is visible (identical
    /// to any modal Win32 dialog — WinUI's dispatcher keeps pumping because
    /// <c>GetOpenFileName</c> runs its own inner message loop).
    /// Returns an empty list when the user cancels.
    /// </summary>
    public static IReadOnlyList<string> PickMultipleFiles(IntPtr ownerHwnd)
    {
        // 32 KiB of Unicode characters — enough for ~500 long paths.
        // Heap-allocated with Marshal so it's always pinned; freed in finally.
        const int bufferChars = 16_384;
        var buf = Marshal.AllocHGlobal(bufferChars * sizeof(char));
        try
        {
            Marshal.WriteInt16(buf, 0);   // start with an empty filename

            var ofn = new OPENFILENAME
            {
                lStructSize     = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner       = ownerHwnd,
                lpstrFile       = buf,
                nMaxFile        = bufferChars,
                lpstrTitle      = "Select Files to Send",
                lpstrFilter     = "All Files\0*.*\0\0",
                nFilterIndex    = 1,
                lpstrInitialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Flags           = OFN_ALLOWMULTISELECT | OFN_EXPLORER
                                | OFN_FILEMUSTEXIST    | OFN_PATHMUSTEXIST,
            };

            if (!GetOpenFileName(ref ofn))
            {
                var dialogError = CommDlgExtendedError();
                if (dialogError != 0)
                    LanLogger.Warn("Attachment", $"Win32 file picker failed with CommDlgExtendedError=0x{dialogError:X}.");
                return [];
            }

            return ParseFilenameBuffer(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // Multi-select result layout in the buffer:
    //   Single file  → "C:\dir\file.txt\0\0"
    //   Multiple     → "C:\dir\0file1.txt\0file2.txt\0\0"
    private static IReadOnlyList<string> ParseFilenameBuffer(IntPtr buf)
    {
        var segments = new List<string>();
        var ptr = buf;
        while (true)
        {
            var segment = Marshal.PtrToStringUni(ptr) ?? "";
            if (segment.Length == 0) break;
            segments.Add(segment);
            ptr = IntPtr.Add(ptr, (segment.Length + 1) * sizeof(char));
        }

        return segments.Count switch
        {
            0 => [],
            1 => [segments[0]],    // single selection — already a full path
            _ => segments.Skip(1).Select(f => Path.Combine(segments[0], f)).ToList(),
        };
    }
}
