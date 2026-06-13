using BrowseRouter.Core;
using System.IO;

namespace BrowseRouter.Host.Config;

/// <summary>
/// Resolves config-related filesystem paths. All paths live under the user profile;
/// nothing needs admin rights.
/// </summary>
internal static class ConfigPaths
{
    /// <summary>
    /// Full path to the live config file (read by Host, edited by user).
    /// </summary>
    public static string ConfigFile => Constants.DefaultConfigFilePath;

    /// <summary>
    /// Directory containing <see cref="ConfigFile"/>.
    /// </summary>
    public static string ConfigDirectory => Constants.AppDataDirectory;

    /// <summary>
    /// Path to the template config shipped next to the Host executable.
    /// Copied to <see cref="ConfigFile"/> on first run if the user has no config yet.
    /// </summary>
    public static string TemplateConfigFile => Path.Combine(AppContext.BaseDirectory, "browsers.template.json");

    /// <summary>
    /// Ensure the config directory exists. Idempotent.
    /// </summary>
    public static void EnsureConfigDirectory() => Directory.CreateDirectory(ConfigDirectory);
}