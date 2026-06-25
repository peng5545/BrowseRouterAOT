namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Control request asking the running Host daemon to perform a full garbage
/// collection and emit a diagnostics snapshot (managed heap, handles, threads,
/// GDI/USER objects, thread pool). Sent by <c>BrowseRouter.Host.exe --gc</c>,
/// which the Launcher forwards via <c>BrowseRouter.Launcher.exe --gc</c>.
///
/// <para>
/// Carries no fields — the discriminator <c>"type":"gc"</c> alone identifies the
/// request. The Host replies with a <see cref="GcResponse"/> whose
/// <see cref="GcResponse.Report"/> is a human-readable diagnostics block; the
/// same block is also written to the daemon's log file.
/// </para>
/// </summary>
public sealed class GcRequest : PipeRequest;