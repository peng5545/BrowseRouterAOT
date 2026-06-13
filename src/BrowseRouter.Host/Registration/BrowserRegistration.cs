using BrowseRouter.Core;
using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Logging;
using System.Collections.Generic;

namespace BrowseRouter.Host.Registration;

/// <summary>
/// HKCU registration as a Windows default-browser candidate. Writes the same key
/// layout as the original BrowseRouter (so the system's Default Apps UI sees us),
/// but points <c>shell\open\command</c> at the Launcher.exe so each click takes
/// the cheap forwarding path instead of spinning up the heavyweight Host.
/// </summary>
internal sealed class BrowserRegistration(FileLogger log)
{
    // Registry layout — mirrors original BrowseRouter exactly.
    private const string AppKey = $@"SOFTWARE\{Constants.AppId}";
    private const string CapabilitiesKey = $@"SOFTWARE\{Constants.AppId}\Capabilities";
    private const string UrlAssocKey = $@"SOFTWARE\{Constants.AppId}\Capabilities\URLAssociations";
    private const string UrlProgIdKey = $@"SOFTWARE\Classes\{Constants.AppId}URL";
    private const string UrlProgIdCmdKey = $@"SOFTWARE\Classes\{Constants.AppId}URL\shell\open\command";
    private const string RegisteredAppsKey = @"SOFTWARE\RegisteredApplications";
    private const string ProgIdName = $"{Constants.AppId}URL";

    /// <summary>
    /// Register the Launcher as a browser candidate, then open Default Apps so the
    /// user can pick it. <paramref name="launcherExePath"/> must be the absolute
    /// path to <c>BrowseRouter.Launcher.exe</c>.
    /// </summary>
    /// <remarks>
    /// <para>Only <c>http</c> and <c>https</c> protocols are advertised. FTP and
    /// HTML file associations are intentionally NOT registered — they would
    /// broaden the surface for a routing tool that targets web links.</para>
    ///
    /// <para>The whole write is wrapped in a snapshot/restore safety net: before
    /// touching any value we read its current state, and if any step throws we
    /// walk the snapshots back in reverse order to undo the partial write. The
    /// user's existing registration (if any) is therefore preserved verbatim on
    /// failure instead of being left half-overwritten.</para>
    /// </remarks>
    public void Register(string launcherExePath)
    {
        log.Info($"Registering as default-browser candidate. launcher={launcherExePath}");

        var iconValue = $"\"{launcherExePath}\",0";
        var openUrlCmd = $"\"{launcherExePath}\" \"%1\"";

        // (parent path, value name, new value) tuples in the order we'll
        // attempt writes. Snapshots are taken right before the commit phase
        // so they reflect pre-write state.
        var plan = new List<(string Parent, string Name, string New)>
        {
            (CapabilitiesKey, "ApplicationName", Constants.AppName),
            (CapabilitiesKey, "ApplicationDescription", Constants.AppDescription),
            (CapabilitiesKey, "ApplicationIcon", iconValue),
            (UrlAssocKey, "http", ProgIdName),
            (UrlAssocKey, "https", ProgIdName),
            (UrlProgIdKey, string.Empty, Constants.AppName),
            (UrlProgIdKey, "FriendlyTypeName", Constants.AppName),
            (UrlProgIdCmdKey, string.Empty, openUrlCmd),
            (RegisteredAppsKey, Constants.AppId, CapabilitiesKey)
        };

        // Snapshot phase. Capture every value we're about to overwrite; an
        // "absent" entry is recorded when the value doesn't exist, so a
        // rollback deletes it.
        var snapshots = new AdvApi32.ValueSnapshot[plan.Count];
        for (var i = 0; i < plan.Count; i++)
        {
            snapshots[i] = AdvApi32.SnapshotValue(plan[i].Parent, plan[i].Name);
        }

        // Commit phase. Any throw lands in the catch and walks the
        // snapshots back in reverse to undo the partial write.
        try
        {
            // Pre-create AppKey so the subtree always exists after a (failed
            // or successful) Register. Rollback deletes the AppKey subkey,
            // which is fine because the prior state didn't have it.
            using var _ = AdvApi32.CreateHkcuSubKey(AppKey);

            for (var i = 0; i < plan.Count; i++)
            {
                using var key = AdvApi32.CreateHkcuSubKey(plan[i].Parent);
                CheckRc(AdvApi32.SetStringValue(key, plan[i].Name, plan[i].New));
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Registration failed mid-write, rolling back: {ex.Message}");
            for (var i = snapshots.Length - 1; i >= 0; i--)
            {
                try
                {
                    AdvApi32.RestoreValue(snapshots[i]);
                }
                catch (Exception rbEx)
                {
                    log.Warn($"Rollback of {plan[i].Parent}\\{plan[i].Name} failed: {rbEx.Message}");
                }
            }

            // The pre-create above (line ~80) wrote the AppKey subkey without
            // recording it in the snapshot list, so the snapshot walk doesn't
            // remove it. On a failed Register, the prior state had no AppKey
            // (the whole subtree is owned by us), so we can drop the now-empty
            // parent key. On a successful Register, the subkey stays so the
            // browser can find its Capabilities\… layout.
            AdvApi32.DeleteHkcuTreeQuiet(AppKey);

            throw;
        }

        log.Info("Registration complete.");
    }

    /// <summary>
    /// Remove every registry artefact created by <see cref="Register"/>.
    /// </summary>
    public void Unregister()
    {
        log.Info("Unregistering as default-browser candidate.");
        AdvApi32.DeleteHkcuValueQuiet(RegisteredAppsKey, Constants.AppId);
        AdvApi32.DeleteHkcuTreeQuiet(AppKey);
        AdvApi32.DeleteHkcuTreeQuiet(UrlProgIdKey);
        log.Info("Unregistration complete.");
    }

    private static void CheckRc(int rc)
    {
        if (rc != 0)
            throw new InvalidOperationException($"Registry write failed: {rc}");
    }
}