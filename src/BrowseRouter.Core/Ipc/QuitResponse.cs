namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Reply to a <see cref="QuitRequest"/> acknowledging that the daemon has
/// initiated a graceful shutdown.
/// </summary>
public sealed class QuitResponse : PipeResponse
{
    /// <summary>
    /// True iff the daemon accepted the shutdown signal.
    /// </summary>
    public bool Ok { get; set; }
}