using System.Runtime.InteropServices;

namespace LanMessenger.Core.Services;

/// <summary>
/// Reliable Win32 file/folder dialogs for unpackaged WinUI 3 apps.
///
/// Every WinRT picker (<c>FileOpenPicker</c>, <c>FileSavePicker</c>,
/// <c>FolderPicker</c>) delegates to a shell-broker COM surrogate that can
/// throw <c>COMException 0x80004005</c> (E_FAIL) in unpackaged processes —
/// particularly after the window comes out of the system tray or when the COM
/// apartment hasn't been primed by prior shell interactions.  Because the
/// picker call sites are <c>async void</c> event handlers, that throw is
/// unhandled and takes the whole app down with the crash dialog.
///
/// <c>GetOpenFileNameW</c> / <c>GetSaveFileNameW</c> / <c>SHBrowseForFolderW</c>
/// bypass that broker entirely: they show their dialogs in-process and are
/// reliable in every Win32 process regardless of package identity.
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
    private const int OFN_OVERWRITEPROMPT  = 0x00000002;
    // Without this the dialog leaves the process CWD wherever the user browsed.
    private const int OFN_NOCHANGEDIR      = 0x00000008;

    private const uint BIF_RETURNONLYFSDIRS = 0x00000001;
    private const uint BIF_NEWDIALOGSTYLE   = 0x00000040;
    private const int  MAX_PATH             = 260;

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

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true,
               EntryPoint = "GetSaveFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    // Folder selection has no comdlg32 equivalent; SHBrowseForFolderW is the
    // in-process shell API (no broker) and needs no hand-rolled COM vtable.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr  hwndOwner;
        public IntPtr  pidlRoot;
        public IntPtr  pszDisplayName;
        public string? lpszTitle;
        public uint    ulFlags;
        public IntPtr  lpfn;
        public IntPtr  lParam;
        public int     iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHBrowseForFolderW")]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetPathFromIDListW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, IntPtr pszPath);

    // BIF_NEWDIALOGSTYLE requires the calling thread to be OLE-initialised.
    // OleInitialize returns S_FALSE when the thread already is (WinUI's UI
    // thread normally is, for drag-and-drop), and every successful call —
    // S_FALSE included — must be balanced by OleUninitialize.
    [DllImport("ole32.dll", EntryPoint = "OleInitialize")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll", EntryPoint = "OleUninitialize")]
    private static extern void OleUninitialize();

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

    /// <summary>
    /// Displays a Win32 single-file open dialog and returns the chosen absolute
    /// path, or null when the user cancels.  STA / UI-thread only.
    /// </summary>
    /// <param name="filterSpec">
    /// Semicolon-separated wildcards, e.g. "*.png;*.jpg".
    /// </param>
    public static string? PickSingleFile(IntPtr ownerHwnd, string title,
                                         string filterLabel, string filterSpec)
    {
        const int bufferChars = 4096;
        var buf = Marshal.AllocHGlobal(bufferChars * sizeof(char));
        try
        {
            Marshal.WriteInt16(buf, 0);

            var ofn = new OPENFILENAME
            {
                lStructSize  = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner    = ownerHwnd,
                lpstrFile    = buf,
                nMaxFile     = bufferChars,
                lpstrTitle   = title,
                lpstrFilter  = $"{filterLabel}\0{filterSpec}\0All Files\0*.*\0\0",
                nFilterIndex = 1,
                Flags        = OFN_EXPLORER | OFN_FILEMUSTEXIST
                             | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };

            if (!GetOpenFileName(ref ofn))
            {
                var dialogError = CommDlgExtendedError();
                if (dialogError != 0)
                    LanLogger.Warn("Contacts", $"Win32 open dialog failed with CommDlgExtendedError=0x{dialogError:X}.");
                return null;
            }

            var path = Marshal.PtrToStringUni(buf);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Displays a Win32 "Save As" dialog owned by <paramref name="ownerHwnd"/> and
    /// returns the chosen absolute path, or null when the user cancels.
    ///
    /// Unlike the WinRT <c>FileSavePicker</c>, this creates nothing on disk — the
    /// caller gets a path and writes it itself.  Same STA rule as
    /// <see cref="PickMultipleFiles"/>: call from the UI thread, never Task.Run.
    /// </summary>
    /// <param name="extension">Extension including the dot, e.g. ".zip".</param>
    public static string? SaveFile(IntPtr ownerHwnd,
                                   string  suggestedFileName,
                                   string  filterLabel,
                                   string  extension,
                                   string? initialDir = null,
                                   string? title      = null)
    {
        const int bufferChars = 4096;
        var buf = Marshal.AllocHGlobal(bufferChars * sizeof(char));
        try
        {
            // Seed the name edit box.  Truncate rather than overrun the buffer.
            var seed = suggestedFileName ?? "";
            if (seed.Length > bufferChars - 1) seed = seed[..(bufferChars - 1)];
            var seeded = new char[seed.Length + 1];
            seed.CopyTo(0, seeded, 0, seed.Length);
            seeded[seed.Length] = '\0';
            Marshal.Copy(seeded, 0, buf, seeded.Length);

            var ext = extension.TrimStart('.');
            var ofn = new OPENFILENAME
            {
                lStructSize     = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner       = ownerHwnd,
                lpstrFile       = buf,
                nMaxFile        = bufferChars,
                lpstrTitle      = title,
                lpstrFilter     = $"{filterLabel}\0*.{ext}\0\0",
                nFilterIndex    = 1,
                lpstrDefExt     = ext,            // appended when the user types no extension
                lpstrInitialDir = initialDir,
                Flags           = OFN_EXPLORER   | OFN_PATHMUSTEXIST
                                | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR,
            };

            if (!GetSaveFileName(ref ofn))
            {
                var dialogError = CommDlgExtendedError();
                if (dialogError != 0)
                    LanLogger.Warn("Settings", $"Win32 save dialog failed with CommDlgExtendedError=0x{dialogError:X}.");
                return null;
            }

            var path = Marshal.PtrToStringUni(buf);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Displays the shell folder-browse dialog and returns the chosen absolute
    /// path, or null when the user cancels (or the path exceeds MAX_PATH, which
    /// <c>SHGetPathFromIDListW</c> reports as failure rather than truncating).
    /// STA / UI-thread only, same as the other dialogs here.
    /// </summary>
    public static string? PickFolder(IntPtr ownerHwnd, string title)
    {
        var pathBuf = Marshal.AllocHGlobal(MAX_PATH * sizeof(char));
        // The shell writes the display name here unconditionally, so this is a
        // real MAX_PATH buffer rather than NULL.
        var nameBuf = Marshal.AllocHGlobal(MAX_PATH * sizeof(char));
        var pidl    = IntPtr.Zero;
        var oleHr   = OleInitialize(IntPtr.Zero);
        var oleOk   = oleHr >= 0;     // S_OK or S_FALSE; balance only on success
        try
        {
            Marshal.WriteInt16(nameBuf, 0);

            var bi = new BROWSEINFO
            {
                hwndOwner      = ownerHwnd,
                pidlRoot       = IntPtr.Zero,
                pszDisplayName = nameBuf,
                lpszTitle      = title,
                ulFlags        = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
            };

            pidl = SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero) return null;          // cancelled

            if (!SHGetPathFromIDList(pidl, pathBuf))
            {
                // Non-filesystem selection, or a path longer than MAX_PATH —
                // SHGetPathFromIDListW reports both as failure rather than truncating.
                LanLogger.Warn("Settings", "Folder dialog returned an item with no usable filesystem path.");
                return null;
            }

            var path = Marshal.PtrToStringUni(pathBuf);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            // The PIDL comes from the shell's IMalloc, which is the COM task allocator.
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            Marshal.FreeHGlobal(pathBuf);
            Marshal.FreeHGlobal(nameBuf);
            if (oleOk) OleUninitialize();
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
