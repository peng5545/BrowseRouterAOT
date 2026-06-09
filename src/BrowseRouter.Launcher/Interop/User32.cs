using System.Runtime.InteropServices;

namespace BrowseRouter.Launcher.Interop;

/// <summary>
/// Foreground-window helpers used as a fallback when the parent-process query is empty.
/// </summary>
internal static partial class User32
{
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(IntPtr hWnd);

    /// <summary>
    /// LibraryImport-friendly signature: <c>char*</c> avoids the SYSLIB1051 trap with
    /// <c>[Out] char[]</c>. Callers stackalloc / pin a buffer and pass a pointer.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static unsafe partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// Convenience: read the foreground window's title, or empty if none.
    /// </summary>
    public static unsafe string GetForegroundWindowTitle()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
            return string.Empty;
        var len = GetWindowTextLength(hWnd);
        if (len <= 0)
            return string.Empty;
        // +1 for trailing NUL written by Win32 (we strip it from the C# string anyway).
        Span<char> buf = len < 512 ? stackalloc char[len + 1] : new char[len + 1];
        int read;
        fixed (char* p = buf)
        {
            read = GetWindowText(hWnd, p, buf.Length);
        }

        return read <= 0 ? string.Empty : new string(buf[..read]);
    }

    /// <summary>
    /// MessageBox for last-ditch error reporting. AOT-friendly; we only use it
    /// when the Launcher cannot reach the Host AND cannot start one.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    /// <summary>
    /// Sentinel for <see cref="AllowSetForegroundWindow"/> meaning "any process".
    /// Defined as <c>(DWORD)-1</c> in the Windows SDK as <c>ASFW_ANY</c>.
    /// </summary>
    public const uint AsfwAny = 0xFFFFFFFF;

    /// <summary>
    /// Grant <paramref name="dwProcessId"/> the right to call SetForegroundWindow
    /// on its next attempt. The caller must currently be eligible (foreground
    /// process, or one started in response to user input). Pass
    /// <see cref="AsfwAny"/> to grant any process — useful when the eventual
    /// callee is reached through a chain we don't control (Host → browser shim →
    /// existing browser main process).
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint dwProcessId);
}