using System;
using System.IO;

namespace BrowseRouter.Core;

/// <summary>
/// Cross-process constants shared by Launcher and Host.
/// </summary>
public static class Constants
{
    /// <summary>
    /// Friendly app name shown in notifications / settings UI.
    /// </summary>
    public const string AppName = "BrowseRouter (AOT)";

    /// <summary>
    /// Stable identifier used in registry / pipe / mutex names.
    /// </summary>
    public const string AppId = "BrowseRouterAOT";

    /// <summary>
    /// Application description, shown in Windows default-apps capabilities.
    /// </summary>
    public const string AppDescription = "Routes URLs to the right browser.";

    /// <summary>
    /// Default config filename in %AppData%.
    /// </summary>
    public const string ConfigFileName = "browsers.json";

    /// <summary>
    /// Default unscoped portion of the named-pipe name. The full pipe name is
    /// <c>{PipeBaseName}.{userSid}.{sessionId}</c>, scoping a pipe to one
    /// user+session to avoid cross-session collisions on RDP / Fast User Switching.
    /// </summary>
    public const string PipeBaseName = AppId;

    /// <summary>
    /// Per-user folder for config (read-write).
    /// Example: <c>C:\Users\Joe\AppData\Roaming\BrowseRouterAOT</c>.
    /// </summary>
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId);

    /// <summary>
    /// Per-user folder for logs (read-write, non-roaming).
    /// Example: <c>C:\Users\Joe\AppData\Local\BrowseRouterAOT</c>.
    /// </summary>
    public static string LocalAppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId);

    /// <summary>
    /// Full path to the config file under <see cref="AppDataDirectory"/>.
    /// </summary>
    public static string DefaultConfigFilePath =>
        Path.Combine(AppDataDirectory, ConfigFileName);

    /// <summary>
    /// Default log directory under <see cref="LocalAppDataDirectory"/>.
    /// </summary>
    public static string DefaultLogDirectory =>
        Path.Combine(LocalAppDataDirectory, "logs");
}