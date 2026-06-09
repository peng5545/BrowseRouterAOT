using BrowseRouter.Core;
using BrowseRouter.Core.Config;
using System.IO;
using System.Threading;

namespace BrowseRouter.Host.Logging;

/// <summary>
/// Lightweight daily-rotating file logger. Writes to
/// <c>%LocalAppData%\BrowseRouterAOT\logs\yyyy-MM-dd.log</c> by default, or to a
/// user-overridden directory from <see cref="LogOptions.Directory"/>. Thread-safe
/// via a single lock — log volume is low (one entry per URL click).
/// </summary>
internal sealed class FileLogger(LogOptions? options = null)
{
    private readonly Lock _gate = new();
    private LogOptions _options = options ?? new LogOptions();

    /// <summary>
    /// Replace the logger's options (e.g. after config reload).
    /// </summary>
    public void UpdateOptions(LogOptions options)
    {
        lock (_gate)
            _options = options;
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
        LogOptions opt;
        lock (_gate)
            opt = _options;
        if (!opt.Enabled)
            return;

        // `is { Length: > 0 }` narrows the nullable string to non-null in the
        // true branch, so the `!` we used to need here is no longer necessary.
        var dir = opt.Directory is { Length: > 0 } nonEmpty ? nonEmpty : Constants.DefaultLogDirectory;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";

        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");
            // FileShare.Read so users can tail the live log without errors.
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(line);
        }
        catch
        {
            // Never let logging crash the daemon. Best-effort console mirror.
            try
            {
                Console.Error.WriteLine(line);
            }
            catch
            {
                /* ignored */
            }
        }
    }
}