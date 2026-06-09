using BrowseRouter.Core.Config;
using System.Threading;

namespace BrowseRouter.Host.Config;

/// <summary>
/// Holds the current <see cref="RootConfig"/> snapshot. Readers (e.g. the pipe-server
/// handler) call <see cref="Current"/> for a lock-free, atomic reference. Writers
/// (the watcher reload, or first-time load) call <see cref="Replace"/>; the previous
/// snapshot is retained on null to keep the last-known-good in case of a bad edit.
/// </summary>
internal sealed class ConfigStore
{
    private RootConfig _current = RootConfig.Empty;

    /// <summary>
    /// Current snapshot. Always non-null. Volatile read so changes from the
    /// watcher thread are visible to the pipe-handler threads immediately.
    /// </summary>
    public RootConfig Current => Volatile.Read(ref _current);

    /// <summary>
    /// Replace the snapshot. A null argument is silently ignored.
    /// </summary>
    public void Replace(RootConfig? next)
    {
        if (next is null)
            return;
        Volatile.Write(ref _current, next);
    }
}