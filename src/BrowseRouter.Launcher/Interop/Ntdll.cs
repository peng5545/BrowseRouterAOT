using System.Runtime.InteropServices;

namespace BrowseRouter.Launcher.Interop;

/// <summary>
/// Minimal wrapper for <c>NtQueryInformationProcess</c>. The only field we need
/// from <c>PROCESS_BASIC_INFORMATION</c> is <c>InheritedFromUniqueProcessId</c>
/// — the PID of the process that spawned us. Available without elevation on
/// processes the caller can already query (i.e. owned by the same user).
/// </summary>
internal static partial class Ntdll
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    public const int ProcessBasicInformationClass = 0;

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        uint processInformationLength,
        out uint returnLength
    );
}