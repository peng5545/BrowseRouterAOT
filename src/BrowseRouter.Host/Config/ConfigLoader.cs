using BrowseRouter.Core.Config;
using BrowseRouter.Core.Json;
using BrowseRouter.Host.Logging;
using System.IO;
using System.Text.Json;

namespace BrowseRouter.Host.Config;

/// <summary>
/// Reads <see cref="RootConfig"/> from disk using the source-generated
/// <see cref="AppJsonContext"/>. AOT-safe (no reflection). Returns parsed config
/// or throws on I/O / JSON errors — callers are expected to catch and decide
/// whether to keep the previous in-memory snapshot.
/// </summary>
internal static class ConfigLoader
{
    /// <summary>
    /// Read <paramref name="path"/> synchronously and parse it.
    /// </summary>
    public static RootConfig Load(string path, FileLogger? log = null)
    {
        // Read with FileShare.ReadWrite — the user may be saving the file from an
        // editor while we read; we just retry once at the caller level on failure.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var config = JsonSerializer.Deserialize(fs, AppJsonContext.Default.RootConfig);

        var result = config ?? RootConfig.Empty;

        if (log != null)
        {
            var warnings = result.Validate();
            foreach (var w in warnings)
            {
                log.Warn($"Config: {w}");
            }
        }

        return result;
    }

    /// <summary>
    /// Try to load; never throws. Returns <c>null</c> on any failure and reports the
    /// reason via <paramref name="error"/>.
    /// </summary>
    public static RootConfig? TryLoad(string path, out Exception? error, FileLogger? log = null)
    {
        try
        {
            error = null;
            return Load(path, log);
        }
        catch (Exception ex)
        {
            error = ex;
            return null;
        }
    }
}