using BrowseRouter.Launcher.Interop;
using System.Diagnostics;
using System.IO;

namespace BrowseRouter.Launcher;

/// <summary>
/// Best-effort capture of the calling process (the one that spawned the Launcher
/// because the user clicked a link there). Returns <c>null</c> values silently
/// when the OS denies access (typical for SYSTEM / elevated parents) — the Host
/// will then fall back to URL-only routing.
/// </summary>
internal static class ParentInfoCollector
{
    /// <summary>
    /// One snapshot of a calling-source. All fields may be null/empty.
    /// <see cref="Pid"/> is the OS-reported parent PID (the click originator);
    /// null when the OS denies the query (SYSTEM, elevated parents) or when
    /// the parent has already exited.
    /// </summary>
    internal sealed record SourceInfo(int? Pid, string? ProcessName, string? ProcessPath, string? WindowTitle);

    /// <summary>
    /// Gather as much as we can about whichever process is asking us to open a URL.
    /// </summary>
    public static SourceInfo Collect()
    {
        var parentPid = TryGetParentPid();
        string? processName = null;
        string? processPath = null;
        string? windowTitle = null;

        if (parentPid is { } pid && pid != 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int) pid);
                // Query the image path ONCE and derive both the full path and the
                // filename from it. Using two different sources (ProcessName for
                // the filename, MainModule for the path) could disagree on
                // weirdly-named processes like "node-script" or 32-char-truncated
                // names; one source keeps processName and processPath in lock-step.
                processPath = TryGetProcessImagePath(pid);
                processName = processPath is not null ? Path.GetFileName(processPath) : null;
                // MainWindowTitle is best-effort: many command-line / service parents
                // have none, in which case we'll fall back to the foreground window.
                windowTitle = SafeMainWindowTitle(proc);
            }
            catch
            {
                /* parent gone or denied */
            }
        }

        if (string.IsNullOrEmpty(windowTitle) && parentPid is { } p && p != 0)
        {
            // Foreground-window fallback covers cases where the parent is e.g. the
            // shell or an orphan launcher: the actual visible window is usually
            // the one the user just clicked in. BUT — we cross-check that the
            // foreground window actually belongs to the same process. Without
            // this, a stale foreground (user's browser, an unrelated window
            // that grabbed focus during the click) gets attributed to the
            // click source, which can then wrongly match a
            // WindowTitleContains rule and route to the wrong browser.
            if (TryGetForegroundWindowPid(out var fgPid) && fgPid == p)
            {
                windowTitle = User32.GetForegroundWindowTitle();
            }
        }

        return new SourceInfo((int?) parentPid, processName, processPath, windowTitle);
    }

    private static bool TryGetForegroundWindowPid(out uint pid)
    {
        pid = 0;
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;
        User32.GetWindowThreadProcessId(hwnd, out pid);
        return pid != 0;
    }

    private static uint? TryGetParentPid()
    {
        var self = Process.GetCurrentProcess();
        var pbi = default(Ntdll.ProcessBasicInformation);
        var rc = Ntdll.NtQueryInformationProcess(self.Handle, Ntdll.ProcessBasicInformationClass, ref pbi,
            (uint) System.Runtime.InteropServices.Marshal.SizeOf<Ntdll.ProcessBasicInformation>(), out _);
        if (rc != 0)
            return null;
        var pid = pbi.InheritedFromUniqueProcessId.ToInt64();
        return pid > 0 ? (uint) pid : null;
    }

    private static string? TryGetProcessImagePath(uint pid)
    {
        var h = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            unsafe
            {
                Span<char> buf = stackalloc char[1024];
                uint size = (uint) buf.Length;
                fixed (char* p = buf)
                {
                    if (!Kernel32.QueryFullProcessImageName(h, 0, p, ref size))
                        return null;
                }

                return new string(buf[..(int) size]);
            }
        }
        finally
        {
            Kernel32.CloseHandle(h);
        }
    }

    private static string? SafeMainWindowTitle(Process proc)
    {
        try
        {
            return proc.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }
}