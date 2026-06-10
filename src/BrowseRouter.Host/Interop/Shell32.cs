using System.Runtime.InteropServices;

namespace BrowseRouter.Host.Interop;

/// <summary>
/// P/Invoke wrappers and constants for <c>shell32.dll</c> — tray icon API.
/// Balloon-specific fields and flags are intentionally absent: notifications go
/// through the self-drawn toast popup (see <c>BrowseRouter.Host.Notify.ToastNotifier</c>),
/// not <c>Shell_NotifyIcon</c>'s NIF_INFO path. The struct layout itself is kept
/// at the V3 size so <c>Shell_NotifyIcon</c>'s <c>cbSize</c> validation still
/// accepts our calls — the unused balloon fields stay as struct padding.
/// </summary>
internal static partial class Shell32
{
    // NIM_ values used: Add/Delete/SetVersion. NIM_MODIFY is not used — the
    // tray icon is created once and never updated in place.
    public const uint NimAdd = 0x00000000;
    public const uint NimDelete = 0x00000002;
    public const uint NimSetversion = 0x00000004;

    // NIF_ flags — only the ones the tray icon actually needs.
    public const uint NifMessage = 0x00000001;
    public const uint NifIcon = 0x00000002;
    public const uint NifTip = 0x00000004;
    public const uint NifShowtip = 0x00000080;

    /// <summary>
    /// Versions for NIM_SETVERSION. v4 enables the proper WM_CONTEXTMENU.
    /// </summary>
    public const uint NotifyiconVersion4 = 4;

    /// <summary>
    /// Layout matches the NOTIFYICONDATAW struct (Vista+). All string buffers are
    /// inlined fixed-size — required by Win32 marshalling. Use <c>cbSize = sizeof</c>
    /// at run-time to pin the version. The balloon fields (szInfo, szInfoTitle,
    /// dwInfoFlags, hBalloonIcon) are retained so <c>cbSize</c> matches the
    /// V3 size the OS validates against, but the host never sets them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    internal unsafe struct Notifyicondataw
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        public fixed char szTip[128];

        public uint dwState;
        public uint dwStateMask;

        public fixed char szInfo[256];

        public uint uVersion;

        public fixed char szInfoTitle[64];

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;

        public void SetTip(string? value)
        {
            fixed (char* p = szTip)
                CopyString(value, p, 128);
        }

        private static void CopyString(string? source, char* dest, int destChars)
        {
            var span = new Span<char>(dest, destChars);
            span.Clear();
            if (string.IsNullOrEmpty(source))
                return;
            source.AsSpan().TryCopyTo(span[..(destChars - 1)]);
        }
    }

    /// <summary>
    /// Wraps Shell_NotifyIconW. Uses source-gen marshalling via fixed-size buffers.
    /// </summary>
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIcon(uint dwMessage, ref Notifyicondataw pnid);
}