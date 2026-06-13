namespace BrowseRouter.Core.Config;

/// <summary>
/// Tuning knobs for the Host daemon.
/// </summary>
public sealed class HostOptions
{
    /// <summary>
    /// Whether to show a tray icon and tray context menu. Defaults to <c>true</c>.
    /// When <c>false</c>, the Host still runs in the background (pipe server, config
    /// watcher, etc.) — the only thing suppressed is the tray UI. With no tray there
    /// is no menu to Quit from, so a Host launched with this disabled can only be
    /// stopped via Ctrl+C in its console (or via Task Manager).
    /// </summary>
    public bool EnableTrayIcon { get; set; } = true;
}