using BrowseRouter.Core;
using BrowseRouter.Core.Config;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using CoreKernel32 = BrowseRouter.Core.Interop.Kernel32;

namespace BrowseRouter.Host.Logging;

/// <summary>
/// Lightweight daily-rotating file logger. Writes to
/// <c>%LocalAppData%\BrowseRouterAOT\logs\yyyy-MM-dd-sess{N}.log</c> by default,
/// or to a user-overridden directory from <see cref="LogOptions.Directory"/>.
/// 
/// <para>
/// <b>Non-blocking design.</b> <see cref="Info"/>/<see cref="Warn"/>/<see>
///         <cref>Error</cref>
///     </see>
///     format the timestamped line on the caller thread and enqueue it to a bounded
/// <see cref="BlockingCollection{T}"/>. A single dedicated background thread drains the
/// queue and performs all file I/O. Callers never block on disk writes or
/// contend on a lock - the previous synchronous <c>WriteLine</c> + <c>Flush</c>
/// under a lock pinned thread-pool threads during bursts of URL clicks, which
/// was the primary driver of thread-pool churn (and thus thread-handle
/// accumulation).
/// </para>
/// 
/// <para>
/// The background thread owns the <see cref="StreamWriter"/> exclusively and
/// handles date rollover / directory changes / option reloads without any lock.
/// On <see cref="Dispose"/> the collection is completed and the thread drains
/// remaining messages before closing the file, so no log lines are lost on
/// graceful shutdown.
/// </para>
/// </summary>
internal sealed class FileLogger : IDisposable
{
    /// <summary>
    /// Session id never changes for a process's lifetime - cache it once at
    /// class init instead of calling <c>Process.GetCurrentProcess().SessionId</c>
    /// on every writer open (which leaked a process handle each time, reclaimed
    /// only when the <c>Process</c> object was eventually GC'd).
    /// </summary>
    private static readonly int SessionId = CoreKernel32.GetCurrentSessionId();

    private const int QueueCapacity = 4096;

    private readonly BlockingCollection<string> _queue = new(QueueCapacity);

    private readonly Thread _writerThread;
    private volatile bool _disposed;

    // ── Writer-thread-only state (no lock needed) ──────────────────────────
    private LogOptions _options;
    private StreamWriter? _writer;
    private string? _writerPath;
    private string? _writerDate;
    private LogOptions? _writerOptions; // snapshot to detect option reloads

