namespace GameCloudSync.Core.Util;

/// <summary>
/// Puts a downloaded version in place of the running exe. Windows doesn't let a running program be overwritten or
/// deleted, but it may be renamed: the running copy moves aside to <c>.old</c>, the new one takes its name, and the
/// next start deletes the old copy once it has exited.
/// </summary>
public static class SelfUpdate
{
    /// <summary>Where the new version is downloaded to: beside the exe, so that putting it in place is a rename.</summary>
    public static string DownloadPath(string exePath) => exePath + ".download";

    /// <summary>Where the replaced version waits until it has exited and can be deleted.</summary>
    public static string OldPath(string exePath) => exePath + ".old";

    /// <summary>Swaps <paramref name="newProgram"/> in for <paramref name="exePath"/>; on failure the exe is left as it was.</summary>
    public static void Install(string exePath, string newProgram)
    {
        var old = OldPath(exePath);
        // Left by an earlier update whose clean-up didn't get to it.
        if (File.Exists(old))
            Retry(() => File.Delete(old));
        Retry(() => File.Move(exePath, old));
        try
        {
            Retry(() => File.Move(newProgram, exePath));
        }
        catch
        {
            // Without a program under its own name, nothing would start next time.
            try
            {
                Retry(() => File.Move(old, exePath));
            }
            catch
            {
                // The previous program stays as .old, where it can still be renamed back by hand.
            }
            throw;
        }
    }

    /// <summary>Gives the previous version its name back, for a new one that won't start. False when that isn't possible.</summary>
    public static bool Revert(string exePath)
    {
        var old = OldPath(exePath);
        try
        {
            if (!File.Exists(old))
                return false;
            Retry(() => File.Move(old, exePath, overwrite: true));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes what an update leaves behind: a download that never finished, and the previous version, which may
    /// still be closing when the new one starts and is waited for up to <paramref name="patience"/>.
    /// </summary>
    /// <returns>True when a previous version was removed.</returns>
    public static async Task<bool> CleanUpAsync(string exePath, TimeSpan patience, CancellationToken ct = default)
    {
        TryDelete(DownloadPath(exePath));
        var old = OldPath(exePath);
        var deadline = DateTime.UtcNow + patience;
        while (File.Exists(old))
        {
            if (TryDelete(old))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(250, ct);
        }
        return false;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Virus scanners and the search indexer open a new file for a moment.
    private static void Retry(Action action, int attempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < attempts && IsTransient(ex))
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    // A file that isn't there won't appear by waiting.
    private static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException ||
        ex is IOException and not FileNotFoundException and not DirectoryNotFoundException;
}
