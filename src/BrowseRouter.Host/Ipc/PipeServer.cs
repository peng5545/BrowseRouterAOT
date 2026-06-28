using BrowseRouter.Core.Ipc;
using BrowseRouter.Core.Json;
using BrowseRouter.Host.Logging;
using System.Collections.Generic;
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
    Func<PipeRequest, PipeResponse> handler
) : IAsyncDisposable
{
    /// <summary>
    /// Per-request timeout. A client that connects but never finishes a request
    /// is killed after this duration (linked to the server-shutdown token).
    /// Prevents a stuck/malicious client from holding a handler task open
    /// indefinitely.
    /// </summary>
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _started; // 0 = not started, 1 = started. Interlocked gate.
    private readonly List<Task> _inFlight = [];

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

                // Detach: server is owned by HandleClientAsync from here on. Track
                // the task so DisposeAsync can drain in-flight handlers instead of
                // abandoning the request mid-flight (the launcher would then hang
                // waiting for a response that's never going to come).
                var owned = server;
                server = null;
                var task = HandleClientAsync(owned, ct);
                TrackInFlight(task);
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
                    // Token fired during the back-off — shut down.
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // CTS disposed during the back-off (DisposeAsync racing) — same exit.
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

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken serverCt)
    {
        // Per-request timeout linked to the server-shutdown token. If a client
        // connects and then sits silent (or the Host is so backed up that the
        // pipe write blocks), the request is aborted after PerRequestTimeout
        // and the stream is disposed.
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        reqCts.CancelAfter(PerRequestTimeout);
        var ct = reqCts.Token;
        try
        {
            await using (stream)
            {
                var req = await PipeProtocol.ReadAsync(stream, AppJsonContext.Default.PipeRequest, ct)
                    .ConfigureAwait(false);
                if (req is null)
                {
                    log.Warn("Client connected then disconnected before sending a request.");
                    return;
                }

                PipeResponse rsp;
                try
                {
                    rsp = handler(req);
                }
                catch (Exception ex)
                {
                    log.Error("Pipe handler threw", ex);
                    rsp = new OpenUrlResponse { Ok = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
                }

                await PipeProtocol.WriteAsync(stream, rsp, AppJsonContext.Default.PipeResponse, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (serverCt.IsCancellationRequested)
        {
            // Server is shutting down — quiet exit.
        }
        catch (OperationCanceledException)
        {
            // Per-request timeout fired. The launcher will see a dropped
            // connection and treat it as "Host unreachable", which is the
            // correct user-facing behaviour for a stuck client.
            log.Warn($"Pipe handler exceeded {PerRequestTimeout.TotalSeconds:F0}s; aborting.");
        }
        catch (Exception ex)
        {
            log.Warn($"Client handler: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TrackInFlight(Task inner)
    {
        lock (_inFlight)
        {
            _inFlight.Add(inner);
        }

        // Inline cleanup: await the handler, then remove from the in-flight
        // set. No ContinueWith → no extra ThreadPool work item per request.
        // Exceptions are already logged inside HandleClientAsync's own
        // try/catch chain; the outer catch is a backstop for truly unexpected
        // failures (e.g. a ThreadAbortException on older runtimes).
        _ = Wrap(inner);
        return;

        async Task Wrap(Task t)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch
            {
                /* already logged inside HandleClientAsync */
            }
            finally
            {
                lock (_inFlight)
                {
                    _inFlight.Remove(t);
                }
            }
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

        // Drain in-flight handlers so a Quit-during-click doesn't abandon a
        // request that the launcher is still waiting on. Give them a brief
        // grace period (longer than PerRequestTimeout ensures any timer
        // already fired and unblocked the handler) then move on regardless.
        Task[] toAwait;
        lock (_inFlight)
        {
            toAwait = [.. _inFlight];
        }

        if (toAwait.Length > 0)
        {
            try
            {
                await Task.WhenAll(toAwait).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
            catch
            {
                /* timeout or a handler swallowed OCE — proceed to teardown */
            }
        }

        _cts?.Dispose();
        _cts = null;
    }
}