    public FileLogger(LogOptions? options = null)
    {
        _options = options ?? new LogOptions();
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "BrowseRouterAOT.FileLogger"
        };
        _writerThread.Start();
    }

    // ── Public API (callable from any thread) ──────────────────────────────

    /// <summary>
    /// Replace the logger's options (e.g. after config reload). Cheap reference
    /// swap; the writer thread detects the new reference on the next message
    /// and reopens against the new directory if it changed.
    /// </summary>
    public void UpdateOptions(LogOptions options) => _options = options;

    /// <summary>
    /// Write at INFO level.
    /// </summary>
    public void Info(string msg) => Enqueue("INFO", msg);

    /// <summary>
    /// Write at WARN level.
    /// </summary>
    public void Warn(string msg) => Enqueue("WARN", msg);

    /// <summary>
    /// Write at ERROR level.
    /// </summary>
    public void Error(string msg) => Enqueue("ERR ", msg);

    /// <summary>
    /// Convenience: log an exception with a context message.
    /// </summary>
    public void Error(string context, Exception ex) =>
        Enqueue("ERR ", $"{context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    // ── Enqueue (caller thread - never blocks) ─────────────────────────────

    private void Enqueue(string level, string msg)
    {
        if (_disposed)
            return;
        if (!_options.Enabled)
            return;

        // Format the full line (including timestamp) on the caller thread so
        // the timestamp reflects when the event occurred, not when the writer
        // thread happened to flush it. TryAdd returns false if the bounded queue is full (extremely unlikely -
        // the queue holds 4096 lines); the line is silently dropped.
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        _queue.TryAdd(line);
    }

    // ── Writer thread ──────────────────────────────────────────────────────

    private void WriterLoop()
    {
        try
        {
            // GetConsumingEnumerable() blocks until items arrive or
            // CompleteAdding() is called. Synchronous - fine for a dedicated thread
            // that has no other job.
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                WriteOne(line);
            }
        }
        catch (Exception ex)
        {
            // The writer loop should never throw (WriteOne catches its own
            // exceptions), but guard against the truly unexpected so the
            // background thread doesn't die silently.
            try
            {
                Console.Error.WriteLine($"FileLogger writer crashed: {ex}");
            }
            catch
            {
                /* no console */
            }
        }
        finally
        {
            CloseWriter();
        }
    }

    private void WriteOne(string line)
    {
        var opt = _options;

        var dir = opt.Directory is { Length: > 0 } nonEmpty ? nonEmpty : Constants.DefaultLogDirectory;
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Reopen the writer if the date, directory, or options reference
        // changed (hot-reload), or if the previous open failed (null writer).
        if (_writer is null ||
            _writerDate != today ||
            _writerPath is null ||
            !string.Equals(Path.GetDirectoryName(_writerPath), dir, StringComparison.OrdinalIgnoreCase) ||
            !ReferenceEquals(_writerOptions, opt))
        {
            CloseWriter();
            if (!TryOpenWriter(dir, today, out _writer, out _writerPath))
            {
                // Open failed - mirror to console as a fallback, then bail.
                // The next message will retry the open.
                TryConsole(line);
                return;
            }

            _writerDate = today;
            _writerOptions = opt;
        }

        try
        {
            _writer.WriteLine(line);
            // Flush per line so an unexpected process death loses at most the
            // tail of the in-flight line, not a full buffer's worth.
            _writer.Flush();
        }
        catch
        {
            // Write failed (disk full, handle stolen, etc.). Drop the writer;
            // the next message will attempt to reopen. Never crash the daemon.
            CloseWriter();
            TryConsole(line);
        }
    }

    private static bool TryOpenWriter(
        string dir,
        string date,
        [NotNullWhen(true)] out StreamWriter? writer,
        out string path
    )
    {
        writer = null;
        // Per-session suffix so two concurrent sessions (RDP / Fast User
        // Switching) each get their own file instead of interleaving writes.
        path = Path.Combine(dir, $"{date}-sess{SessionId}.log");
        try
        {
            Directory.CreateDirectory(dir);
            // FileShare.ReadWrite so a tail/grep tool can read while we write.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            // StreamWriter's default UTF-8-without-BOM is fine for log files.
            writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false // explicit Flush per line
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CloseWriter()
    {
        if (_writer is null)
            return;
        try
        {
            _writer.Flush();
        }
        catch
        {
            /* ignored */
        }

        try
        {
            _writer.Dispose();
        }
        catch
        {
            /* underlying stream included */
        }

        _writer = null;
        _writerPath = null;
        _writerDate = null;
        _writerOptions = null;
    }

    private static void TryConsole(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
            /* no console attached */
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Signal the writer thread that no more items will arrive. It drains
        // the remaining queue, then closes the file in its finally block.
        _queue.CompleteAdding();

        // Bounded wait so a stuck write (e.g. network-share hang) doesn't
        // deadlock shutdown. 2 s is generous for a local file flush.
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    /// <summary>
    /// Best-effort one-shot console write for callers that don't have a
    /// <see cref="FileLogger"/> in scope (e.g. <c>SettingsLauncher</c> when
    /// invoked from a CLI subcommand with no daemon context). Writes to
    /// stderr so it doesn't interleave with normal stdout output.
    /// </summary>
    public static void TryLogConsole(string msg)
    {
        try
        {
            Console.Error.WriteLine($"{Constants.AppName}: {msg}");
        }
        catch
        {
            /* no console attached */
        }
    }
}