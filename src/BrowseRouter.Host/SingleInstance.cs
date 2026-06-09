using BrowseRouter.Core;
using System.Security.Principal;
using System.Threading;

namespace BrowseRouter.Host;

/// <summary>
/// Per-user, per-session mutex guard for the Host daemon. The mutex is named in the
/// <c>Global\</c> namespace so that side-by-side sessions for the same user (e.g.
/// RDP into the same console) each get their own Host.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        Acquired = owned;
    }

    /// <summary>
    /// True if THIS process won the race and owns the singleton role.
    /// </summary>
    public bool Acquired { get; }

    /// <summary>
    /// Try to acquire the singleton. Returns an object whose <see cref="Acquired"/>
    /// indicates success. Always call <see cref="Dispose"/> to release.
    /// </summary>
    public static SingleInstance TryAcquire()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        var session = Interop.Kernel32.GetCurrentSessionId();
        // Mutex names must not contain backslashes (legal in SID but illegal in
        // mutex-name syntax outside the Global\ or Local\ prefix). Rewrite hyphens through.
        var nameTail = $"{Constants.AppId}.Host.{sid}.{session}".Replace('\\', '-');
        var name = $"Local\\{nameTail}";

        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    public void Dispose()
    {
        try
        {
            if (Acquired)
                _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            /* not owned — ignored */
        }

        _mutex.Dispose();
    }
}