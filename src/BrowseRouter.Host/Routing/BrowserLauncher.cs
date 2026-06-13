using BrowseRouter.Core.Routing;
using BrowseRouter.Host.Logging;
using System.Diagnostics;
using System.IO;

namespace BrowseRouter.Host.Routing;

/// <summary>
/// Launches the chosen browser process. Uses <see cref="ProcessStartInfo.ArgumentList"/>
/// (not raw <see cref="ProcessStartInfo.Arguments"/>) so each argument is escaped by
/// the framework — no homegrown quoting, no command-injection foot-guns when a
/// rewritten URL contains spaces or quotes.
/// </summary>
internal sealed class BrowserLauncher(FileLogger log)
{
    /// <summary>
    /// Spawn the resolved browser. Returns the child PID, or <c>null</c> on failure
    /// (failure is logged; caller decides whether to surface a notification).
    /// </summary>
    /// <remarks>
    /// Returns the PID instead of the <see cref="Process"/> so the caller cannot
    /// accidentally hold onto a <c>Process</c> whose OS handle we have to release
    /// here. <c>Process.Dispose()</c> only closes the parent's handle — the child
    /// keeps running — so disposing eagerly is safe and stops the parent from
    /// leaking one OS process handle per URL click.
    /// </remarks>
    public int? Launch(RouteResult route)
    {
        var path = Environment.ExpandEnvironmentVariables(route.Browser.Path);
        // Strip surrounding quotes (a hand-edited config can have them) and
        // normalise to a full path so the log line shows the resolved location
        // even when the config used a relative or env-var-laden path.
        path = path.Trim().Trim('"');
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                // Resolve relative paths against the install directory
                // (AppContext.BaseDirectory), NOT the current working directory.
                // The daemon's CWD can change during the session (someone runs
                // a different program in the same console, the user opens a
                // file dialog, etc.) — we want the configured browser to keep
                // resolving to the same physical exe the user originally
                // pointed at.
                path = Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(path, AppContext.BaseDirectory);
            }
            catch
            {
                /* leave as-is on malformed input */
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
        };
        ArgsFormatter.Format(route.Browser.Args, route.Uri, route.RawUrl, psi.ArgumentList);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                // Process.Start returns null when the OS refuses to create the
                // process without throwing (UAC intercept, blocked executable).
                log.Warn($"Process.Start returned null for {route.BrowserName} at \"{path}\"; {route.Reason}");
                return null;
            }

            // Process.Id can throw InvalidOperationException if the child has
            // already exited between Start and the Id query (rare but real for
            // command-line utilities that print a version and exit). Treat
            // that as "launched successfully but already gone" — not an error.
            int pid;
            try
            {
                pid = proc.Id;
            }
            catch (InvalidOperationException)
            {
                log.Info(
                    $"Launched {route.BrowserName} for {route.Uri.OriginalString}; {route.Reason} (child already exited)");
                return null;
            }

            log.Info($"Launched {route.BrowserName} (pid={pid}) for {route.Uri.OriginalString}; {route.Reason}");
            return pid;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to launch {route.BrowserName} at \"{path}\": {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}