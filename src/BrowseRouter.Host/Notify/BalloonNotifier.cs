using BrowseRouter.Core.Config;
using BrowseRouter.Host.Tray;

namespace BrowseRouter.Host.Notify;

/// <summary>
/// Convenience facade over <see cref="TrayIcon.ShowBalloon"/> that respects the
/// current <see cref="NotifyOptions"/> snapshot. Drops the notification silently
/// when notifications are disabled — no exception, no logging spam.
///
/// The tray argument is nullable: when the Host is configured with
/// <c>host.enableTrayIcon=false</c>, the tray is never created and notifications
/// have no surface to attach to. In that mode <see cref="Notify"/> short-circuits
/// without touching a tray handle.
/// </summary>
internal sealed class BalloonNotifier(TrayIcon? tray, NotifyOptions options)
{
    private NotifyOptions _options = options;

    /// <summary>
    /// Update preferences after a config reload.
    /// </summary>
    public void UpdateOptions(NotifyOptions options) => _options = options;

    /// <summary>
    /// Show a balloon if notifications are enabled and a tray icon exists; no-op otherwise.
    /// </summary>
    public void Notify(string title, string message)
    {
        if (!_options.Enabled)
            return;
        tray?.ShowBalloon(title, message);
    }
}