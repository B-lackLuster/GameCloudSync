namespace GameCloudSync.Core.Models;

/// <param name="Target">"OneDrive" or "WebDAV"; several joined by commas when one problem stopped all of them.</param>
public sealed record BackupFailure(string Target, string Message);

/// <summary>What one backup run achieved: the targets that now hold the save and why the others don't.</summary>
public sealed class BackupResult
{
    public List<string> Succeeded { get; } = new();
    public List<BackupFailure> Failures { get; } = new();

    public bool HasFailures => Failures.Count > 0;

    public string FailureSummary =>
        string.Join("; ", Failures.Select(f => f.Target.Length > 0 ? $"{f.Target}: {f.Message}" : f.Message));
}

/// <summary>One automatic backup run, reported by the monitor.</summary>
/// <param name="IsNewFailure">The game's backups were fine before this run; false while a failure keeps repeating.</param>
/// <param name="RetryIn">When the monitor will try again by itself, or null when it has stopped trying.</param>
public sealed record BackupAttempt(GameConfig Game, BackupResult Result, bool IsNewFailure, TimeSpan? RetryIn);
