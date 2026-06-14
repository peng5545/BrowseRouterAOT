using BrowseRouter.Core;
using BrowseRouter.Core.Ipc;
using BrowseRouter.Core.UriUtil;
using BrowseRouter.Launcher.Interop;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreKernel32 = BrowseRouter.Core.Interop.Kernel32;

namespace BrowseRouter.Launcher;

/// <summary>
/// Tiny AOT entry point. Two modes:
/// <list type="bullet">
///   <item>A URL argument → gather source info, forward to Host, exit fast.</item>
///   <item>A subcommand → defer to Host.exe (which owns registration logic).</item>
/// </list>
/// On a fast path (Host already running) total wall-clock should be &lt; 50 ms.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Attach to parent console so --help is visible when run from a terminal.
        Kernel32.AttachConsole(Kernel32.AttachParentProcess);

        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        // Subcommands are delegated to Host.exe — the Launcher stays focused on
        // forwarding clicks. Keeps the binary minimal.
        if (IsSubcommand(args[0]))
        {
            return DelegateToHost(args);
        }

        // Process every argument that looks like a URL. Original BrowseRouter
        // allowed `BrowseRouter foo.com bar.com` to open both — preserved.
        var exitCode = 0;
        foreach (var raw in args)
        {
            var url = StripOptionDashes(raw);
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var rc = await ForwardOneAsync(url).ConfigureAwait(false);
            if (rc != 0)
                exitCode = rc;
        }

        return exitCode;
    }

    private static async Task<int> ForwardOneAsync(string url)
    {
        // Reject tokens that don't even look like http(s) URLs — local file
        // paths and other strings would otherwise slip through to the Host
        // and (depending on routing) trigger Process.Start on arbitrary inputs.
        // Bare hosts (no scheme) are also fine — UriFactory.TryParse promotes
        // them to https://.
        if (UriFactory.TryParse(url) is null)
        {
            try
            {
                await Console.Error.WriteLineAsync($"{Constants.AppName}: not a URL: {url}").ConfigureAwait(false);
            }
            catch
            {
                /* no console */
            }

            return 2;
        }

        var source = ParentInfoCollector.Collect();
        // Use the same helper the pipe name is built from, so the two are
        // guaranteed to agree (a ProcessIdToSessionId miss used to silently
        // produce session id 0, which collides with SYSTEM services in
        // session 0 and routed the click at the wrong host).
        var sessionId = CoreKernel32.GetCurrentSessionId();

        var req = new OpenUrlRequest
        {
            Url = url,
            SourceProcessName = source.ProcessName,
            SourceProcessPath = source.ProcessPath,
            SourceWindowTitle = source.WindowTitle,
            // SourcePid is the *originating* click process; null when the OS
            // denied the query (SYSTEM, elevated parents). LauncherPid is
            // ourselves — purely diagnostic.
            SourcePid = source.Pid ?? 0,
            LauncherPid = Environment.ProcessId,
            LauncherSessionId = sessionId
        };

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // We were spawned by the OS shell in direct response to the user's
        // click, so we currently hold the right to set the foreground window.
        // Broadcast that right to the system so the eventual browser process
        // (reached through Host → browser shim → existing browser main) can
        // bring its window to the front even when it was sitting in the
        // background. Without this, SetForegroundWindow inside the browser is
        // silently demoted to a taskbar flash. Re-issued before each pipe
        // attempt because the grant is one-shot (consumed by the first
        // SetForegroundWindow that succeeds).
        User32.AllowSetForegroundWindow(User32.AsfwAny);

        // First attempt: maybe Host is already up.
        var (connected, response) = await PipeClient.SendAsync(req, ct.Token).ConfigureAwait(false);
        switch (connected)
        {
            case true when response is not null:
                return response.Ok ? 0 : EmitFailure(url, response);
            // First connect failed — log the pipe name + SID/session so a misconfigured
            // SID or a session boundary issue is diagnosable from a single console line.
            case false:
                try
                {
                    await Console.Error
                        .WriteLineAsync($"{Constants.AppName}: pipe not reachable yet: {PipeClient.DescribePipe()}")
                        .ConfigureAwait(false);
                }
                catch
                {
                    /* no console */
                }

                break;
        }

        // Bootstrap a new Host and retry with exponential backoff. The previous
        // 5×150ms schedule capped at ~1s, but the Host has to load .NET AOT,
        // take the SingleInstance mutex, load/seed config, build tray + notifier
        // + watcher, and start the pipe server. On a cold start (or after AV
        // scan) that can easily exceed 750ms, so a user clicking a link would
        // see "could not contact its background service". 20 attempts with
        // 250→500ms backoff (cap ~6–7s) sits comfortably under the outer 10s
        // cancellation budget.
        if (!HostBootstrapper.TryStart())
        {
            FallbackOpener.NotifyUnreachable(url);
            return 4;
        }

        const int maxAttempts = 20;
        var delayMs = 250;
        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                await Task.Delay(delayMs, ct.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Outer timeout fired while we were sleeping — give up.
                break;
            }
            catch (ObjectDisposedException)
            {
                // CancellationTokenSource disposed mid-wait (process tearing down).
                break;
            }

            // Backoff: 250, 500, 500, 500, ... (cap at 500ms — the marginal
            // value of waiting longer once we've already crossed 1s is small,
            // and the outer 10s budget is the real ceiling).
            delayMs = Math.Min(delayMs * 2, 500);

            User32.AllowSetForegroundWindow(User32.AsfwAny);
            (connected, response) = await PipeClient.SendAsync(req, ct.Token).ConfigureAwait(false);
            if (connected && response is not null)
            {
                return response.Ok ? 0 : EmitFailure(url, response);
            }
        }

        FallbackOpener.NotifyUnreachable(url);
        return 4;
    }

    private static int EmitFailure(string url, OpenUrlResponse rsp)
    {
        try
        {
            Console.Error.WriteLine($"{Constants.AppName}: {rsp.Error ?? "unknown error"} ({url})");
        }
        catch
        {
            /* no console attached */
        }

        return 5;
    }

    /// <summary>
    /// Delegate <c>--register</c>/<c>--unregister</c>/<c>--auto</c>/etc. to Host.exe.
    /// </summary>
    private static int DelegateToHost(string[] args)
    {
        var exe = HostBootstrapper.FindHostExe();
        if (exe is null)
        {
            Console.Error.WriteLine("BrowseRouter.Host.exe not found next to this executable.");
            return 3;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false,
                // exe came from FindHostExe(), which only returns a path whose
                // parent directory exists. The throw is a type-system assertion
                // matching the one in HostBootstrapper.
                WorkingDirectory = Path.GetDirectoryName(exe) ??
                                   throw new InvalidOperationException(
                                       $"Resolved Host exe has no directory component: {exe}"),
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null)
                return 3;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to invoke Host: {ex.Message}");
            return 3;
        }
    }

    /// <summary>
    /// Whitelist of subcommands the Launcher will delegate to Host.exe. Anything
    /// not in this set is treated as a URL (or skipped if it doesn't look like
    /// one). <c>--host</c> is intentionally absent — invoking the Launcher with
    /// <c>--host</c> would otherwise spawn a daemon that never exits and block
    /// the caller's terminal. To start the daemon, run <c>BrowseRouter.Host.exe
    /// --host</c> directly.
    /// </summary>
    private static readonly HashSet<string> Subcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "-r", "--register",
        "-u", "--unregister",
        "--auto",
        "-h", "--help",
    };

    private static bool IsSubcommand(string arg) => Subcommands.Contains(arg);

    private static string StripOptionDashes(string arg) => arg.StartsWith('-') ? arg.TrimStart('-') : arg;

    private static void PrintHelp()
    {
        try
        {
            Console.WriteLine($"""
                               {Constants.AppName} (Launcher) — forwards URLs to the background BrowseRouter Host.

                               Usage:
                                 BrowseRouter.Launcher.exe <url> [<url> …]
                                     Forward each URL to the running Host (starts one if needed).

                                 BrowseRouter.Launcher.exe -r|--register   |  -u|--unregister  |  --auto  |  -h|--help
                                     Convenience — delegated to BrowseRouter.Host.exe.

                                 To start the daemon manually, run BrowseRouter.Host.exe --host directly.
                               """);
        }
        catch
        {
            /* no console */
        }
    }
}