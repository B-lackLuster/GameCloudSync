namespace GameCloudSync.Core.Models;

public enum SyncStatusKind
{
    Synced,
    LocalNew,
    CloudNew,
    None,
    Paused,
    Unknown
}

public sealed record SyncStatus(string Text, SyncStatusKind Kind);
