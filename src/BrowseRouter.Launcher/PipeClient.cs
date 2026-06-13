using BrowseRouter.Core;
using BrowseRouter.Core.Ipc;
using BrowseRouter.Core.Json;
using BrowseRouter.Launcher.Interop;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace BrowseRouter.Launcher;

/// <summary>
/// Talks to the running Host daemon over the per-user pipe. One-shot: connect,
/// write the request, read the response, close. All timeouts are short — if the
/// Host doesn't answer quickly, we return false and the caller decides whether
/// to bootstrap a new Host or surface an error to the user.
/// </summary>
internal static class PipeClient
{
    /// <summary>
    /// Connect timeout when probing for a live Host (ms).
    /// </summary>
    public const int ConnectTimeoutMs = 200;

    /// <summary>
    /// Per-request read timeout once connected (ms).
    /// </summary>
    public const int ResponseTimeoutMs = 5_000;

    /// <summary>
    /// Send <paramref name="request"/> and read the response. Returns
    /// <c>(true, response)</c> on success or <c>(false, null)</c> if the pipe
    /// wasn't reachable in <see cref="ConnectTimeoutMs"/>.
    /// </summary>
    public static async Task<(bool Connected, OpenUrlResponse? Response)> SendAsync(
        OpenUrlRequest request,
        CancellationToken ct
    )
    {
        var pipeName = BuildPipeName(out _);
        try
        {
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeoutMs);
                try
                {
                    await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        throw;
                    return (false, null);
                }
                catch (TimeoutException)
                {
                    return (false, null);
                }
                catch (IOException)
                {
                    return (false, null);
                }

                using var rwCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                rwCts.CancelAfter(ResponseTimeoutMs);

                await PipeProtocol.WriteAsync(client, request, AppJsonContext.Default.OpenUrlRequest, rwCts.Token)
                    .ConfigureAwait(false);
                var rsp = await PipeProtocol.ReadAsync(client, AppJsonContext.Default.OpenUrlResponse, rwCts.Token)
                    .ConfigureAwait(false);
                return (true, rsp);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Pipe closed mid-conversation / Host crashed.
            return (false, null);
        }
    }

    /// <summary>
    /// Compose the per-user, per-session pipe name. Matches what Host computes.
    /// </summary>
    public static string BuildPipeName(out string diagnosticInfo)
    {
        var user = WindowsIdentity.GetCurrent().User;
        var sid = user?.Value ?? "anon";
        var sessionId = Kernel32.GetCurrentSessionId();

        diagnosticInfo = $"sid={sid}, sess={sessionId}";

        return PipeProtocol.BuildPipeName(Constants.PipeBaseName, sid, sessionId);
    }
}