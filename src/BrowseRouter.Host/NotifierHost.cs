using BrowseRouter.Core;
using BrowseRouter.Core.Ipc;
using BrowseRouter.Core.Routing;
using BrowseRouter.Host.Config;
using BrowseRouter.Host.Logging;
using BrowseRouter.Host.Notify;
using BrowseRouter.Host.Registration;
using BrowseRouter.Host.Routing;
using BrowseRouter.Host.Tray;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BrowseRouter.Host;

/// <summary>
/// Event-handler relay for the long-lived daemon. Owns the
/// <see cref="ToastNotifier"/> plus the other state the tray / config watcher
/// / pipe server callbacks need (<see cref="FileLogger"/>, <see cref="ConfigStore"/>,
/// <see cref="TaskCompletionSource{TResult}"/>, <see cref="BrowserLauncher"/>),
/// and exposes the methods those event sources subscribe to.
///
/// The events are subscribed as <strong>method-group references</strong>
/// (<c>tray.OnTrayRightClick += host.OnTrayRightClick;</c>) rather than as
/// lambdas capturing <c>notifier</c>. This avoids the "captured variable is
/// disposed in the outer scope" IDE warning that fires when an event handler
/// lambda closes over a <c>using var</c> declaration. Method groups don't
/// create closures, so the captured-by-using analysis never runs.
///
/// Lifetime: the host is created after <c>notifier</c> starts and lives for
/// the rest of <c>RunDaemonAsync</c>. The event sources (tray, watcher, pipe
/// server) are torn down by their own <c>using var</c> at the end of the
/// method, so all callbacks have completed before <c>notifier</c> is disposed
/// by the enclosing scope.
/// </summary>
internal sealed class NotifierHost(
    ToastNotifier notifier,
    FileLogger log,
    ConfigStore store,
    TaskCompletionSource<int> exitSignal,
    BrowserLauncher launcher
)
{
    // Menu command ids — kept stable for unit/regression testing.
    internal const int CmdReload = 1001;
    internal const int CmdOpenAppDir = 1005;
    internal const int CmdOpenDir = 1002;
    internal const int CmdOpenLog = 1003;
    internal const int CmdSettings = 1004;
    internal const int CmdQuit = 1099;

    // ─── Tray events ──────────────────────────────────────────────────────────

    /// <summary>
    /// Show the context menu when the user right-clicks the tray icon, then
    /// dispatch the chosen command back through <see cref="HandleMenuCommand"/>.
    /// </summary>
    public void OnTrayRightClick(IntPtr windowHandle)
    {
        var pick = ContextMenu.Show(windowHandle, [
            new ContextMenu.Item(CmdReload, "Reload config"),
            new ContextMenu.Item(CmdOpenAppDir, "Open app folder"),
            new ContextMenu.Item(CmdOpenDir, "Open config folder"),
            new ContextMenu.Item(CmdOpenLog, "Open log folder"),
            ContextMenu.Separator,
            new ContextMenu.Item(CmdSettings, "Open Default Apps settings"),
            ContextMenu.Separator,
            new ContextMenu.Item(CmdQuit, "Quit BrowseRouter")
        ]);
        if (pick != 0)
        {
            // TrackPopupMenu with TPM_RETURNCMD returns the id directly without
            // posting WM_COMMAND; dispatch ourselves.
            HandleMenuCommand(pick);
        }
    }

    public void HandleMenuCommand(int cmd)
    {
        switch (cmd)
        {
            case CmdReload:
                // Force an immediate, synchronous reload.
                var fresh = ConfigLoader.TryLoad(ConfigPaths.ConfigFile, out var err, log);
                if (fresh is null)
                {
                    log.Warn($"Manual reload failed: {err?.Message}");
                    notifier.Notify(err?.Message ?? "Unknown error");
                }
                else
                {
                    store.Replace(fresh);
                    log.UpdateOptions(fresh.Log);
                    notifier.UpdateOptions(fresh.Notify);
                    log.Info("Config reloaded via tray menu.");
                    notifier.Notify($"{fresh.Rules.Count} rules, {fresh.Browsers.Count} browsers loaded.");
                }

                break;

            case CmdOpenDir:
                OpenExplorer(ConfigPaths.ConfigFile, select: true, "Open config folder");
                break;

            case CmdOpenAppDir:
                // AppContext.BaseDirectory resolves to the directory of the
                // entry assembly — for the AOT-published Host.exe this is the
                // install directory alongside the Launcher.exe. Use it instead
                // of an environment variable so the path is correct even when
                // the user has moved the whole bundle to a custom location.
                OpenExplorer(AppContext.BaseDirectory, select: false, "Open app folder");
                break;

            case CmdOpenLog:
                // Best-effort: ensure the log directory exists (FileLogger may not
                // have rotated there yet) before handing it to explorer.
                try
                {
                    Directory.CreateDirectory(Constants.DefaultLogDirectory);
                }
                catch (Exception ex)
                {
                    log.Warn($"Create log dir failed: {ex.Message}");
                }

                OpenExplorer(Constants.DefaultLogDirectory, select: false, "Open log folder");
                break;

            case CmdSettings:
                SettingsLauncher.OpenDefaultApps();
                break;

            case CmdQuit:
                exitSignal.TrySetResult(0);
                break;
        }
    }

    // ─── Config watcher ───────────────────────────────────────────────────────

    /// <summary>
    /// Re-apply log + notifier options after a hot-reload. Subscribed as
    /// <c>onReload: host.OnConfigReload</c> — no lambda capture.
    /// </summary>
    public void OnConfigReload()
    {
        log.UpdateOptions(store.Current.Log);
        notifier.UpdateOptions(store.Current.Notify);
    }

    // ─── Menu helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Hand <paramref name="path"/> to Windows Explorer. <paramref name="select"/>
    /// picks the file when true (highlights the config file in its folder) or the
    /// folder itself when false. Failures are logged with <paramref name="opName"/>
    /// so the user can correlate a "nothing happened" click with a log line.
    /// </summary>
    private void OpenExplorer(string path, bool select, string opName)
    {
        try
        {
            // AppContext.BaseDirectory returns a path with a trailing directory
            // separator. explorer.exe treats paths ending in `\` inconsistently
            // across Windows versions — some interpret it as "the drive" or
            // "This PC" instead of the folder, so `D:\Program Files\App\`
            // silently opens the wrong window. Strip the trailing separator(s)
            // so the path is in canonical form before being handed off.
            var normalized = path.TrimEnd('\\', '/');

            var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
            if (select)
            {
                // /select, must be passed as its own argv element followed by
                // the path as the next argv element. End-to-end test against
                // a real fixture folder whose name contains a space showed
                // the *combined* form (`/select,"<path>"` as a single arg)
                // parses incorrectly: explorer treats the whole token as a
                // malformed switch and silently falls back to opening
                // "Documents" (its default location when no valid target is
                // resolved). Splitting into two args lets explorer's switch
                // parser take the standard "/select, <path>" code path, which
                // opens the parent folder with the file highlighted.
                psi.ArgumentList.Add("/select,");
            }

            psi.ArgumentList.Add(normalized);
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            log.Warn($"{opName} failed: {ex.Message}");
        }
    }

    // ─── Pipe server ──────────────────────────────────────────────────────────

    /// <summary>
    /// Per-request handler for the named-pipe server. Returns a
    /// <see cref="Task{TResult}"/> so the pipe server can await it on its
    /// own thread; the actual work runs on the thread pool via
    /// <see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>.
    /// </summary>
    public Task<OpenUrlResponse> HandlePipeRequest(OpenUrlRequest req, CancellationToken ct)
    {
        return Task.Run(() => ResolveAndLaunch(req), ct);
    }

    /// <summary>
    /// Resolve a URL request to a route, launch the browser, toast-notify.
    /// Runs on a thread-pool worker (called via <see cref="HandlePipeRequest"/>);
    /// touches the notifier's thread indirectly (it's all thread-safe).
    /// </summary>
    private OpenUrlResponse ResolveAndLaunch(OpenUrlRequest req)
    {
        // Two PIDs are interesting in a click: the originating process (the one
        // the user actually clicked a link in) and the Launcher (the small AOT
        // shim the OS spawned). Log both distinctly so an audit reader can tell
        // them apart.
        log.Info(
            $"Open: \"{req.Url}\" (source pid={req.SourcePid}, launcher pid={req.LauncherPid}, launcher sess={req.LauncherSessionId}, process={req.SourceProcessName ?? "?"}, title={req.SourceWindowTitle ?? "?"})");

        if (!RuleEngine.Resolve(store.Current, req.Url, req.SourceProcessName, req.SourceProcessPath,
                req.SourceWindowTitle, out var route, out var err,
                onFilterError: (name, ex) => log.Warn($"Filter '{name}' failed: {ex.Message}")))
        {
            log.Warn($"Resolve failed: {err.Reason} ({err.Detail}).");
            notifier.Notify($"No browser for URL: {err.Detail}");
            return new OpenUrlResponse { Ok = false, Error = $"{err.Reason}: {err.Detail}" };
        }

        // NotNullWhen(true) on route guarantees non-null here.
        var pid = launcher.Launch(route);
        if (pid is null)
        {
            notifier.Notify($"Failed to launch {route.BrowserName}");
            return new OpenUrlResponse { Ok = false, Error = $"Failed to launch {route.BrowserName}" };
        }

        // Body format: "{browser}->{url}" — no whitespace anywhere. The toast
        // renders the body with DT_WORD_BREAK, which breaks at whitespace; by
        // keeping the arrow adjacent to both the browser name and the URL, the
        // entire "browser->url" is treated as one word, so long URLs only wrap
        // at the very end of the string (or onto a new line as a whole) —
        // never splitting the "{browser}->" prefix from the URL itself.
        var body = route.AppliedFilter is null
            ? $"{route.BrowserName}->{route.Uri.OriginalString}"
            : $"{route.BrowserName}->{route.Uri.OriginalString} [filter: {route.AppliedFilter}]";
        notifier.Notify(body);

        return new OpenUrlResponse
        {
            Ok = true,
            ChosenBrowser = route.BrowserName,
            ResolvedUrl = route.Uri.OriginalString,
            AppliedFilter = route.AppliedFilter,
            Reason = route.Reason
        };
    }
}