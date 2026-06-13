using BrowseRouter.Core;
using BrowseRouter.Core.Config;
using System.IO;
using System.Text;
using System.Threading;

namespace BrowseRouter.Host.Logging;

/// <summary>
/// Lightweight daily-rotating file logger. Writes to
/// <c>%LocalAppData%\BrowseRouterAOT\logs\yyyy-MM-dd.log</c> by default, or to a
/// user-overridden directory from <see cref="LogOptions.Directory"/>.
///
/// <para>
/// Holds a single long-lived <see cref="StreamWriter"/> per current calendar day
/// (local time), reopened only on date rollover or on <see cref="UpdateOptions"/>.
/// Each write is O(1) syscalls instead of "open / write / close" per log call —
/// matters because every URL click emits several lines.
/// </para>
///
/// <para>
/// All access goes through <see cref="_gate"/>; the writer is rebuilt under the
/// lock when the day or directory changes. <see cref="Dispose"/> flushes and
/// closes the active writer.
/// </para>
/// </summary>
internal sealed class FileLogger(LogOptions? options = null) : IDisposable
{
    private readonly Lock _gate = new();
    private LogOptions _options = options ?? new LogOptions();
    private StreamWriter? _writer;
    private string? _writerPath; // path the active writer is bound to
    private string? _writerDate; // yyyy-MM-dd of the active writer
    private bool _disposed;

    /// <summary>
    /// Replace the logger's options (e.g. after config reload). The next write
    /// will reopen the writer against the new directory/date if either changed.
    /// </summary>
    public void UpdateOptions(LogOptions options)
    {
        lock (_gate)
        {
            _options = options;
            // Force a re-open on next write by clearing the cached identity.
            _writerDate = null;
        }
    }

    /// <summary>
    /// Write at INFO level.
    /// </summary>
    public void Info(string msg) => Write("INFO", msg);

    /// <summary>
    /// Write at WARN level.
    /// </summary>
    public void Warn(string msg) => Write("WARN", msg);

    /// <summary>
    /// Write at ERROR level.
    /// </summary>
    public void Error(string msg) => Write("ERR ", msg);

    /// <summary>
    /// Convenience: log an exception with a context message.
    /// </summary>
    public void Error(string context, Exception ex) =>
        Write("ERR ", $"{context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private void Write(string level, string msg)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var opt = _options;
            if (!opt.Enabled)
                return;

            var dir = opt.Directory is { Length: > 0 } nonEmpty ? nonEmpty : Constants.DefaultLogDirectory;
            var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            // Reopen the writer if the date, path, or options-driven path changed,
            // OR if the previous open failed and left us with a null writer.
            if (_writer is null ||
                _writerDate != today ||
                _writerPath is null ||
                !string.Equals(Path.GetDirectoryName(_writerPath), dir, StringComparison.OrdinalIgnoreCase))
            {
                CloseWriterLocked();
                if (!TryOpenWriterLocked(dir, today, out _writer, out _writerPath))
                {
                    // Open failed — best-effort console mirror, then bail. The
                    // next call will retry the open.
                    MirrorToConsole(level, msg);
                    return;
                }

                _writerDate = today;
            }

            try
            {
                // _writer is guaranteed non-null here: the if-block above
                // either skipped (meaning it was already non-null) or entered
                // and assigned a fresh one via TryOpenWriterLocked.
                _writer!.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}");
                // AutoFlush so an unexpected process death loses at most the
                // tail of the in-flight line, not a full buffer's worth.
                _writer!.Flush();
            }
            catch
            {
                // Write failed (disk full, file handle stolen by external
                // truncation, etc.). Drop the writer; the next call will
                // attempt to reopen. Never let logging crash the daemon.
                CloseWriterLocked();
                MirrorToConsole(level, msg);
            }
        }
    }

    private static bool TryOpenWriterLocked(string dir, string date, out StreamWriter? writer, out string path)
    {
        writer = null;
        path = Path.Combine(dir, $"{date}.log");
        try
        {
            Directory.CreateDirectory(dir);
            // FileShare.Read so users can tail the live log without errors.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            // StreamWriter's default UTF-8-without-BOM is fine for log files.
            writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false // we flush explicitly per write
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CloseWriterLocked()
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
            /* ignored — underlying stream included */
        }

        _writer = null;
        _writerPath = null;
        _writerDate = null;
    }

    private static void MirrorToConsole(string level, string msg)
    {
        try
        {
            Console.Error.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}");
        }
        catch
        {
            /* no console — ignored */
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CloseWriterLocked();
        }
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