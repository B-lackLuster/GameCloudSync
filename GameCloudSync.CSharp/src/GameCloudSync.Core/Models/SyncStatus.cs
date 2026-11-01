namespace GameCloudSync.Core.Models;

public enum SyncStatusKind
{
    Synced,
    LocalNew,
    CloudNew,
    /// <summary>No backup method, or none of its targets is set up: nothing can be backed up yet.</summary>
    NotConfigured,
    /// <summary>Neither this PC nor the cloud holds a save yet.</summary>
    NoSaves,
    Paused,
    /// <summary>Not known yet: the first check (or the WebDAV check) hasn't finished.</summary>
    Unknown,
    /// <summary>Backups can't work until something is fixed (bad path, wrong password, missing folder).</summary>
    Error,
    /// <summary>The local save and the cloud both changed since they were last in sync on this PC.</summary>
    Conflict
}

/// <param name="LastBackup">When the newest backup was made, if one is known.</param>
/// <param name="Detail">The full explanation behind a short <paramref name="Text"/>.</param>
public sealed record SyncStatus(string Text, SyncStatusKind Kind, DateTime? LastBackup = null, string? Detail = null);
