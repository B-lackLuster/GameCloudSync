using System.Threading.Channels;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

/// <summary>
/// Watches game save folders with FileSystemWatcher and backs a game up once its folder has
/// been quiet for a moment (never later than a maximum delay). Backups run one at a time on a
/// background worker; a change that arrives while another backup is running is queued and
/// backed up afterwards, never dropped.
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private readonly BackupService _backupService;
    private readonly Func<AppConfig> _getConfig;
    private readonly Func<string> _getPassword;
    private readonly Action<string> _log;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maxDelay;
    private readonly object _lock = new();
    private readonly List<GameWatcher> _watchers = new();
    private readonly HashSet<GameWatcher> _queued = new();
    // Kept across Stop/Start, keyed by save folder (which survives a rename), so a restart
    // neither re-uploads unchanged saves nor forgets pending changes.
    private readonly Dictionary<string, string> _backedUpFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingSaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<GameWatcher> _channel;
    private readonly CancellationTokenSource _cts = new();

    public MonitoringService(
        BackupService backupService,
        Func<AppConfig> getConfig,
        Func<string> getPassword,
        Action<string> log,
        TimeSpan? quietPeriod = null,
        TimeSpan? maxDelay = null)
    {
        _backupService = backupService;
        _getConfig = getConfig;
        _getPassword = getPassword;
        _log = log;
        _quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(Constants.BackupQuietSeconds);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(Constants.BackupMaxDelaySeconds);
        _channel = Channel.CreateUnbounded<GameWatcher>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _ = Task.Run(RunWorkerAsync);
    }

    public event Action? StateChanged;
    public event Action? BackupFinished;

    public bool IsMonitoring
    {
        get
        {
            lock (_lock)
                return _watchers.Count > 0;
        }
    }

    public int MonitoredCount
    {
        get
        {
            lock (_lock)
                return _watchers.Count;
        }
    }

    public bool Start()
    {
        int count;
        lock (_lock)
        {
            if (_watchers.Count > 0)
                return true;

            var config = _getConfig();
            var games = config.Games.Where(g => !g.Paused && g.BackupMethods.Count > 0).ToList();
            if (games.Count == 0)
                return false;

            foreach (var game in games)
            {
                if (!Directory.Exists(game.Save))
                {
                    try
                    {
                        Directory.CreateDirectory(game.Save);
                    }
                    catch (Exception ex)
                    {
                        _log(Localization.T("Log.MonitorDirCreateFail", game.Name, ex.Message));
                        continue;
                    }
                }
                GameWatcher watcher;
                try
                {
                    watcher = new GameWatcher(game, _quietPeriod, _maxDelay, Enqueue, _log);
                }
                catch (Exception ex)
                {
                    _log(Localization.T("Log.MonitorGameFail", game.Name, ex.Message));
                    continue;
                }
                if (!watcher.Start())
                {
                    watcher.Dispose();
                    continue;
                }
                _watchers.Add(watcher);
                // A change seen before the restart still needs its backup.
                if (_pendingSaves.Contains(SaveKey(game)))
                    watcher.MarkChanged();
            }
            _pendingSaves.RemoveWhere(key => _watchers.Any(w => SaveKey(w.Game) == key));
            count = _watchers.Count;
        }
        RaiseStateChanged();
        _log(Localization.T("Log.MonitorStarted", count));
        return count > 0;
    }

    public void Stop()
    {
        List<GameWatcher> watchers;
        lock (_lock)
        {
            watchers = _watchers.ToList();
            _watchers.Clear();
        }
        foreach (var watcher in watchers)
            watcher.Dispose();
        lock (_lock)
        {
            foreach (var watcher in watchers.Where(w => w.PendingAtDispose))
                _pendingSaves.Add(SaveKey(watcher.Game));
        }
        RaiseStateChanged();
        _log(Localization.T("Log.MonitorStopped"));
    }

    public void RestartIfActive()
    {
        if (!IsMonitoring)
            return;
        Stop();
        Start();
    }

    private void Enqueue(GameWatcher watcher)
    {
        lock (_lock)
        {
            // One waiting run per game is enough: it backs up whatever the folder holds when it starts.
            if (!_queued.Add(watcher))
                return;
        }
        if (!_channel.Writer.TryWrite(watcher))
        {
            lock (_lock)
                _queued.Remove(watcher);
        }
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var watcher in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                lock (_lock)
                {
                    _queued.Remove(watcher);
                    if (watcher.IsDisposed)
                    {
                        // Monitoring restarted while this run waited: hand it to the new watcher.
                        var live = _watchers.FirstOrDefault(w => SaveKey(w.Game) == SaveKey(watcher.Game));
                        if (live is not null)
                            live.MarkChanged();
                        else
                            _pendingSaves.Add(SaveKey(watcher.Game));
                        continue;
                    }
                }
                try
                {
                    if (await BackupIfChangedAsync(watcher.Game))
                        RaiseBackupFinished();
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log(Localization.T("Log.TaskError", ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private async Task<bool> BackupIfChangedAsync(GameConfig game)
    {
        if (!FileUtil.HasAnyFile(game.Save))
            return false;
        var fingerprint = FileUtil.Fingerprint(game.Save);
        lock (_lock)
        {
            // Nothing new since the last backup (e.g. a save rewritten with identical files).
            if (_backedUpFingerprints.TryGetValue(SaveKey(game), out var last) && last == fingerprint)
                return false;
        }
        var result = await _backupService.PerformBackupAsync(game, _getConfig(), _getPassword(), _log, _cts.Token);
        if (result.Count > 0)
        {
            lock (_lock)
                _backedUpFingerprints[SaveKey(game)] = fingerprint;
        }
        return true;
    }

    private static string SaveKey(GameConfig game) =>
        Path.TrimEndingDirectorySeparator(game.Save).ToUpperInvariant();

    private void RaiseStateChanged() => RaiseSafely(StateChanged);

    private void RaiseBackupFinished() => RaiseSafely(BackupFinished);

    private void RaiseSafely(Action? handlers)
    {
        if (handlers is null)
            return;
        // A failing subscriber (e.g. UI code called from the wrong thread) must not leave
        // monitoring half stopped or kill the backup worker.
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _log(Localization.T("Log.TaskError", ex.Message));
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Stop();
        _channel.Writer.TryComplete();
    }

    private sealed class GameWatcher : IDisposable
    {
        private readonly TimeSpan _quietPeriod;
        private readonly TimeSpan _maxDelay;
        private readonly Action<GameWatcher> _onDue;
        private readonly Action<string> _log;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;
        private readonly object _lock = new();
        private DateTime? _firstChangeUtc;
        private bool _disposed;

        public GameWatcher(GameConfig game, TimeSpan quietPeriod, TimeSpan maxDelay, Action<GameWatcher> onDue,
            Action<string> log)
        {
            Game = game;
            _quietPeriod = quietPeriod;
            _maxDelay = maxDelay;
            _onDue = onDue;
            _log = log;
            _watcher = new FileSystemWatcher(game.Save)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = false
            };
            _timer = new Timer(_ => OnDue(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += OnWatcherError;
        }

        public GameConfig Game { get; }

        /// <summary>True when a change was still waiting for its quiet period at disposal.</summary>
        public bool PendingAtDispose { get; private set; }

        public bool IsDisposed
        {
            get
            {
                lock (_lock)
                    return _disposed;
            }
        }

        public bool Start()
        {
            try
            {
                _watcher.EnableRaisingEvents = true;
                return true;
            }
            catch (Exception ex)
            {
                _log(Localization.T("Log.MonitorGameFail", Game.Name, ex.Message));
                return false;
            }
        }

        public void MarkChanged()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                var now = DateTime.UtcNow;
                _firstChangeUtc ??= now;
                // Wait until the game has finished writing, but never postpone past the max delay.
                var untilDeadline = _firstChangeUtc.Value + _maxDelay - now;
                var due = untilDeadline < _quietPeriod ? untilDeadline : _quietPeriod;
                _timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            // Folder entries change whenever files inside them do; only file events matter.
            if (Directory.Exists(e.FullPath))
                return;
            MarkChanged();
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // Events were lost, so assume the folder changed.
            if (e.GetException() is InternalBufferOverflowException)
                MarkChanged();
            else
                _log(Localization.T("Log.MonitorWatchError", Game.Name, e.GetException().Message));
        }

        private void OnDue()
        {
            lock (_lock)
            {
                if (_disposed || _firstChangeUtc is null)
                    return;
                _firstChangeUtc = null;
            }
            _onDue(this);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                PendingAtDispose = _firstChangeUtc is not null;
                _timer.Dispose();
            }
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
        }
    }
}
