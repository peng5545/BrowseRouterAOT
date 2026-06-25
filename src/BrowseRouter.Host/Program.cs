using BrowseRouter.Core;
using BrowseRouter.Core.Ipc;
using BrowseRouter.Core.Json;
using BrowseRouter.Host.Config;
using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Ipc;
using BrowseRouter.Host.Logging;
using BrowseRouter.Host.Notify;
using BrowseRouter.Host.Registration;
using BrowseRouter.Host.Routing;
using BrowseRouter.Host.Tray;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using CoreKernel32 = BrowseRouter.Core.Interop.Kernel32;

namespace BrowseRouter.Host;

/// <summary>
/// Host entry point. Acts as both a daemon (when invoked with <c>--host</c>) and
/// as the setup/teardown CLI for browser registration and autostart.
/// </summary>
internal static class Program
{
    // Menu command ids live on NotifierHost (NotifierHost.CmdReload etc.) —
    // this entry point no longer holds any daemon-event state of its own.

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || EqualsAny(args[0], "--host", "-host"))
        {
            return await RunDaemonAsync().ConfigureAwait(false);
        }

        // Case-insensitive flag matching so `--GC`, `--Gc`, `--gc` all work —
        // the Launcher forwards the arg verbatim and users don't expect a
        // specific casing for a diagnostic toggle.
        var cmd = args[0];
        if (EqualsAny(cmd, "-h", "--help"))
        {
            PrintHelp();
            return 0;
        }

        if (EqualsAny(cmd, "-r", "--register"))
            return Register();
        if (EqualsAny(cmd, "-u", "--unregister"))
            return Unregister();
        if (EqualsAny(cmd, "--auto"))
            return AutoToggle();
        if (EqualsAny(cmd, "--gc", "-gc"))
            return await RunGcAsync().ConfigureAwait(false);

        await Console.Error.WriteLineAsync($"Unknown argument: {cmd}").ConfigureAwait(false);
        PrintHelp();
        return 2;
    }

    /// <summary>
    /// Run the long-lived daemon: pipe server, config watcher, tray icon.
    /// </summary>
    private static async Task<int> RunDaemonAsync()
    {
        using var instance = SingleInstance.TryAcquire();
        if (!instance.Acquired)
        {
            // Another Host is already serving this user+session. Bootstrap
            // races by the Launcher are normal and silent; a *user* who
            // manually launched a second copy gets a hint via stderr so they
            // know why nothing visibly happened.
            FileLogger.TryLogConsole(
                $"{Constants.AppName} is already running in this user session; this process will exit.");
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

        // 4) Toast notifier + tray icon + event-host relay. The tray is optional
        // — when host.enableTrayIcon=false, the Host daemon still runs the pipe
        // server / config watcher, but exposes no UI. Exiting the daemon in
        // headless mode is then possible only via Ctrl+C here, or Task Manager.
        //
        // The toast notifier runs on its own STA message-loop thread, so it is
        // independent of the tray icon and works in headless mode too.
        //
        // Event subscriptions use method-group references on `host` (not
        // lambdas capturing `notifier`). That sidesteps the "captured variable
        // is disposed in the outer scope" IDE warning that fires when an
        // event-handler lambda closes over a `using var` — method groups don't
        // create closures.
        var exitSignal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enableTrayIcon = store.Current.Host.EnableTrayIcon;
        using var notifier = new ToastNotifier(log, store.Current.Notify);
        notifier.Start();
        // If the very first config load failed, force a one-shot toast so the
        // user knows why every subsequent click is landing on the "no rule
        // matched" notification. We bypass notify.enabled because that's
        // exactly the config the user just couldn't load.
        if (initial is null)
        {
            notifier.ForceNotify($"Config is invalid: {loadErr?.Message ?? "unknown error"}");
        }

        var launcher = new BrowserLauncher(log);
        var host = new NotifierHost(notifier, log, store, exitSignal, launcher);

        using var tray = enableTrayIcon ? new TrayIcon(log) : null;
        if (tray is not null)
        {
            tray.Start();
            tray.OnTrayRightClick += host.OnTrayRightClick;
            tray.OnMenuCommand += host.HandleMenuCommand;
            log.Info("Tray icon enabled.");
        }
        else
        {
            log.Info("Tray icon disabled by config (host.enableTrayIcon=false); running headless.");
        }

        // 5) Config watcher — debounced reload; re-apply log/notify options on reload.
        using var watcher = new ConfigWatcher(store, log, onReload: host.OnConfigReload);
        watcher.Start();

        // 6) Pipe server — the heart of the daemon. The pipe name is intentionally
        // not user-overridable: the Launcher always looks for the default
        // Constants.PipeBaseName, so a custom name here would silently break click
        // routing. Pipe scoping by SID + session is still per-user/per-session.
        var pipeName = PipeProtocol.BuildPipeName(Constants.PipeBaseName,
            WindowsIdentity.GetCurrent().User?.Value ?? "anon", CoreKernel32.GetCurrentSessionId());

        var server = new PipeServer(pipeName, log, host.HandlePipeRequest);

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

    // ────────────────────────────────────────────────────────────────────────
    // Subcommand handlers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--gc</c> client mode: this process is NOT the daemon — it connects to
    /// the already-running Host daemon over the per-user pipe, sends a
    /// <see cref="GcRequest"/>, and prints the diagnostics report the daemon
    /// returns. The daemon has already logged the same report to its log file;
    /// we only mirror it to the console here.
    ///
    /// <para>
    /// This is the path the Launcher reaches via <c>BrowseRouter.Launcher.exe
    /// --gc</c> (delegated through <c>Host.exe --gc</c>). It intentionally does
    /// NOT spin up a daemon: triggering GC in a freshly-started process would
    /// measure the wrong process and miss the real leak signal (which is the
    /// long-lived daemon's accumulated GDI/handle/thread counts).
    /// </para>
    /// </summary>
    private static async Task<int> RunGcAsync()
    {
        var pipeName = PipeProtocol.BuildPipeName(Constants.PipeBaseName,
            WindowsIdentity.GetCurrent().User?.Value ?? "anon", CoreKernel32.GetCurrentSessionId());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;

        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            try
            {
                await client.ConnectAsync(2000, ct).ConfigureAwait(false);
            }
            catch
            {
                await Console.Error.WriteLineAsync(
                        $@"{Constants.AppName}: daemon is not running on \\.\pipe\{pipeName}; cannot trigger GC.")
                    .ConfigureAwait(false);
                await Console.Error.WriteLineAsync("Start it first with 'BrowseRouter.Host.exe --host'.")
                    .ConfigureAwait(false);
                return 6;
            }

            try
            {
                await PipeProtocol.WriteAsync(client, new GcRequest(), AppJsonContext.Default.PipeRequest, ct)
                    .ConfigureAwait(false);
                var rsp = await PipeProtocol.ReadAsync(client, AppJsonContext.Default.PipeResponse, ct)
                    .ConfigureAwait(false);

                if (rsp is not GcResponse gc)
                {
                    await Console.Error.WriteLineAsync(
                            $"{Constants.AppName}: unexpected response type from daemon: {rsp?.GetType().Name ?? "<null>"}.")
                        .ConfigureAwait(false);
                    return 7;
                }

                Console.WriteLine(gc.Report ?? (gc.Ok ? "(no report)" : "(daemon reported failure)"));
                return gc.Ok ? 0 : 8;
            }
            catch (Exception ex)
            {
                await Console.Error
                    .WriteLineAsync($"{Constants.AppName}: --gc failed: {ex.GetType().Name}: {ex.Message}")
                    .ConfigureAwait(false);
                return 8;
            }
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static int Register()
    {
        using var log = new FileLogger();
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
        using var log = new FileLogger();
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
        //
        // We compare the registered command's launcher segment against the
        // current launcher path (case-insensitive) rather than reconstructing
        // the exact "expected" command string. That way, an install at a path
        // containing embedded quotes (or any other legitimate variation) still
        // matches its own previous registration.
        var (_, launcherExe) = ResolveExePaths();
        var existingCmd = ReadExistingOpenCommand();
        return IsCommandForOurLauncher(existingCmd, launcherExe) ? Unregister() : Register();
    }

    /// <summary>
    /// True when <paramref name="cmd"/> is a registered open command whose
    /// launcher segment is the same file as <paramref name="launcherExe"/>.
    /// "Same file" is matched by the command's first quoted token OR its
    /// first whitespace-delimited token (whichever the registry value uses)
    /// and compared case-insensitively against the launcher path's filename
    /// and full-path suffix.
    /// </summary>
    private static bool IsCommandForOurLauncher(string? cmd, string launcherExe)
    {
        if (string.IsNullOrWhiteSpace(cmd) || string.IsNullOrEmpty(launcherExe))
            return false;

        // First token — either "path" (quoted) or path (unquoted up to space).
        var span = cmd.AsSpan().TrimStart();
        ReadOnlySpan<char> firstToken;
        if (span.Length > 0 && span[0] == '"')
        {
            var end = span[1..].IndexOf('"');
            if (end < 0)
                return false;
            firstToken = span[1..(end + 1)];
        }
        else
        {
            var end = span.IndexOf(' ');
            firstToken = end < 0 ? span : span[..end];
        }

        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        return firstToken.Equals(launcherExe, cmp) || firstToken.Equals(Path.GetFileName(launcherExe), cmp);
    }

    private static string? ReadExistingOpenCommand()
    {
        // Probe HKCU\SOFTWARE\Classes\{AppId}URL\shell\open\command's default value.
        // Returned verbatim so AutoToggle() can compare it against the expected command
        // — if a previous install pointed at a different launcher path (moved binary,
        // partial cleanup, etc.), the comparison falls through to Register() instead of
        // mistakenly Unregister()ing based on mere key existence.
        const string keyPath = $@"SOFTWARE\Classes\{Constants.AppId}URL\shell\open\command";

        AdvApi32.SafeRegistryHandle? key = null;
        try
        {
            return AdvApi32.RegOpenKeyEx(AdvApi32.HkeyCurrentUser, keyPath, 0, AdvApi32.KeyRead, out key) != 0
                ? null
                : AdvApi32.ReadStringValue(key, string.Empty);
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
            // Also write to stderr so a user who runs Host from a console
            // (--host in a terminal, or via the Register subcommand) sees the
            // warning immediately — without this they'd only see a stray
            // "no rule matched" toast on every click and have to dig into
            // the log file to find out why.
            log.Warn($"No template found at {template}; starting with empty config.");
            try
            {
                Console.Error.WriteLine(
                    $"{Constants.AppName}: no template at {template}; every click will hit NoRuleMatched.");
            }
            catch
            {
                /* no console attached */
            }

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
                      throw new InvalidOperationException($"Self path has no directory component: {hostExe}");
        var launcherExe = Path.Combine(hostDir, "BrowseRouter.Launcher.exe");
        return (hostExe, launcherExe);
    }

    private static bool EqualsAny(string s, params string[] options) =>
        options.Any(o => string.Equals(s, o, StringComparison.OrdinalIgnoreCase));

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
                             BrowseRouter.Host.exe --gc                  Force a GC in the running daemon and print diagnostics
                                                                         (heap, handles, threads, GDI/USER objects, thread pool).
                             BrowseRouter.Host.exe -h | --help           Show this help.

                           Config file: {Constants.DefaultConfigFilePath}
                           Logs:        {Constants.DefaultLogDirectory}
                           """);
    }
}