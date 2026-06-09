using BrowseRouter.Core;
using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Logging;

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
    /// Only <c>http</c> and <c>https</c> protocols are advertised. FTP and HTML
    /// file associations are intentionally NOT registered — they would broaden
    /// the surface for a routing tool that targets web links.
    /// </remarks>
    public void Register(string launcherExePath)
    {
        log.Info($"Registering as default-browser candidate. launcher={launcherExePath}");

        var iconValue = $"\"{launcherExePath}\",0";
        var openUrlCmd = $"\"{launcherExePath}\" \"%1\"";

        using (var _ = AdvApi32.CreateHkcuSubKey(AppKey))
        using (var caps = AdvApi32.CreateHkcuSubKey(CapabilitiesKey))
        using (var urlAssoc = AdvApi32.CreateHkcuSubKey(UrlAssocKey))
        {
            // Capabilities: shown in Default Apps "Choose default apps by protocol" UI.
            CheckRc(AdvApi32.SetStringValue(caps, "ApplicationName", Constants.AppName));
            CheckRc(AdvApi32.SetStringValue(caps, "ApplicationDescription", Constants.AppDescription));
            CheckRc(AdvApi32.SetStringValue(caps, "ApplicationIcon", iconValue));

            // Protocol associations: hand http(s) through our ProgId. ftp and
            // .htm/.html are intentionally NOT registered.
            CheckRc(AdvApi32.SetStringValue(urlAssoc, "http", ProgIdName));
            CheckRc(AdvApi32.SetStringValue(urlAssoc, "https", ProgIdName));
        }

        using (var progId = AdvApi32.CreateHkcuSubKey(UrlProgIdKey))
        using (var cmd = AdvApi32.CreateHkcuSubKey(UrlProgIdCmdKey))
        {
            // The ProgId — what URL associations point at. shell\open\command
            // is what Windows invokes with %1 = URL.
            CheckRc(AdvApi32.SetStringValue(progId, string.Empty, Constants.AppName));
            CheckRc(AdvApi32.SetStringValue(progId, "FriendlyTypeName", Constants.AppName));
            CheckRc(AdvApi32.SetStringValue(cmd, string.Empty, openUrlCmd));
        }

        // Advertise ourselves in RegisteredApplications. Without this entry, our
        // Capabilities subtree is invisible to the Default Apps UI.
        using (var ra = AdvApi32.CreateHkcuSubKey(RegisteredAppsKey))
        {
            CheckRc(AdvApi32.SetStringValue(ra, Constants.AppId, CapabilitiesKey));
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