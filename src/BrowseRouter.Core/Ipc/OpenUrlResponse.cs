namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Reply written by the Host after handling an <see cref="OpenUrlRequest"/>.
/// The <c>"type":"openUrl"</c> discriminator is contributed by
/// <see cref="PipeResponse"/>.
/// </summary>
public sealed class OpenUrlResponse : PipeResponse
{
    /// <summary>
    /// True iff a browser was successfully launched.
    /// </summary>
    public bool Ok { get; set; }

    /// <summary>
    /// Browser key chosen (matches <c>browsers</c> entry).
    /// </summary>
    public string? ChosenBrowser { get; set; }

    /// <summary>
    /// The URL that was actually launched (after any filter).
    /// </summary>
    public string? ResolvedUrl { get; set; }

    /// <summary>
    /// Name of the filter that fired, if any.
    /// </summary>
    public string? AppliedFilter { get; set; }

    /// <summary>
    /// One-line explanation of which rule matched (for logging).
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// On <c>Ok=false</c>, a human-readable error message.
    /// </summary>
    public string? Error { get; set; }
}