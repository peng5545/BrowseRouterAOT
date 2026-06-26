using System.Runtime.InteropServices;

namespace BrowseRouter.Core.Interop;

/// <summary>
/// P/Invoke wrappers for <c>kernel32.dll</c> that are shared between the
/// Host and the Launcher. AOT-friendly via source generation.
/// </summary>
internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentProcessId();

    /// <summary>
    /// Pseudo-handle to the current process (<c>(HANDLE)-1</c>). Does NOT need to
    /// be closed — passing it to APIs like <c>GetGuiResources</c> /
    /// <c>GetProcessHandleCount</c> is cheaper than opening a real handle via
    /// <see cref="System.Diagnostics.Process"/> just to read counters.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    public static partial IntPtr GetCurrentProcess();

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
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return p.SessionId;
    }
}