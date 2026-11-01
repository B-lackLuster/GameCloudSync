namespace GameCloudSync.Core;

public static class Constants
{
    public const string ConfigFileName = "config.json";
    public const string WakeupFileName = "wakeup.tmp";
    public const string CredFileName = ".gcs_webdav.cred";
    public const string SaltFileName = ".gcs_salt";
    public const string MutexName = "Global\\GameCloudSync_Unique_Mutex";

    // Back up once a save folder has been quiet this long, but never wait longer than the max delay.
    public const int BackupQuietSeconds = 5;
    public const int BackupMaxDelaySeconds = 120;
    public const int DefaultMaxBackups = 10;
    public const int MaxRestoreSnapshots = 5;
    public const int WebDavWorkers = 4;
    public const int MaxConfigBackups = 10;
    public const int WebDavConfigListLimit = 10;
    public const int SyncThresholdSeconds = 3;

    public const string CloudConfigBackupDir = "云存档配置备份";
    // Backups are written under this prefix and renamed to save_* only once complete.
    public const string IncompleteBackupPrefix = "incomplete_";
    public const string RestoreSnapshotDir = "restore-snapshots";

    public const string MethodOneDrive = "onedrive";
    public const string MethodWebDav = "webdav";
    public const string SourceAsk = "ask";

    public const string AutostartRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AutostartRegName = "GameCloudSync";
    // Passed by the autostart entry so a login start goes straight to the tray.
    public const string StartMinimizedArg = "--minimized";

    public const string LogDirName = "logs";
    public const string LogFileName = "gamecloudsync.log";
    public const long MaxLogFileBytes = 1_000_000;

    public const string TargetOneDrive = "OneDrive";
    public const string TargetWebDav = "WebDAV";
}
