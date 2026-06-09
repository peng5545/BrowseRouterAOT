namespace BrowseRouter.Core.Config;

/// <summary>
/// File-logging preferences.
/// </summary>
public sealed class LogOptions
{
    /// <summary>
    /// Whether to write to a log file (and console where attached).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional override for the log directory. When <c>null</c>, defaults to
    /// <see cref="Constants.DefaultLogDirectory"/>. One log file per day:
    /// <c>yyyy-MM-dd.log</c>.
    /// </summary>
    public string? Directory { get; set; }
}