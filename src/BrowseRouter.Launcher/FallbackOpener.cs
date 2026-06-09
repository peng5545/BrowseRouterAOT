using BrowseRouter.Core;
using BrowseRouter.Launcher.Interop;

namespace BrowseRouter.Launcher;

/// <summary>
/// Final user-facing error path: a Win32 MessageBox. Used only when the Launcher
/// cannot reach the Host AND cannot spawn a new one — at that point we cannot
/// open the URL silently (would loop back through ourselves as default browser).
/// </summary>
internal static class FallbackOpener
{
    private const uint MbIconwarning = 0x00000030;
    private const uint MbOk = 0x00000000;

    public static void NotifyUnreachable(string url)
    {
        var pipeName = PipeClient.BuildPipeName(out var diag);
        var msg = $"""
                   {Constants.AppName} could not contact its background service.

                   URL: {url}

                   Pipe: \\.\pipe\{pipeName}
                   Diag: {diag}

                   Try running 'BrowseRouter.Host.exe --host' once, or re-register via '--register'.
                   """;
        User32.MessageBox(IntPtr.Zero, msg, Constants.AppName, MbOk | MbIconwarning);
    }
}