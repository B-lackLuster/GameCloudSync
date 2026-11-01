using Microsoft.Win32;

namespace GameCloudSync.Core.Util;

/// <summary>Finds the OneDrive folders signed in on this PC, so the backup folder can be suggested instead of typed.</summary>
public static class OneDriveLocator
{
    /// <returns>Existing OneDrive folders, the personal one first.</returns>
    public static IReadOnlyList<string> FindRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            var full = Path.TrimEndingDirectorySeparator(path.Trim());
            if (Directory.Exists(full) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                roots.Add(full);
        }

        Add(Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add(Environment.GetEnvironmentVariable("OneDrive"));
        Add(Environment.GetEnvironmentVariable("OneDriveCommercial"));
        try
        {
            // The variables are missing in some sessions; the OneDrive client also records each account's folder.
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var name in accounts?.GetSubKeyNames() ?? [])
            {
                using var account = accounts!.OpenSubKey(name);
                Add(account?.GetValue("UserFolder") as string);
            }
        }
        catch
        {
            // Not readable: the variables are all there is.
        }
        return roots;
    }

    /// <summary>The backup folder to suggest inside a OneDrive folder: its own subfolder, not the OneDrive root itself.</summary>
    public static string SuggestBackupRoot(string oneDriveRoot) => Path.Combine(oneDriveRoot, Constants.AppFolderName);
}
