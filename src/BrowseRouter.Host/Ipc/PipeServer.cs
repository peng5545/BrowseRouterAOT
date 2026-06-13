using BrowseRouter.Core.Ipc;
using BrowseRouter.Core.Json;
using BrowseRouter.Host.Logging;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace BrowseRouter.Host.Ipc;

/// <summary>
/// Named-pipe server hosting one logical endpoint per (user, session). Accepts
/// many concurrent client connections via the
/// <see cref="NamedPipeServerStream.MaxAllowedServerInstances"/> pattern with an
/// accept-then-renew loop — the next listener is constructed immediately after a
/// client arrives, so connections are never refused while a previous client is
/// still being handled.
/// </summary>
internal sealed class PipeServer(
    string pipeName,
    FileLogger log,
    Func<OpenUrlRequest, CancellationToken, Task<OpenUrlResponse>> handler
) : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _started; // 0 = not started, 1 = started. Interlocked gate.

    /// <summary>
    /// Begin listening. Returns immediately; the loop runs in the background.
    /// Idempotent under concurrent calls — only the first wins.
    /// </summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        log.Info($@"Pipe server listening on \\.\pipe\{pipeName}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Each loop iteration creates a fresh server instance, awaits a client,
                // then hands the connected stream to a detached handler task and goes
                // straight back to constructing the next listener. This eliminates the
                // gap window where rapid clicks would otherwise see "no listener".
                server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // Detach: server is owned by HandleClientAsync from here on.
                var owned = server;
                server = null;
                _ = HandleClientAsync(owned, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Warn($"Pipe accept failed; will retry. {ex.GetType().Name}: {ex.Message}");
                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            finally
            {
                if (server != null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        try
        {
            await using (stream)
            {
                var req = await PipeProtocol.ReadAsync(stream, AppJsonContext.Default.OpenUrlRequest, ct)
                    .ConfigureAwait(false);
                if (req is null)
                {
                    log.Warn("Client connected then disconnected before sending a request.");
                    return;
                }

                OpenUrlResponse rsp;
                try
                {
                    rsp = await handler(req, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Error("Pipe handler threw", ex);
                    rsp = new OpenUrlResponse { Ok = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
                }

                await PipeProtocol.WriteAsync(stream, rsp, AppJsonContext.Default.OpenUrlResponse, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* shutting down */
        }
        catch (Exception ex)
        {
            log.Warn($"Client handler: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
            await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                /* swallowed */
            }

            _loop = null;
        }

        _cts?.Dispose();
        _cts = null;
    }
}