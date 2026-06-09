using System.Diagnostics;
using System.IO;

namespace BrowseRouter.Launcher;

/// <summary>
/// Detached bootstrap of the Host daemon. The Launcher only ever
/// <em>requests</em> a start — it does not wait for the Host to be ready.
/// The Host's own SingleInstance mutex prevents duplicates if many launchers
/// race here at the same time (e.g. 10 clicks while the daemon was being
/// killed by AV).
/// </summary>
internal static class HostBootstrapper
{
    /// <summary>
    /// Locate <c>BrowseRouter.Host.exe</c> next to this Launcher's path.
    /// </summary>
    public static string? FindHostExe()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
            return null;
        var dir = Path.GetDirectoryName(self);
        if (string.IsNullOrEmpty(dir))
            return null;
        var candidate = Path.Combine(dir, "BrowseRouter.Host.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Fire-and-forget Process.Start of the Host. The new process is fully
    /// detached: no stdio redirect, no window. Returns false only when the exe
    /// cannot be located or Process.Start throws immediately.
    /// </summary>
    public static bool TryStart()
    {
        var exe = FindHostExe();
        if (exe is null)
            return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // FindHostExe() already verified the directory portion of `self`
                // is non-null (see the early-return in that method), so exe is a
                // rooted file path with a parent directory.
                WorkingDirectory = Path.GetDirectoryName(exe) ??
                                   throw new InvalidOperationException("Resolved Host exe has no directory component: " + exe),
            };
            psi.ArgumentList.Add("--host");
            using var _ = Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}