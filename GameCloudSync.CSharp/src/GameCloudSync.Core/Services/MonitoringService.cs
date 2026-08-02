using System.Threading.Channels;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

/// <summary>
/// Watches game save folders with FileSystemWatcher and runs backups on a
/// single background worker with atomic try-submit semantics (no pile-up,
/// no TOCTOU race), matching the Python original's BackgroundWorker design.
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private readonly BackupService _backupService;
    private readonly Func<AppConfig> _getConfig;
    private readonly Func<string> _getPassword;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly List<GameWatcher> _watchers = new();
    private readonly Channel<Func<Task>> _channel;
    private readonly CancellationTokenSource _cts = new();
    private int _busy;

    public MonitoringService(
        BackupService backupService,
        Func<AppConfig> getConfig,
        Func<string> getPassword,
        Action<string> log)
    {
        _backupService = backupService;
        _getConfig = getConfig;
        _getPassword = getPassword;
        _log = log;
        _channel = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
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
                        _log($"⚠ {game.Name} 存档目录创建失败: {ex.Message}");
                        continue;
                    }
                }
                var watcher = new GameWatcher(game, OnBackupTriggered, _log);
                if (watcher.Start())
                    _watchers.Add(watcher);
            }
        }
        StateChanged?.Invoke();
        _log($"✓ 已启动，当前监控 {_watchers.Count} 个游戏。");
        return _watchers.Count > 0;
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
        StateChanged?.Invoke();
        _log("⏹ 所有监控已暂停。");
    }

    public void RestartIfActive()
    {
        if (!IsMonitoring)
            return;
        Stop();
        Start();
    }

    private void OnBackupTriggered(GameConfig game)
    {
        TrySubmit(() => _backupService.PerformBackupAsync(game, _getConfig(), _getPassword(), _log));
    }

    /// <summary>Atomically enqueues a task only when the worker is idle.</summary>
    public bool TrySubmit(Func<Task> task)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;
        if (!_channel.Writer.TryWrite(async () =>
        {
            try
            {
                await task();
            }
            catch (Exception ex)
            {
                _log($"后台任务异常: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                BackupFinished?.Invoke();
            }
        }))
        {
            Interlocked.Exchange(ref _busy, 0);
            return false;
        }
        return true;
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var task in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await task();
                }
                catch
                {
                    // handled inside the wrapper
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
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
        private readonly GameConfig _game;
        private readonly Action<GameConfig> _onTrigger;
        private readonly Action<string> _log;
        private readonly FileSystemWatcher _watcher;
        private readonly object _lock = new();
        private long _lastBackupTime;
        private long _lastBackupMTime;

        public GameWatcher(GameConfig game, Action<GameConfig> onTrigger, Action<string> log)
        {
            _game = game;
            _onTrigger = onTrigger;
            _log = log;
            _watcher = new FileSystemWatcher(game.Save)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = false
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnCreated;
            _watcher.Renamed += OnRenamed;
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
                _log($"⚠ {_game.Name} 监控失败: {ex.Message}");
                return false;
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (!e.ChangeType.HasFlag(WatcherChangeTypes.Changed) || Directory.Exists(e.FullPath))
                return;
            Backup();
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            if (Directory.Exists(e.FullPath))
                return;
            Backup();
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (!Directory.Exists(e.FullPath))
                Backup();
        }

        private void Backup()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (_lock)
            {
                if (now - _lastBackupTime < Constants.CooldownSeconds)
                    return;
                var currentMTime = FileUtil.GetLatestMTime(_game.Save);
                if (currentMTime <= _lastBackupMTime)
                    return;
                _lastBackupTime = now;
                _lastBackupMTime = currentMTime;
            }
            _onTrigger(_game);
        }

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnCreated;
            _watcher.Renamed -= OnRenamed;
            _watcher.Dispose();
        }
    }
}
