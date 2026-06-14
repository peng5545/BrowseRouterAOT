using System.Runtime.InteropServices;

namespace BrowseRouter.Host.Interop;

/// <summary>
/// Host-only P/Invoke wrappers for <c>kernel32.dll</c>. APIs shared with the
/// Launcher live in <c>BrowseRouter.Core.Interop.Kernel32</c>.
/// </summary>
internal static partial class Kernel32
{
    /// <summary>
    /// Returns the module handle for the given module name (null = current process).
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    public static partial IntPtr GetModuleHandle(string? lpModuleName);
}