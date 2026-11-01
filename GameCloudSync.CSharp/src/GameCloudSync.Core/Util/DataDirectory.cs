namespace GameCloudSync.Core.Util;

/// <summary>
/// Where config, credentials, logs and snapshots live: beside the exe (portable, the default), or under
/// %APPDATA% when the exe's folder can't be written (for example in Program Files), where every attempt
/// to save the settings would otherwise fail.
/// </summary>
public static class DataDirectory
{
    // What a portable run leaves beside the exe and a move to %APPDATA% must take along.
    private static readonly string[] CarriedFiles =
    [
        Constants.ConfigFileName,
        Constants.ConfigFileName + ".bak",
        Constants.CredFileName,
        Constants.SaltFileName,
        Constants.SyncStateFileName
    ];

    /// <param name="fallbackDir">Used when <paramref name="exeDir"/> can't be written, normally %APPDATA%\GameCloudSync.</param>
    /// <param name="canWrite">Replaces the write test (for tests).</param>
    public static string Resolve(string exeDir, string fallbackDir, Func<string, bool>? canWrite = null)
    {
        if ((canWrite ?? IsWritable)(exeDir))
            return exeDir;
        try
        {
            Directory.CreateDirectory(fallbackDir);
        }
        catch
        {
            // Nowhere better to go; saving will fail as it did before.
            return exeDir;
        }
        // A config already beside the exe is taken along once, so the move doesn't look like every game was forgotten.
        if (!File.Exists(Path.Combine(fallbackDir, Constants.ConfigFileName)))
        {
            foreach (var name in CarriedFiles)
            {
                try
                {
                    var source = Path.Combine(exeDir, name);
                    if (File.Exists(source))
                        File.Copy(source, Path.Combine(fallbackDir, name), overwrite: false);
                }
                catch
                {
                    // That file stays behind; the app starts without it.
                }
            }
        }
        return fallbackDir;
    }

    public static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".gcs-write-test-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
