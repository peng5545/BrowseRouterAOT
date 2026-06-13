using System.Runtime.InteropServices;

namespace BrowseRouter.Host.Interop;

/// <summary>
/// P/Invoke wrappers for <c>kernel32.dll</c>. AOT-friendly via source generation.
/// </summary>
internal static partial class Kernel32
{
    /// <summary>
    /// Returns the module handle for the given module name (null = current process).
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    public static partial IntPtr GetModuleHandle(string? lpModuleName);

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
}