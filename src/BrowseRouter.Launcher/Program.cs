using BrowseRouter.Core;
using BrowseRouter.Core.Ipc;
using BrowseRouter.Launcher.Interop;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
        int exitCode = 0;
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
        var source = ParentInfoCollector.Collect();
        Kernel32.ProcessIdToSessionId(Kernel32.GetCurrentProcessId(), out var sessionId);

        var req = new OpenUrlRequest
        {
            Url = url,
            SourceProcessName = source.ProcessName,
            SourceProcessPath = source.ProcessPath,
            SourceWindowTitle = source.WindowTitle,
            CallerPid = Environment.ProcessId,
            CallerSessionId = (int) sessionId
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
        if (connected && response is not null)
        {
            return response.Ok ? 0 : EmitFailure(url, response);
        }

        // Bootstrap and retry up to a few times, with brief sleeps. Caps total
        // worst-case at ~1s so a slow Host start still feels acceptable.
        if (!HostBootstrapper.TryStart())
        {
            FallbackOpener.NotifyUnreachable(url);
            return 4;
        }

        for (int i = 0; i < 5; i++)
        {
            try
            {
                await Task.Delay(150, ct.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

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
                                   throw new InvalidOperationException("Resolved Host exe has no directory component: " + exe),
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

    private static bool IsSubcommand(string arg) => arg.StartsWith('-') || arg.StartsWith('/');

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
                               """);
        }
        catch
        {
            /* no console */
        }
    }
}