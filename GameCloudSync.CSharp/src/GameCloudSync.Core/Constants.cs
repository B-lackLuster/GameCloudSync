namespace GameCloudSync.Core;

public static class Constants
{
    public const string ConfigFileName = "config.json";
    public const string WakeupFileName = "wakeup.tmp";
    public const string CredFileName = ".gcs_webdav.cred";
    public const string SaltFileName = ".gcs_salt";
    public const string MutexName = "Global\\GameCloudSync_Unique_Mutex";

    public const int CooldownSeconds = 10;
    public const int DefaultMaxBackups = 10;
    public const int WebDavWorkers = 4;
    public const int MaxConfigBackups = 10;
    public const int WebDavConfigListLimit = 10;
    public const int SyncThresholdSeconds = 3;

    public const string CloudConfigBackupDir = "云存档配置备份";

    public const string MethodOneDrive = "onedrive";
    public const string MethodWebDav = "webdav";
    public const string SourceAsk = "ask";

    public const string AutostartRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AutostartRegName = "GameCloudSync";
}
