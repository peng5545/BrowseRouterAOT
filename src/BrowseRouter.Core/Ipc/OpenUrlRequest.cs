namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Request sent over the named pipe from Launcher → Host. One message per URL.
/// The Host treats absent/null source fields as "unknown" (Launcher couldn't gather
/// them — e.g. orphan parent / admin token), and falls back to URL rules only.
/// </summary>
public sealed class OpenUrlRequest
{
    /// <summary>
    /// Protocol message type. Always <c>"openUrl"</c> for now.
    /// </summary>
    public string Type { get; set; } = "openUrl";

    /// <summary>
    /// The URL exactly as received from the OS (before any filter).
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// File name of the calling process (e.g. <c>"TEAMS.EXE"</c>), or null.
    /// </summary>
    public string? SourceProcessName { get; set; }

    /// <summary>
    /// Full path to the calling process executable, or null.
    /// </summary>
    public string? SourceProcessPath { get; set; }

    /// <summary>
    /// Main-window title of the calling process, or null.
    /// </summary>
    public string? SourceWindowTitle { get; set; }

    /// <summary>
    /// PID of the calling process. Diagnostic only.
    /// </summary>
    public int CallerPid { get; set; }

    /// <summary>
    /// Session ID of the calling process. Diagnostic only.
    /// </summary>
    public int CallerSessionId { get; set; }
}