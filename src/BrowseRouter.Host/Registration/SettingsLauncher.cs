using BrowseRouter.Core;
using System.Diagnostics;

namespace BrowseRouter.Host.Registration;

/// <summary>
/// Opens the Windows Settings "Default apps" page, optionally pre-filtered to our
/// registered app. Users still have to click through — Windows 10 1803+ blocks
/// programs from silently taking over the default-browser association.
/// </summary>
internal static class SettingsLauncher
{
    /// <summary>
    /// Open Default Apps with our app pre-selected.
    /// </summary>
    public static void OpenDefaultApps()
    {
        try
        {
            // The `registeredAppUser` deep-link is what surfaces "Set defaults for
            // BrowseRouter" in the Settings UI.
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = $"ms-settings:defaultapps?registeredAppUser={Constants.AppName}",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // Some locked-down systems disable ms-settings: — fall back to the bare URI.
            try
            {
                using var __ = Process.Start(new ProcessStartInfo
                    { FileName = "ms-settings:defaultapps", UseShellExecute = true });
            }
            catch (Exception inner)
            {
                // Both attempts failed — at least log so the user/operator can see
                // why nothing visibly happened. Goes to the existing FileLogger
                // that Program.cs already constructed.
                Logging.FileLogger.TryLogConsole(
                    $"{Constants.AppName}: could not open Default Apps: {inner.Message} (initial: {ex.Message})");
            }
        }
    }
}