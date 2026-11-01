using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Util;

public enum GameNameError
{
    None,
    Empty,
    TooLong,
    InvalidChars,
    Reserved,
    Duplicate
}

/// <summary>
/// Game names double as the backup folder names on OneDrive and WebDAV, so they must be valid,
/// unambiguous folder names and unique regardless of case.
/// </summary>
public static class GameNames
{
    public const int MaxLength = 100;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <param name="self">The game being edited, which may keep its own name.</param>
    public static GameNameError Validate(string name, IEnumerable<GameConfig> existing, GameConfig? self = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return GameNameError.Empty;
        if (name.Length > MaxLength)
            return GameNameError.TooLong;
        // Windows drops trailing dots and spaces, so "Game." would silently share "Game"'s folder.
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != name.Trim() || name.EndsWith('.'))
            return GameNameError.InvalidChars;
        if (ReservedDeviceNames.Contains(name.Split('.')[0])
            || string.Equals(name, Constants.CloudConfigBackupDir, StringComparison.OrdinalIgnoreCase))
            return GameNameError.Reserved;
        // Folders ignore case on Windows, so "Elden Ring" and "elden ring" would share one backup folder.
        if (existing.Any(g => !ReferenceEquals(g, self) && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            return GameNameError.Duplicate;
        return GameNameError.None;
    }

    public static string Describe(GameNameError error, string name) => error switch
    {
        GameNameError.Empty => Localization.T("Name.Empty"),
        GameNameError.TooLong => Localization.T("Name.TooLong", MaxLength),
        GameNameError.InvalidChars => Localization.T("Name.InvalidChars"),
        GameNameError.Reserved => Localization.T("Name.Reserved", name),
        GameNameError.Duplicate => Localization.T("Name.Duplicate", name),
        _ => ""
    };
}
