namespace BrowseRouter.Core.Config;

/// <summary>
/// Desktop notification preferences. The Host always renders notifications as
/// self-drawn toast popups at the bottom-right of the work area (see
/// <c>BrowseRouter.Host.Notify.ToastNotifier</c>) — the classic
/// <c>Shell_NotifyIcon</c> balloon path was dropped because Win10/11 funnel it
/// through Action Center and clamp its on-screen time to a ~5s minimum that
/// <c>uTimeout</c> cannot override.
/// </summary>
public sealed class NotifyOptions
{
    /// <summary>
    /// Whether to show a toast when opening a URL. Defaults to <c>false</c> —
    /// notifications are opt-in to avoid surprising the user.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How long the toast stays on screen, in milliseconds. Defaults to
    /// <c>3000</c>. Values are clamped to <c>[500, 60000]</c> at use time.
    /// </summary>
    public int DurationMs { get; set; } = 3000;
}