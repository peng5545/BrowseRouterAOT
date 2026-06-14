using BrowseRouter.Host.Logging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BrowseRouter.Host.Config;

/// <summary>
/// Watches the config file for changes and re-loads on edit, with a 300 ms debounce
/// so multi-event saves (e.g. atomic-replace by editors, AV touching the file)
/// don't trigger N reloads. Bad-JSON reloads are logged but DO NOT replace the
/// last-known-good snapshot in <see cref="ConfigStore"/>.
/// </summary>
internal sealed class ConfigWatcher : IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly FileSystemWatcher _fsw;
    private readonly ConfigStore _store;
    private readonly FileLogger _log;
    private readonly Action? _onReload;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _pending;

    // Interlocked gate for DoReload: ensures two parallel debounce expirations
    // (or a manual ReloadNow racing a debounced reload) don't both deserialize
    // the same file. The losing caller observes _reloading==1 and bails.
    private int _reloading;

    public ConfigWatcher(ConfigStore store, FileLogger log, Action? onReload = null)
    {
        _store = store;
        _log = log;
        _onReload = onReload;

        // ConfigFile is a fully qualified path under %AppData% that always has a
        // parent directory on Windows; the throw is a type-system assertion.
        var dir = Path.GetDirectoryName(ConfigPaths.ConfigFile) ??
                  throw new InvalidOperationException(
                      $"Config file path has no directory component: {ConfigPaths.ConfigFile}");
        var file = Path.GetFileName(ConfigPaths.ConfigFile);

        _fsw = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite |
                           NotifyFilters.CreationTime |
                           NotifyFilters.Size |
                           NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false
        };
        _fsw.Changed += OnChanged;
        _fsw.Created += OnChanged;
        _fsw.Renamed += OnChanged;
    }

    /// <summary>
    /// Start watching. Idempotent.
    /// </summary>
    public void Start()
    {
        _fsw.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Force a synchronous reload (e.g. on startup or from the tray menu).
    /// </summary>
    public void ReloadNow()
    {
        DoReload();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: cancel any pending reload and schedule a new one. The CTS pattern
        // doubles as cancellation if the watcher itself is disposed mid-wait.
        // Dispose the superseded CTS so its registered callbacks (Task.Delay's
        // cancellation registration, primarily) don't pile up if the user
        // hot-reloads the config in a tight loop.
        CancellationTokenSource cts;
        lock (_gate)
        {
            var old = _pending;
            _pending = new CancellationTokenSource();
            cts = _pending;
            old?.Cancel();
            old?.Dispose();
        }

        _ = DelayedReload(cts.Token);
    }

    private async Task DelayedReload(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceWindow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Token fired — a newer event superseded us. Drop this reload.
            return;
        }
        catch (ObjectDisposedException)
        {
            // The CTS backing our token was disposed (e.g. watcher torn down
            // mid-debounce). The next event will be ignored anyway, so just exit.
            return;
        }

        DoReload();
    }

    private void DoReload()
    {
        // Serialize concurrent reloads. The first caller does the work; the rest
        // (which can include a manual ReloadNow racing a debounced one) bail
        // rather than re-parse the same file. The interlocked release is the
        // last thing we do so a later reload that arrives mid-parse still gets
        // its turn.
        if (Interlocked.CompareExchange(ref _reloading, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Spurious change events (AV, editor save-without-changes) will
            // re-run the full reload path. That's harmless: ConfigLoader.TryLoad
            // re-parses identical content into an equal RootConfig, ConfigStore
            // accepts a same-content replacement, and the onReload hook
            // (re-applying log + notifier options) is idempotent.
            var path = ConfigPaths.ConfigFile;
            var next = ConfigLoader.TryLoad(path, out var error, _log);
            if (next is null)
            {
                _log.Warn(
                    $"Config reload failed; keeping previous snapshot. {error?.GetType().Name}: {error?.Message}");
                return;
            }

            _store.Replace(next);
            _log.Info(
                $"Config reloaded: {next.Rules.Count} rules, {next.SourceRules.Count} source rules, {next.Filters.Count} filters, {next.Browsers.Count} browsers.");
            try
            {
                _onReload?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error("Post-reload hook", ex);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reloading, 0);
        }
    }

    public void Dispose()
    {
        _fsw.EnableRaisingEvents = false;
        _fsw.Changed -= OnChanged;
        _fsw.Created -= OnChanged;
        _fsw.Renamed -= OnChanged;
        _fsw.Dispose();
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}