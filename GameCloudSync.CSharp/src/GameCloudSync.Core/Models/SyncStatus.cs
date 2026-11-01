namespace GameCloudSync.Core.Models;

public enum SyncStatusKind
{
    Synced,
    LocalNew,
    CloudNew,
    None,
    Paused,
    Unknown,
    /// <summary>Backups can't work until something is fixed (bad path, wrong password, missing folder).</summary>
    Error
}

/// <param name="LastBackup">When the newest backup was made, if one is known.</param>
/// <param name="Detail">The full explanation behind a short <paramref name="Text"/>.</param>
public sealed record SyncStatus(string Text, SyncStatusKind Kind, DateTime? LastBackup = null, string? Detail = null);
