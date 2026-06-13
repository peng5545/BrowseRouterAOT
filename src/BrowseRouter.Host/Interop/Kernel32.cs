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
    /// Convenience: session id of the current process (0 if the API fails).
    /// </summary>
    public static int GetCurrentSessionId()
    {
        return ProcessIdToSessionId(GetCurrentProcessId(), out var sid) ? (int) sid : 0;
    }
}