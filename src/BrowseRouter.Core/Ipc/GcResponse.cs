namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Reply to a <see cref="GcRequest"/>. <see cref="Report"/> is a multi-line,
/// human-readable diagnostics snapshot captured by the daemon immediately after
/// it ran <c>GC.Collect</c>. The <c>--gc</c> caller prints it to the console;
/// the daemon has already logged the same text to its log file.
/// </summary>
public sealed class GcResponse : PipeResponse
{
    /// <summary>
    /// True iff the GC + diagnostics capture completed without throwing.
    /// </summary>
    public bool Ok { get; set; }

    /// <summary>
    /// The formatted diagnostics block, or an error message when
    /// <see cref="Ok"/> is false.
    /// </summary>
    public string? Report { get; set; }
}