using BrowseRouter.Core;
using BrowseRouter.Core.Ipc;
using BrowseRouter.Core.Routing;
using BrowseRouter.Host.Config;
using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Ipc;
using BrowseRouter.Host.Logging;
using BrowseRouter.Host.Notify;
using BrowseRouter.Host.Registration;
using BrowseRouter.Host.Routing;
using BrowseRouter.Host.Tray;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace BrowseRouter.Host;

/// <summary>
/// Host entry point. Acts as both a daemon (when invoked with <c>--host</c>) and
/// as the setup/teardown CLI for browser registration and autostart.
/// </summary>
internal static class Program
{
    // Menu command ids — kept stable for unit/regression testing.
    private const int CmdReload = 1001;
    private const int CmdOpenDir = 1002;
    private const int CmdSettings = 1003;
    private const int CmdOpenLog = 1004;
    private const int CmdQuit = 1099;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || EqualsAny(args[0], "--host", "-host"))
        {
            return await RunDaemonAsync().ConfigureAwait(false);
        }

        switch (args[0])
        {
            case "-h":
            case "--help":
                PrintHelp();
                return 0;

            case "-r":
            case "--register":
                return Register();

            case "-u":
            case "--unregister":
                return Unregister();

            case "--auto":
                return AutoToggle();

            default:
                await Console.Error.WriteLineAsync($"Unknown argument: {args[0]}").ConfigureAwait(false);
                PrintHelp();
                return 2;
        }
    }

    /// <summary>
    /// Run the long-lived daemon: pipe server, config watcher, tray icon.
    /// </summary>
    private static async Task<int> RunDaemonAsync()
    {
        using var instance = SingleInstance.TryAcquire();
        if (!instance.Acquired)
        {
            // Another Host is already serving this user+session. Exit quietly so
            // bootstrap races by the Launcher don't pile up duplicates.
            return 0;
        }

        var log = new FileLogger();
        log.Info($"{Constants.AppName} starting. pid={Environment.ProcessId}");

        // 1) Make sure %AppData% dir exists; seed default config if user has none.
        ConfigPaths.EnsureConfigDirectory();
        SeedTemplateIfMissing(log);

        // 2) Load config into the snapshot store.
        var store = new ConfigStore();
        var initial = ConfigLoader.TryLoad(ConfigPaths.ConfigFile, out var loadErr, log);
        if (initial is null)
        {
            log.Warn($"Initial config load failed; starting empty. {loadErr?.Message}");
        }
        else
        {
            store.Replace(initial);
            log.Info(
                $"Loaded config: {initial.Rules.Count} rules, {initial.SourceRules.Count} source rules, {initial.Filters.Count} filters, {initial.Browsers.Count} browsers.");
        }

        // 3) Apply log options from config.
        log.UpdateOptions(store.Current.Log);

        // 4) Tray icon + notifier (UI thread is owned by TrayIcon). The tray is
        // optional — when host.enableTrayIcon=false, the Host daemon still runs the
        // pipe server / config watcher, but exposes no UI. Exiting the daemon in
        // headless mode is then possible only via Ctrl+C here, or Task Manager.
        var exitSignal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enableTrayIcon = store.Current.Host.EnableTrayIcon;
        using var tray = enableTrayIcon ? new TrayIcon(log) : null;
        var notifier = new BalloonNotifier(tray, store.Current.Notify);
        if (tray is not null)
        {
            tray.Start();

            var trayWindowHandle = tray.WindowHandle;
            tray.OnTrayRightClick += () =>
            {
                var pick = ContextMenu.Show(trayWindowHandle, [
                    new ContextMenu.Item(CmdReload, "Reload config"),
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
                    HandleMenuCommand(pick, log, store, notifier, exitSignal);
                }
            };
            tray.OnMenuCommand += cmd => HandleMenuCommand(cmd, log, store, notifier, exitSignal);

            log.Info("Tray icon enabled.");
        }
        else
        {
            log.Info("Tray icon disabled by config (host.enableTrayIcon=false); running headless.");
        }

        // 5) Config watcher — debounced reload; re-apply log/notify options on reload.
        using var watcher = new ConfigWatcher(store, log, onReload: () =>
        {
            log.UpdateOptions(store.Current.Log);
            notifier.UpdateOptions(store.Current.Notify);
        });
        watcher.Start();

        // 6) Pipe server — the heart of the daemon.
        var launcher = new BrowserLauncher(log);
        var pipeName = PipeProtocol.BuildPipeName(store.Current.Host.PipeName ?? Constants.PipeBaseName,
            System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "anon",
            Kernel32.GetCurrentSessionId());

        var server = new PipeServer(pipeName, log, async (req, ct) =>
        {
            return await Task.Run(() => Handle(req, store, launcher, notifier, log), ct).ConfigureAwait(false);
        });

        // 7) Wait for shutdown (tray Quit or Ctrl+C from console invocation).
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitSignal.TrySetResult(0);
        };

        try
        {
            server.Start();
            var exitCode = await exitSignal.Task.ConfigureAwait(false);
            log.Info($"Daemon shutting down (exit={exitCode}).");
            return exitCode;
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolve a URL request to a route, launch the browser, balloon-notify.
    /// </summary>
    private static OpenUrlResponse Handle(
        OpenUrlRequest req,
        ConfigStore store,
        BrowserLauncher launcher,
        BalloonNotifier notifier,
        FileLogger log
    )
    {
        log.Info(
            $"Open: \"{req.Url}\" (from pid={req.CallerPid}, sess={req.CallerSessionId}, process={req.SourceProcessName ?? "?"}, title={req.SourceWindowTitle ?? "?"})");

        if (!RuleEngine.Resolve(store.Current, req.Url, req.SourceProcessName, req.SourceProcessPath,
                req.SourceWindowTitle, out var route, out var err,
                onFilterError: (name, ex) => log.Warn($"Filter '{name}' failed: {ex.Message}")))
        {
            log.Warn($"Resolve failed: {err.Reason} ({err.Detail}).");
            notifier.Notify("BrowseRouter", $"No browser for URL: {err.Detail}");
            return new OpenUrlResponse { Ok = false, Error = $"{err.Reason}: {err.Detail}" };
        }

        // NotNullWhen(true) on route guarantees non-null here.
        var pid = launcher.Launch(route);
        if (pid is null)
        {
            notifier.Notify("BrowseRouter", $"Failed to launch {route.BrowserName}");
            return new OpenUrlResponse { Ok = false, Error = $"Failed to launch {route.BrowserName}" };
        }

        var title = $"Opening {route.BrowserName}";
        var body = route.AppliedFilter is null
            ? route.Uri.OriginalString
            : $"[filtered: {route.AppliedFilter}] {route.Uri.OriginalString}";
        notifier.Notify(title, body);

        return new OpenUrlResponse
        {
            Ok = true,
            ChosenBrowser = route.BrowserName,
            ResolvedUrl = route.Uri.OriginalString,
            AppliedFilter = route.AppliedFilter,
            Reason = route.Reason
        };
    }

    private static void HandleMenuCommand(
        int cmd,
        FileLogger log,
        ConfigStore store,
        BalloonNotifier notifier,
        TaskCompletionSource<int> exitSignal
    )
    {
        switch (cmd)
        {
            case CmdReload:
                // Force an immediate, synchronous reload.
                var fresh = ConfigLoader.TryLoad(ConfigPaths.ConfigFile, out var err, log);
                if (fresh is null)
                {
                    log.Warn($"Manual reload failed: {err?.Message}");
                    notifier.Notify("Reload failed", err?.Message ?? "Unknown error");
                }
                else
                {
                    store.Replace(fresh);
                    log.UpdateOptions(fresh.Log);
                    notifier.UpdateOptions(fresh.Notify);
                    log.Info("Config reloaded via tray menu.");
                    notifier.Notify("Config reloaded", $"{fresh.Rules.Count} rules, {fresh.Browsers.Count} browsers.");
                }

                break;

            case CmdOpenDir:
                try
                {
                    using var _ = Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        ArgumentList = { "/select,", ConfigPaths.ConfigFile },
                        UseShellExecute = false
                    });
                }
                catch (Exception ex)
                {
                    log.Warn($"Open config folder failed: {ex.Message}");
                }

                break;

            case CmdOpenLog:
                // Best-effort: ensure the log directory exists (FileLogger may not
                // have rotated there yet) before handing it to explorer.
                try
                {
                    Directory.CreateDirectory(Constants.DefaultLogDirectory);
                    using var _ = Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = Constants.DefaultLogDirectory,
                        UseShellExecute = false
                    });
                }
                catch (Exception ex)
                {
                    log.Warn($"Open log folder failed: {ex.Message}");
                }

                break;

            case CmdSettings:
                SettingsLauncher.OpenDefaultApps();
                break;

            case CmdQuit:
                exitSignal.TrySetResult(0);
                break;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Subcommand handlers
    // ────────────────────────────────────────────────────────────────────────

    private static int Register()
    {
        var log = new FileLogger();
        var (hostExe, launcherExe) = ResolveExePaths();
        if (!File.Exists(launcherExe))
        {
            Console.Error.WriteLine($"Launcher not found at: {launcherExe}");
            Console.Error.WriteLine(
                "Make sure both BrowseRouter.Host.exe and BrowseRouter.Launcher.exe are in the same directory.");
            return 3;
        }

        new BrowserRegistration(log).Register(launcherExe);
        new AutoStart(log).Enable(hostExe);
        SettingsLauncher.OpenDefaultApps();
        Console.WriteLine(
            $"Registered. Please select '{Constants.AppName}' in the Default Apps page that just opened.");
        return 0;
    }

    private static int Unregister()
    {
        var log = new FileLogger();
        new BrowserRegistration(log).Unregister();
        new AutoStart(log).Disable();
        Console.WriteLine(
            $"Unregistered. A running {Constants.AppName} daemon (if any) will continue until you Quit it from the tray.");
        return 0;
    }

    private static int AutoToggle()
    {
        // "Auto" mode: if the registered URL command points to our current
        // Launcher path, treat the install as already registered → unregister.
        // Otherwise (un-registered or moved), register fresh.
        var (_, launcherExe) = ResolveExePaths();
        var expectedCmd = $"\"{launcherExe}\" \"%1\"";
        var existingCmd = ReadExistingOpenCommand();
        if (string.Equals(existingCmd, expectedCmd, StringComparison.OrdinalIgnoreCase))
            return Unregister();
        return Register();
    }

    private static string? ReadExistingOpenCommand()
    {
        // Probe HKCU\SOFTWARE\Classes\BrowseRouterAOTURL\shell\open\command's default value.
        // Done via the same advapi32 wrappers; lazy, single-shot read.
        const string keyPath = $@"SOFTWARE\Classes\{Constants.AppId}URL\shell\open\command";

        AdvApi32.SafeRegistryHandle? key = null;
        try
        {
            return AdvApi32.RegOpenKeyEx(AdvApi32.HkeyCurrentUser, keyPath, 0, AdvApi32.KeyRead, out key) != 0
                ? null
                :
                // Not implementing RegQueryValueEx here — we only need a yes/no decision and
                // anything non-trivial routes through Register/Unregister anyway. Return a
                // non-null sentinel so existence triggers Unregister().
                "<present>";
        }
        finally
        {
            key?.Dispose();
        }
    }

    /// <summary>
    /// Copy <c>browsers.template.json</c> next to the exe into the user config dir.
    /// </summary>
    private static void SeedTemplateIfMissing(FileLogger log)
    {
        if (File.Exists(ConfigPaths.ConfigFile))
            return;
        var template = ConfigPaths.TemplateConfigFile;
        if (!File.Exists(template))
        {
            log.Warn($"No template found at {template}; starting with empty config.");
            return;
        }

        try
        {
            File.Copy(template, ConfigPaths.ConfigFile, overwrite: false);
            log.Info($"Seeded user config from template: {ConfigPaths.ConfigFile}");
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to seed config: {ex.Message}");
        }
    }

    private static (string Host, string Launcher) ResolveExePaths()
    {
        var hostExe = Environment.ProcessPath ??
                      throw new InvalidOperationException(
                          "Environment.ProcessPath is null — cannot resolve self path.");
        var hostDir = Path.GetDirectoryName(hostExe) ??
                      throw new InvalidOperationException("Self path has no directory component: " + hostExe);
        var launcherExe = Path.Combine(hostDir, "BrowseRouter.Launcher.exe");
        return (hostExe, launcherExe);
    }

    private static bool EqualsAny(string s, params string[] options)
    {
        foreach (var o in options)
            if (string.Equals(s, o, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
                           {Constants.AppName} — routes URLs to a browser of your choice based on JSON rules.

                           Usage:
                             BrowseRouter.Host.exe                       Run as daemon (default).
                             BrowseRouter.Host.exe --host                Same as above (explicit).
                             BrowseRouter.Host.exe -r | --register       Register as a default browser candidate.
                             BrowseRouter.Host.exe -u | --unregister     Remove registration + autostart.
                             BrowseRouter.Host.exe --auto                Toggle: register if not present, else unregister.
                             BrowseRouter.Host.exe -h | --help           Show this help.

                           Config file: {Constants.DefaultConfigFilePath}
                           Logs:        {Constants.DefaultLogDirectory}
                           """);
    }
}