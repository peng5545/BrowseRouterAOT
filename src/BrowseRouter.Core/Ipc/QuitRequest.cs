namespace BrowseRouter.Core.Ipc;

/// <summary>
/// Control request asking the running Host daemon to shut down gracefully.
/// Sent by <c>BrowseRouter.Host.exe --quit</c> (or <c>--exit</c>), which the
/// Launcher also forwards via <c>BrowseRouter.Launcher.exe --quit</c>.
/// </summary>
public sealed class QuitRequest : PipeRequest;