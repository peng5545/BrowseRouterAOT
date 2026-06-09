namespace BrowseRouter.Core.Config;

/// <summary>
/// Desktop notification preferences.
/// </summary>
public sealed class NotifyOptions
{
    /// <summary>
    /// Whether to show a balloon when opening a URL. Defaults to <c>false</c> —
    /// notifications are opt-in to avoid surprising the user.
    /// </summary>
    public bool Enabled { get; set; }
}