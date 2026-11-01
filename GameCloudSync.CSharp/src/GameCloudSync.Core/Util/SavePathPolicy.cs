using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Util;

public enum SavePathError
{
    None,
    Empty,
    Invalid,
    NotAbsolute,
    DriveRoot,
    TooBroad,
    ContainsBackupFolder
}

/// <summary>
/// A save folder is watched, copied to the cloud and — on restore — replaced as a whole, so a folder
/// that is far bigger than one game's save (a whole drive, the user profile, Documents) or that holds
/// its own backups must never be accepted.
/// </summary>
public static class SavePathPolicy
{
    /// <param name="cloudRoot">The OneDrive backup root, or empty when the game isn't backed up there.</param>
    /// <param name="protectedFolders">Folders a save folder may not be or contain; defaults to the system's own.</param>
    public static SavePathError Validate(string? savePath, string gameName, string cloudRoot,
        IEnumerable<string>? protectedFolders = null)
    {
        if (string.IsNullOrWhiteSpace(savePath))
            return SavePathError.Empty;
        var path = savePath.Trim();
        // .NET's own list of invalid path characters leaves out the wildcards and < > ", which Windows refuses too.
        // The \\?\ and \\.\ prefixes legitimately contain a question mark or dot.
        var body = path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? path[4..]
            : path;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || body.IndexOfAny(['*', '?', '"', '<', '>']) >= 0)
            return SavePathError.Invalid;
        // "Saves\Game" would mean a different folder depending on where the app was started from.
        if (!Path.IsPathFullyQualified(path))
            return SavePathError.NotAbsolute;

        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return SavePathError.Invalid;
        }
        if (Path.GetDirectoryName(full) is null)
            return SavePathError.DriveRoot;

        foreach (var folder in protectedFolders ?? DefaultProtectedFolders())
        {
            if (TryNormalize(folder) is { } protectedFull && IsSameOrAncestor(full, protectedFull))
                return SavePathError.TooBroad;
        }

        // Backups are written to <cloud root>\<game>; inside the save folder they would trigger more backups.
        if (!string.IsNullOrWhiteSpace(cloudRoot) && TryNormalize(Path.Combine(cloudRoot.Trim(), gameName ?? "")) is { } backups
            && IsSameOrAncestor(full, backups))
            return SavePathError.ContainsBackupFolder;

        return SavePathError.None;
    }

    public static string Describe(SavePathError error) => error switch
    {
        SavePathError.Empty => Localization.T("SavePath.Empty"),
        SavePathError.Invalid => Localization.T("SavePath.Invalid"),
        SavePathError.NotAbsolute => Localization.T("SavePath.NotAbsolute"),
        SavePathError.DriveRoot => Localization.T("SavePath.DriveRoot"),
        SavePathError.TooBroad => Localization.T("SavePath.TooBroad"),
        SavePathError.ContainsBackupFolder => Localization.T("SavePath.ContainsBackups"),
        _ => ""
    };

    /// <summary>The user's own folders (and the system's) that hold far more than one game's save.</summary>
    public static IReadOnlyList<string> DefaultProtectedFolders()
    {
        var folders = new List<string>();
        void Add(string? folder)
        {
            if (!string.IsNullOrWhiteSpace(folder))
                folders.Add(folder);
        }
        string Known(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        var profile = Known(Environment.SpecialFolder.UserProfile);
        Add(profile);
        Add(Known(Environment.SpecialFolder.DesktopDirectory));
        Add(Known(Environment.SpecialFolder.MyDocuments));
        Add(Known(Environment.SpecialFolder.MyPictures));
        Add(Known(Environment.SpecialFolder.MyMusic));
        Add(Known(Environment.SpecialFolder.MyVideos));
        Add(Known(Environment.SpecialFolder.ApplicationData));
        Add(Known(Environment.SpecialFolder.LocalApplicationData));
        Add(Known(Environment.SpecialFolder.CommonApplicationData));
        Add(Known(Environment.SpecialFolder.ProgramFiles));
        Add(Known(Environment.SpecialFolder.ProgramFilesX86));
        Add(Known(Environment.SpecialFolder.Windows));
        Add(Known(Environment.SpecialFolder.System));
        if (!string.IsNullOrWhiteSpace(profile))
        {
            Add(Path.Combine(profile, "Downloads"));
            Add(Path.Combine(profile, "AppData"));
            Add(Path.Combine(profile, "AppData", "LocalLow"));
        }
        Add(Environment.GetEnvironmentVariable("OneDrive"));
        Add(Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add(Environment.GetEnvironmentVariable("OneDriveCommercial"));
        // Config, logs and restore snapshots live here, and the running exe can't be replaced.
        Add(AppContext.BaseDirectory);
        return folders;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string? TryNormalize(string path)
    {
        try
        {
            return Normalize(path);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return null;
        }
    }

    private static bool IsPathException(Exception ex) =>
        ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException;

    private static bool IsSameOrAncestor(string ancestor, string path) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
