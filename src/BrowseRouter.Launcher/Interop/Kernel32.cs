using System.Runtime.InteropServices;

namespace BrowseRouter.Launcher.Interop;

/// <summary>
/// P/Invoke wrappers for <c>kernel32.dll</c>. AOT-friendly.
/// </summary>
internal static partial class Kernel32
{
    public const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        char* lpExeName,
        ref uint lpdwSize
    );

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", EntryPoint = "ProcessIdToSessionId")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    /// <summary>
    /// Convenience: session id of the current process. Prefers the kernel
    /// API but falls back to <see cref="System.Diagnostics.Process.SessionId"/>
    /// when <c>ProcessIdToSessionId</c> returns 0 — that happens on rare
    /// locked-down configurations and previously produced cross-session
    /// collisions with services running in session 0.
    /// </summary>
    public static int GetCurrentSessionId()
    {
        if (ProcessIdToSessionId(GetCurrentProcessId(), out var sid) && sid != 0)
            return (int) sid;
        return System.Diagnostics.Process.GetCurrentProcess().SessionId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(int dwProcessId);

    /// <summary>
    /// Sentinel for <c>AttachConsole(ATTACH_PARENT_PROCESS)</c>.
    /// </summary>
    public const int AttachParentProcess = -1;
}