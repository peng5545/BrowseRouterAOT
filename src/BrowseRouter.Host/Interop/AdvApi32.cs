using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace BrowseRouter.Host.Interop;

/// <summary>
/// P/Invoke wrappers for <c>advapi32.dll</c> — the 6 registry APIs the Host actually
/// uses for browser registration and autostart. Avoiding the <c>Microsoft.Win32.Registry</c>
/// NuGet keeps the AOT binary ~100 KB smaller.
/// </summary>
internal static partial class AdvApi32
{
    public const uint KeyRead = 0x20019;
    public const uint KeyWrite = 0x20006;
    public const uint KeyAllAccess = 0xF003F;

    public const uint RegOptionNonVolatile = 0x00000000;

    public const uint RegSz = 1;
    public const uint RegExpandSz = 2;
    public const uint RegDword = 4;

    /// <summary>
    /// Predefined HKEY values (cast to IntPtr for x86/x64 portability).
    /// </summary>
    public static readonly IntPtr HkeyCurrentUser = new(unchecked((int) 0x80000001));

    /// <summary>
    /// SafeHandle for a registry HKEY — auto-closes on dispose.
    /// </summary>
    internal sealed class SafeRegistryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeRegistryHandle() : base(ownsHandle: true)
        {
        }

        public SafeRegistryHandle(IntPtr existing, bool ownsHandle) : base(ownsHandle)
        {
            SetHandle(existing);
        }

        protected override bool ReleaseHandle() => RegCloseKey(handle) == 0;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegCreateKeyEx(
        IntPtr hKey,
        string lpSubKey,
        uint reserved,
        string? lpClass,
        uint dwOptions,
        uint samDesired,
        IntPtr lpSecurityAttributes,
        out SafeRegistryHandle phkResult,
        out uint lpdwDisposition
    );

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegOpenKeyEx(
        IntPtr hKey,
        string lpSubKey,
        uint ulOptions,
        uint samDesired,
        out SafeRegistryHandle phkResult
    );

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegSetValueEx(
        SafeRegistryHandle hKey,
        string lpValueName,
        uint reserved,
        uint dwType,
        byte[] lpData,
        uint cbData
    );

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW")]
    public static partial int RegQueryValueEx(
        SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpValueName,
        IntPtr lpReserved,
        out uint lpType,
        [Out] byte[] lpData,
        ref uint lpcbData
    );

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegDeleteValue(SafeRegistryHandle hKey, string lpValueName);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteTreeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegDeleteTree(IntPtr hKey, string lpSubKey);

    [LibraryImport("advapi32.dll")]
    public static partial int RegCloseKey(IntPtr hKey);

    /// <summary>
    /// Write a string value (UTF-16 LE, NUL-terminated). Type may be
    /// <see cref="RegSz"/> or <see cref="RegExpandSz"/>.
    /// </summary>
    public static int SetStringValue(SafeRegistryHandle key, string name, string value, uint type = RegSz)
    {
        // RegSetValueExW takes a byte buffer; need to encode the string + trailing NUL.
        var bytes = System.Text.Encoding.Unicode.GetBytes(value + '\0');
        return RegSetValueEx(key, name, 0, type, bytes, (uint) bytes.Length);
    }

    /// <summary>
    /// Read a REG_SZ / REG_EXPAND_SZ value. Returns null if missing, wrong type,
    /// the buffer is too small, or any other failure. Buffer is fixed-size:
    /// 2048 bytes (1024 chars) is well over any realistic <c>%1</c> command line.
    /// </summary>
    public static string? ReadStringValue(SafeRegistryHandle key, string name)
    {
        var buf = new byte[2048];
        var cb = (uint) buf.Length;
        var rc = RegQueryValueEx(key, name, IntPtr.Zero, out var type, buf, ref cb);
        if (rc != 0 || (type != RegSz && type != RegExpandSz))
            return null;
        // cb holds bytes written including the trailing NUL; strip it.
        return cb < 2 ? string.Empty : System.Text.Encoding.Unicode.GetString(buf, 0, (int) (cb - 2));
    }

    /// <summary>
    /// Create or open a HKCU subkey for writing.
    /// </summary>
    public static SafeRegistryHandle CreateHkcuSubKey(string path)
    {
        var rc = RegCreateKeyEx(HkeyCurrentUser, path, 0, null, RegOptionNonVolatile, KeyAllAccess, IntPtr.Zero,
            out var key, out _);
        return rc != 0 ? throw new InvalidOperationException($"RegCreateKeyEx({path}) failed: {rc}") : key;
    }

    /// <summary>
    /// Delete an entire HKCU subtree; missing keys are non-fatal.
    /// </summary>
    public static void DeleteHkcuTreeQuiet(string path)
    {
        _ = RegDeleteTree(HkeyCurrentUser, path);
    }

    /// <summary>
    /// Delete a named value under HKCU\<paramref name="parentPath"/>; missing values are non-fatal.
    /// </summary>
    public static void DeleteHkcuValueQuiet(string parentPath, string valueName)
    {
        SafeRegistryHandle? key = null;

        try
        {
            if (RegOpenKeyEx(HkeyCurrentUser, parentPath, 0, KeyAllAccess, out key) != 0)
                return;

            _ = RegDeleteValue(key, valueName);
        }
        finally
        {
            key?.Dispose();
        }
    }
}