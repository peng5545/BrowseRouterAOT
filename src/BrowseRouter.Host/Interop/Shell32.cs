using System.Runtime.InteropServices;

namespace BrowseRouter.Host.Interop;

/// <summary>
/// P/Invoke wrappers and constants for <c>shell32.dll</c> — tray icon API.
/// </summary>
internal static partial class Shell32
{
    // NIM_ values: Add/Modify/Delete/SetFocus/SetVersion
    public const uint NimAdd = 0x00000000;
    public const uint NimModify = 0x00000001;
    public const uint NimDelete = 0x00000002;
    public const uint NimSetversion = 0x00000004;

    // NIF_ flags select which NOTIFYICONDATA fields are valid.
    public const uint NifMessage = 0x00000001;
    public const uint NifIcon = 0x00000002;
    public const uint NifTip = 0x00000004;
    public const uint NifState = 0x00000008;
    public const uint NifInfo = 0x00000010;
    public const uint NifGuid = 0x00000020;
    public const uint NifRealtime = 0x00000040;
    public const uint NifShowtip = 0x00000080;

    // NIIF_ values control balloon icon.
    public const uint NiifNone = 0x00000000;
    public const uint NiifInfo = 0x00000001;
    public const uint NiifWarning = 0x00000002;
    public const uint NiifError = 0x00000003;
    public const uint NiifUser = 0x00000004;
    public const uint NiifLargeIcon = 0x00000020;

    /// <summary>
    /// Versions for NIM_SETVERSION. v4 enables the proper WM_CONTEXTMENU.
    /// </summary>
    public const uint NotifyiconVersion4 = 4;

    /// <summary>
    /// Layout matches the NOTIFYICONDATAW struct (Vista+). All string buffers are
    /// inlined fixed-size — required by Win32 marshalling. Use <c>cbSize = sizeof</c>
    /// at run-time to pin the version.
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

        public void SetInfo(string? value)
        {
            fixed (char* p = szInfo)
                CopyString(value, p, 256);
        }

        public void SetInfoTitle(string? value)
        {
            fixed (char* p = szInfoTitle)
                CopyString(value, p, 64);
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