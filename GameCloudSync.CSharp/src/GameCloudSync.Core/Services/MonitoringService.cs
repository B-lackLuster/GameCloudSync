using System.Threading.Channels;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

/// <summary>
/// Watches game save folders with FileSystemWatcher and backs a game up once its folder has
/// been quiet for a moment (never later than a maximum delay). Backups run one at a time on a
/// background worker; a change that arrives while another backup is running is queued and
/// backed up afterwards, never dropped. A backup that fails is repeated after a growing pause.
/// </summary>
public sealed class MonitoringService : IDisposable
{
    // Pauses before each repeat of a failed backup; once they are used up the next save change tries again.
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)
    ];

    private readonly BackupService _backupService;
    private readonly Func<AppConfig> _getConfig;
    private readonly Func<string> _getPassword;
    private readonly Action<string> _log;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maxDelay;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly object _lock = new();
    private readonly List<GameWatcher> _watchers = new();
    private readonly HashSet<GameWatcher> _queued = new();
    // Kept across Stop/Start, keyed by save folder (which survives a rename) and target, so a restart
    // neither re-uploads unchanged saves nor forgets pending changes, and a target that already holds
    // a save isn't sent it again just because another target failed.
    private readonly Dictionary<(string Save, string Target), string> _backedUp = new();
    private readonly HashSet<string> _pendingSaves = new(StringComparer.OrdinalIgnoreCase);
    // Games whose latest backup failed, by save folder. Cleared by the next fully successful backup.
    private readonly Dictionary<string, RetryState> _failing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<GameWatcher> _channel;
    private readonly CancellationTokenSource _cts = new();
    // Set by Start even when there was nothing to watch, cleared by Stop.
    private bool _active;
    // A backup taken off the queue is running.
    private bool _backupRunning;

    private sealed class RetryState
    {
        public int Failures;
        public Timer? Timer;
    }

    public MonitoringService(
        BackupService backupService,
        Func<AppConfig> getConfig,
        Func<string> getPassword,
        Action<string> log,
        TimeSpan? quietPeriod = null,
        TimeSpan? maxDelay = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _backupService = backupService;
        _getConfig = getConfig;
        _getPassword = getPassword;
        _log = log;
        _quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(Constants.BackupQuietSeconds);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(Constants.BackupMaxDelaySeconds);
        _retryDelays = retryDelays ?? DefaultRetryDelays;
        _channel = Channel.CreateUnbounded<GameWatcher>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _ = Task.Run(RunWorkerAsync);
    }

    public event Action? StateChanged;
    public event Action? BackupFinished;

    /// <summary>Raised after every automatic backup run with its outcome, successful or not.</summary>
    public event Action<BackupAttempt>? BackupAttempted;

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

    /// <summary>
    /// True from <see cref="Start"/> until <see cref="Stop"/>, also while there is no game to watch (none added
    /// yet, or all paused). <see cref="IsMonitoring"/> is only true once a game is watched.
    /// </summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
                return _active;
        }
    }

    /// <summary>
    /// True while a backup runs or waits to run, or a watched folder has changed and its backup is still to come:
    /// quitting now would leave that change without a backup until the save changes again. Backups waiting to be
    /// repeated after a failure don't count.
    /// </summary>
    public bool HasPendingWork
    {
        get
        {
            lock (_lock)
                return _backupRunning || _queued.Count > 0 || _watchers.Any(w => w.HasPendingChange);
        }
    }

    public bool Start()
    {
        int count;
        lock (_lock)
        {
            _active = true;
            if (_watchers.Count > 0)
                return true;

            var config = _getConfig();
            var games = config.Games.Where(g => !g.Paused && g.BackupMethods.Count > 0).ToList();
            if (games.Count == 0)
                return false;

            foreach (var game in games)
            {
                // Watching (and later backing up or restoring) a folder far bigger than a save would be harmful.
                var pathError = SavePathPolicy.Validate(game.Save, game.Name,
                    game.HasMethod(Constants.MethodOneDrive) ? config.CloudRoot : "");
                if (pathError != SavePathError.None)
                {
                    _log(Localization.T("Log.MonitorGameFail", game.Name, SavePathPolicy.Describe(pathError)));
                    continue;
                }
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
            _active = false;
            watchers = _watchers.ToList();
            _watchers.Clear();
        }
        foreach (var watcher in watchers)
            watcher.Dispose();
        lock (_lock)
        {
            foreach (var watcher in watchers.Where(w => w.PendingAtDispose))
                _pendingSaves.Add(SaveKey(watcher.Game));
            // Waiting retries end with the watchers; starting again tries these games again, with a fresh
            // allowance, which is what a user who just fixed the cause (a password, a folder) wants.
            foreach (var (key, state) in _failing)
            {
                state.Timer?.Dispose();
                state.Timer = null;
                state.Failures = 0;
                _pendingSaves.Add(key);
            }
        }
        RaiseStateChanged();
        // Monitoring that was waiting for a game had nothing to stop.
        if (watchers.Count > 0)
            _log(Localization.T("Log.MonitorStopped"));
    }

    /// <summary>
    /// Picks up changed games or settings. Monitoring that had nothing to watch starts watching as soon as there is
    /// a game, as it would when the app starts; monitoring that was stopped stays off.
    /// </summary>
    public void RestartIfActive()
    {
        if (!IsActive)
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
                    _backupRunning = true;
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
                finally
                {
                    lock (_lock)
                        _backupRunning = false;
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
        var config = _getConfig();
        var key = SaveKey(game);
        var fingerprint = FileUtil.Fingerprint(game.Save);
        List<string> pending;
        lock (_lock)
        {
            // A target that already holds exactly this content has nothing new to receive
            // (e.g. a save rewritten with identical files).
            pending = BackupService.TargetsFor(game, config)
                .Where(target => !_backedUp.TryGetValue((key, target), out var last) || last != fingerprint)
                .ToList();
        }
        if (pending.Count == 0)
        {
            ForgetFailure(key);
            return false;
        }
        var result = await _backupService.RunBackupAsync(game, config, _getPassword(), _log, pending, _cts.Token);
        lock (_lock)
        {
            foreach (var target in result.Succeeded)
                _backedUp[(key, target)] = fingerprint;
        }
        RaiseBackupAttempted(RecordOutcome(game, key, result));
        return true;
    }

    /// <summary>Notes how a backup run went and, after a failure, schedules the next try.</summary>
    private BackupAttempt RecordOutcome(GameConfig game, string key, BackupResult result)
    {
        lock (_lock)
        {
            if (!result.HasFailures)
            {
                ForgetFailure(key);
                return new BackupAttempt(game, result, IsNewFailure: false, RetryIn: null);
            }
            var isNew = !_failing.TryGetValue(key, out var state);
            state ??= _failing[key] = new RetryState();
            state.Failures++;
            state.Timer?.Dispose();
            state.Timer = null;
            TimeSpan? retryIn = state.Failures <= _retryDelays.Count ? _retryDelays[state.Failures - 1] : null;
            if (retryIn is { } delay && !_cts.IsCancellationRequested)
                state.Timer = new Timer(_ => OnRetryDue(key), null, delay, Timeout.InfiniteTimeSpan);
            return new BackupAttempt(game, result, isNew, retryIn);
        }
    }

    private void ForgetFailure(string key)
    {
        lock (_lock)
        {
            if (_failing.Remove(key, out var state))
                state.Timer?.Dispose();
        }
    }

    private void OnRetryDue(string key)
    {
        GameWatcher? live;
        lock (_lock)
        {
            if (_cts.IsCancellationRequested || !_failing.TryGetValue(key, out var state))
                return;
            state.Timer?.Dispose();
            state.Timer = null;
            live = _watchers.FirstOrDefault(w => SaveKey(w.Game) == key);
            if (live is null)
                _pendingSaves.Add(key);
        }
        if (live is not null)
            Enqueue(live);
    }

    private static string SaveKey(GameConfig game) =>
        Path.TrimEndingDirectorySeparator(game.Save).ToUpperInvariant();

    private void RaiseStateChanged() => RaiseSafely(StateChanged);

    private void RaiseBackupFinished() => RaiseSafely(BackupFinished);

    private void RaiseBackupAttempted(BackupAttempt attempt)
    {
        if (BackupAttempted is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<BackupAttempt>>())
        {
            try
            {
                handler(attempt);
            }
            catch (Exception ex)
            {
                _log(Localization.T("Log.TaskError", ex.Message));
            }
        }
    }

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

        /// <summary>A change is waiting for its quiet period, after which its backup is queued.</summary>
        public bool HasPendingChange
        {
            get
            {
                lock (_lock)
                    return !_disposed && _firstChangeUtc is not null;
            }
        }

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
