namespace GameCloudSync.Core.Models;

public sealed record BackupSource(
    string Label,
    string Method,
    string Path);

public sealed record BackupEntry(
    string Name,
    string Href,
    string DateText,
    long SizeBytes);

public sealed record CloudConfigSummary(
    string Source,
    string Filename,
    IReadOnlyList<GameConfig> Games,
    string Status);
