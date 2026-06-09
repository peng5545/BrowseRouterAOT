using BrowseRouter.Core.Routing;
using BrowseRouter.Host.Logging;
using System.Diagnostics;

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
        var args = ArgsFormatter.Format(route.Browser.Args, route.Uri, route.RawUrl);

        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            // We don't want to inherit the Host's hidden-window state.
            CreateNoWindow = false,
            // Do NOT redirect stdio — the browser may run for hours and we'd hold its
            // pipes open. Detach completely.
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            log.Info($"Launched {route.BrowserName} (pid={proc?.Id}) for {route.Uri.OriginalString}; {route.Reason}");
            return proc?.Id;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to launch {route.BrowserName} at \"{path}\": {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}