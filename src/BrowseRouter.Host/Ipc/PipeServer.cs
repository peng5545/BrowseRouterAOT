using BrowseRouter.Core.Ipc;
using BrowseRouter.Core.Json;
using BrowseRouter.Host.Logging;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading;

namespace BrowseRouter.Host.Ipc;

/// <summary>
/// Named-pipe server hosting one logical endpoint per (user, session). Uses a
/// producer-consumer pattern with two dedicated threads:
/// <list type="bullet">
/// <item><b>Accept thread</b> - creates a <see cref="NamedPipeServerStream"/> with
/// <see cref="PipeOptions.None"/> (synchronous), blocks on
/// <see cref="NamedPipeServerStream.WaitForConnection()"/>, then enqueues the
/// connected stream and immediately creates the next listener.</item>
/// <item><b>Handler thread</b> - dequeues a connected stream, reads the request,
/// invokes the handler, writes the response, and disposes the stream - all
/// synchronously.</item>
/// </list>
///
/// This design avoids <see cref="System.Threading.ThreadPool"/> and overlapped I/O
/// entirely. In NativeAOT, async pipe I/O (<see cref="PipeOptions.Asynchronous"/>)
/// creates IOCP completion threads per operation that are destroyed after an idle
/// timeout; their OS thread handles persist (held by managed <c>Thread</c> objects
/// in Gen 2) and are only released by a full GC - causing a +2 handle/click leak.
/// By using synchronous I/O on dedicated threads, no threads are created or
/// destroyed per click.
/// </summary>
internal sealed class PipeServer(string pipeName, FileLogger log, Func<PipeRequest, PipeResponse> handler) : IDisposable
{
    // Created at construction so it is never null; AcceptLoop/HandlerLoop can use
    // _cts.Token directly without null-forgiving (!) or null-conditional (?.).
    private readonly CancellationTokenSource _cts = new();
    private int _disposed; // idempotency guard for Dispose (Interlocked)
    private Thread? _acceptThread;
    private Thread? _handlerThread;
    private readonly BlockingCollection<NamedPipeServerStream> _queue = new();
    private NamedPipeServerStream? _currentServer; // for shutdown: dispose to unblock WaitForConnection
    private int _started;

    /// <summary>
    /// Begin listening. Returns immediately; both threads run in the background.
    /// Idempotent under concurrent calls - only the first wins.
    /// </summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;
        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "BrowseRouterAOT.PipeAccept"
        };
        _handlerThread = new Thread(HandlerLoop)
        {
            IsBackground = true,
            Name = "BrowseRouterAOT.PipeHandler"
        };
        _acceptThread.Start();
        _handlerThread.Start();
        log.Info($@"Pipe server listening on \\.\pipe\{pipeName}");
    }

    /// <summary>
    /// Accept loop: create a synchronous pipe, wait for a client, enqueue the
    /// connected stream, then loop back to create the next listener. Connections
    /// are never refused while a previous client is still being handled - the
    /// handler thread processes from the queue independently.
    /// Create a synchronous pipe, block until a client connects, then enqueue
    /// the connected stream for the handler thread. Returns true on success,
    /// false if cancelled before enqueue. On any failure the pipe is disposed
    /// in the finally block - ownership is either transferred to the queue
    /// (success) or cleaned up (failure).
    /// </summary>
    private bool CreateConnectAndEnqueue(CancellationToken ct)
    {
        NamedPipeServerStream? server = null;
        try
        {
            server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.None);
            Volatile.Write(ref _currentServer, server);
            server.WaitForConnection();
            Volatile.Write(ref _currentServer, null);

            if (ct.IsCancellationRequested)
                return false;

            _queue.Add(server, ct);
            server = null; // ownership transferred to queue
            return true;
        }
        finally
        {
            Volatile.Write(ref _currentServer, null);
            server?.Dispose();
        }
    }

    /// <summary>
    /// Accept loop: create a synchronous pipe, wait for a client, enqueue the
    /// connected stream, then loop back to create the next listener. Connections
    /// are never refused while a previous client is still being handled - the
    /// handler thread processes from the queue independently.
    /// </summary>
    private void AcceptLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!CreateConnectAndEnqueue(ct))
                    break; // cancelled
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding was called during shutdown
                break;
            }
            catch (Exception ex)
            {
                log.Warn($"Pipe accept failed; will retry. {ex.GetType().Name}: {ex.Message}");
                try
                {
                    ct.WaitHandle.WaitOne(250);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    /// <summary>
    /// Handler loop: dequeue a connected stream, read the request, invoke the
    /// handler, write the response, dispose the stream. All synchronous - no
    /// async state machine, no ThreadPool, no IOCP.
    /// </summary>
    private void HandlerLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? stream;
            try
            {
                stream = _queue.Take(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding was called and queue is empty
                break;
            }

            try
            {
                using (stream)
                {
                    var req = PipeProtocol.Read(stream, AppJsonContext.Default.PipeRequest);
                    if (req is null)
                    {
                        log.Warn("Client connected then disconnected before sending a request.");
                        continue;
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

                    PipeProtocol.Write(stream, rsp, AppJsonContext.Default.PipeResponse);
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Client handler: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Shut down: cancel the CTS, dispose the current pipe (to unblock the accept
    /// thread's WaitForConnection), drain the queue, and join both threads.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        _cts.Cancel();

        // Unblock + join the accept thread BEFORE marking the queue complete:
        // the accept thread is the only producer, so once it has exited no more
        // items can be enqueued. See UnblockAcceptThread for why a dummy client
        // connection is needed instead of just closing the pipe handle.
        UnblockAcceptThread();

        _queue.CompleteAdding();

        // Drain any queued streams that the handler has not processed yet.
        while (_queue.TryTake(out var leftover))
        {
            try
            {
                leftover.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        // Handler thread should exit after the queue is drained and CompleteAdding was called.
        try
        {
            _handlerThread?.Join(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // ignored
        }

        _queue.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// Wake the accept thread out of a blocking
    /// <see cref="NamedPipeServerStream.WaitForConnection"/> by connecting a
    /// throwaway client. A synchronous pipe (<see cref="PipeOptions.None"/>) blocks
    /// inside the kernel (<c>ConnectNamedPipe</c>) and is NOT reliably woken by
    /// closing the handle from another thread, so disposing <c>_currentServer</c>
    /// alone would leave the accept thread stuck until the Join timeout - a
    /// multi-second exit delay every time. The dummy connection makes
    /// <c>ConnectNamedPipe</c> return; the accept loop then observes the
    /// cancellation and exits on its own. The loop retries because the accept
    /// thread may create a fresh server instance between our connect attempts.
    /// </summary>
    private void UnblockAcceptThread()
    {
        if (_acceptThread is not { IsAlive: true } t)
            return;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (t.IsAlive && DateTime.UtcNow < deadline)
        {
            try
            {
                using var dummy = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                dummy.Connect(150);
            }
            catch
            {
                // No server instance is listening right now (the accept thread is
                // between iterations); the next iteration catches up once it
                // creates the next listener.
            }

            if (t.Join(100))
                return;
        }

        // Last resort: the accept thread still won't exit (e.g. the dummy
        // connection never landed). Close its pipe handle so the OS resource is
        // released; the background thread is then abandoned and reaped at
        // process exit.
        var current = Volatile.Read(ref _currentServer);
        try
        {
            current?.Dispose();
        }
        catch
        {
            // ignored
        }

        t.Join(200);
    }
}