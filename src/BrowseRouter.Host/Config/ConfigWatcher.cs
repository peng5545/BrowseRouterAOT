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

    public ConfigWatcher(ConfigStore store, FileLogger log, Action? onReload = null)
    {
        _store = store;
        _log = log;
        _onReload = onReload;

        // ConfigFile is a fully qualified path under %AppData% that always has a
        // parent directory on Windows; the throw is a type-system assertion.
        var dir = Path.GetDirectoryName(ConfigPaths.ConfigFile) ??
                  throw new InvalidOperationException("Config file path has no directory component: " + ConfigPaths.ConfigFile);
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
        CancellationTokenSource cts;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending = new CancellationTokenSource();
            cts = _pending;
        }

        _ = DelayedReload(cts.Token);
    }

    private async Task DelayedReload(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceWindow, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        DoReload();
    }

    private void DoReload()
    {
        var path = ConfigPaths.ConfigFile;
        var next = ConfigLoader.TryLoad(path, out var error, _log);
        if (next is null)
        {
            _log.Warn($"Config reload failed; keeping previous snapshot. {error?.GetType().Name}: {error?.Message}");
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