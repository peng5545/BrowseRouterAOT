using System.Text.Json.Serialization;

namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Request sent over the named pipe from Launcher → Host to open one URL. The
/// <c>"type":"openUrl"</c> discriminator is contributed by <see cref="PipeRequest"/>
/// — there is no separate <c>Type</c> property here. The Host treats absent/null
/// source fields as "unknown" (Launcher couldn't gather them — e.g. orphan
/// parent / admin token), and falls back to URL rules only.
/// </summary>
public sealed class OpenUrlRequest : PipeRequest
{
    /// <summary>
    /// The URL exactly as received from the OS (before any filter).
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// File name (with <c>.exe</c> extension) of the calling process
    /// (e.g. <c>"TEAMS.EXE"</c>), or null. Computed from the calling process's
    /// image path on the Launcher side, so the value matches what
    /// <see cref="SourceProcessPath"/> points at.
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
    /// PID of the *originating* process (the one that clicked the link and
    /// caused the Launcher to spawn). Best-effort — the OS may deny the query
    /// (SYSTEM / elevated parents), in which case this is 0. Distinct from
    /// <see cref="LauncherPid"/>, which is the Launcher itself.
    /// </summary>
    public int SourcePid { get; set; }

    /// <summary>
    /// PID of the Launcher process (the small AOT binary the OS spawned for
    /// this click). Useful for diagnostic logs but not for routing — the
    /// originating click attribution lives in <see cref="SourcePid"/>.
    /// </summary>
    public int LauncherPid { get; set; }

    /// <summary>
    /// Session ID of the Launcher's session. Diagnostic only.
    /// </summary>
    public int LauncherSessionId { get; set; }

    /// <summary>
    /// STJ uses this constructor (over the parameterless one) so we can validate
    /// the inbound URL at deserialisation time. An empty/whitespace
    /// <c>"url"</c> in the wire payload throws <see cref="ArgumentException"/>
    /// → STJ wraps it in a <see cref="System.Text.Json.JsonException"/>, which
    /// the Host's pipe handler logs as "malformed request" instead of routing
    /// a null URL through the resolver.
    ///
    /// <para><see cref="System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"/>
    /// tells the C# compiler that this constructor satisfies the
    /// <c>required Url</c> contract — the parameter is named <c>url</c>, STJ
    /// routes the JSON property to it, and the C# nullable analysis trusts
    /// that callers have provided a value.</para>
    /// </summary>
    [JsonConstructor]
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public OpenUrlRequest(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Url is required and cannot be empty/whitespace.", nameof(url));
        Url = url;
    }

    /// <summary>
    /// Parameterless constructor for direct (non-STJ) construction, e.g. the
    /// Launcher building a request via object-initializer syntax.
    /// </summary>
    public OpenUrlRequest()
    {
    }
}