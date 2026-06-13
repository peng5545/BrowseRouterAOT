using BrowseRouter.Core;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;

namespace BrowseRouter.Host;

/// <summary>
/// Per-user, per-session mutex guard for the Host daemon. The mutex lives in the
/// <c>Local\</c> namespace, so each Windows session (e.g. a separate RDP login
/// for the same user) gets its own Host — they don't contend for one global slot.
/// This is the right scope: cross-user disambiguation is provided by the
/// per-user folder layout, and cross-session isolation is exactly what
/// "side-by-side sessions each get their own Host" needs.
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
    /// <remarks>
    /// Recovers from a stale mutex: if a previous Host crashed without releasing
    /// the mutex, the OS leaves it in the abandoned state, which would otherwise
    /// block every subsequent launch forever. We detect the dead owner by
    /// enumerating <c>BrowseRouter.Host</c> processes; if none are alive we know
    /// the mutex is orphaned and call <c>WaitOne(0)</c> to take it over.
    /// </remarks>
    public static SingleInstance TryAcquire()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        var session = Interop.Kernel32.GetCurrentSessionId();
        // Mutex names must not contain backslashes. SIDs only contain
        // alphanumeric and hyphens, but be defensive in case caller passed a
        // domain-qualified name.
        var nameTail = $"{Constants.AppId}.Host.{sid}.{session}".Replace('\\', '-');
        var name = $"Local\\{nameTail}";

        if (Mutex.TryOpenExisting(name, out var existing))
        {
            // A mutex with our name already exists. The owning process is
            // either live (we lose the race) or dead (we can take over).
            if (IsAnotherHostProcessAlive())
            {
                existing.Dispose();
                return new SingleInstance(existing, owned: false);
            }

            // Owner is dead — try to take over. WaitOne(0) returns true if the
            // mutex is free OR abandoned; false if some racing thread just
            // grabbed it. The AbandonedMutexException path is defensive — the
            // .NET docs say it isn't thrown on the first waiter, but a future
            // runtime could change that.
            try
            {
                var took = existing.WaitOne(0);
                return new SingleInstance(existing, owned: took);
            }
            catch (AbandonedMutexException)
            {
                return new SingleInstance(existing, owned: true);
            }
        }

        // No existing mutex — claim a fresh one. initiallyOwned applies to the
        // newly-created case; ignored for existing mutexes (handled above).
        var mutex = new Mutex(initiallyOwned: true, name, out _);
        return new SingleInstance(mutex, owned: true);
    }

    /// <summary>
    /// True if any <c>BrowseRouter.Host</c> process other than ourselves is alive
    /// in this session. Cheap best-effort — assumes one installation per user
    /// per session (which is the supported deployment).
    /// </summary>
    private static bool IsAnotherHostProcessAlive()
    {
        var self = Environment.ProcessId;
        try
        {
            foreach (var p in Process.GetProcessesByName("BrowseRouter.Host"))
            {
                try
                {
                    if (p.Id != self)
                        return true;
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // If enumeration fails (rare, e.g. transient WMI issue), assume
            // somebody else is alive — better to bail than to trample a
            // legitimate owner.
            return true;
        }

        return false;
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