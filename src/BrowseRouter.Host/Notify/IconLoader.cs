using BrowseRouter.Host.Interop;
using BrowseRouter.Host.Logging;
using System.IO;

namespace BrowseRouter.Host.Notify;

/// <summary>
/// Loads the embedded <c>BrowseRouter.Host.Resources.icon.png</c> resource as
/// an <c>HICON</c> via <c>CreateIconFromResourceEx</c>. Shared between the tray
/// icon (small / shared / OS-owned) and the toast popup (specific size / owned
/// by us). Returns <see cref="IntPtr.Zero"/> on any failure — callers fall back
/// to the system application icon.
/// </summary>
internal static class IconLoader
{
    private const string ResourceName = "BrowseRouter.Host.Resources.icon.png";

    /// <summary>
    /// Magic version constant required by <c>CreateIconFromResourceEx</c>.
    /// </summary>
    private const uint IconResourceVersion = 0x00030000;

    /// <summary>
    /// Load the embedded PNG as an <c>HICON</c> at the requested pixel size.
    /// </summary>
    /// <param name="cx">
    /// Desired width in physical pixels. Pass <c>0</c> together with <paramref name="defaultSize"/> = <c>true</c>
    /// to let Windows pick the default small-icon size.
    /// </param>
    /// <param name="cy">Desired height in physical pixels (matched with <paramref name="cx"/>).</param>
    /// <param name="shared">
    /// <c>true</c> for OS-cached / system-owned (do NOT <c>DestroyIcon</c>).
    /// <c>false</c> for caller-owned (the caller must <c>DestroyIcon</c>).
    /// </param>
    /// <param name="defaultSize">
    /// Forwards the <c>LR_DEFAULTSIZE</c> flag. When <c>true</c>, <c>cx</c>/<c>cy</c> are ignored
    /// and the system default size is used.
    /// </param>
    /// <param name="log"></param>
    public static IntPtr LoadEmbedded(int cx, int cy, bool shared, bool defaultSize, FileLogger log)
    {
        try
        {
            var assembly = typeof(IconLoader).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                log.Warn($"Embedded icon resource '{ResourceName}' not found.");
                return IntPtr.Zero;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            uint flags = 0;
            if (shared)
                flags |= User32.LrShared;
            if (defaultSize)
                flags |= User32.LrDefaultsize;

            return User32.CreateIconFromResourceEx(bytes, (uint) bytes.Length, true, IconResourceVersion, cx, cy,
                flags);
        }
        catch (Exception ex)
        {
            log.Warn($"Native PNG to Icon conversion failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }
}