using BrowseRouter.Core;
using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Logging;

namespace BrowseRouter.Host.Registration;

/// <summary>
/// Manages the <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> entry so
/// the Host daemon starts at user logon. The value is the Host executable invoked
/// with <c>--host</c>.
/// </summary>
internal sealed class AutoStart(FileLogger log)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = $"{Constants.AppId}.Host";

    /// <summary>
    /// Write the Run-key value. <paramref name="hostExePath"/> must be the absolute
    /// path to <c>BrowseRouter.Host.exe</c>.
    /// </summary>
    public void Enable(string hostExePath)
    {
        var cmd = $"\"{hostExePath}\" --host";
        using var key = AdvApi32.CreateHkcuSubKey(RunKey);
        var rc = AdvApi32.SetStringValue(key, ValueName, cmd);
        if (rc != 0)
            throw new InvalidOperationException($"Set Run key failed: {rc}");
        log.Info($"Enabled autostart: {cmd}");
    }

    /// <summary>
    /// Remove the Run-key value. Missing value is treated as success.
    /// </summary>
    public void Disable()
    {
        AdvApi32.DeleteHkcuValueQuiet(RunKey, ValueName);
        log.Info("Disabled autostart.");
    }
}