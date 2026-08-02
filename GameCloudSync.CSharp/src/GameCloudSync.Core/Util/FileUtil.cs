using System.Text.RegularExpressions;

namespace GameCloudSync.Core.Util;

public static partial class FileUtil
{
    public static long GetLatestMTime(string folder)
    {
        long latest = 0;
        if (!Directory.Exists(folder))
            return 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds();
                    if (mtime > latest)
                        latest = mtime;
                }
                catch
                {
                    // ignore unreadable files
                }
            }
        }
        catch
        {
            // ignore
        }
        if (latest == 0)
        {
            try
            {
                latest = new DateTimeOffset(File.GetLastWriteTimeUtc(folder)).ToUnixTimeSeconds();
            }
            catch
            {
                // ignore
            }
        }
        return latest;
    }

    public static long GetFolderSize(string folder)
    {
        long total = 0;
        if (!Directory.Exists(folder))
            return 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
        return total;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes > 1024L * 1024L)
            return $"{bytes / 1024.0 / 1024.0:F2} MB";
        if (bytes > 1024L)
            return $"{bytes / 1024.0:F2} KB";
        return $"{bytes} B";
    }

    public static async Task CopyDirectoryAsync(string sourceDir, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var source = new DirectoryInfo(sourceDir);
        foreach (var dir in source.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir.FullName)));
        }

        var files = source.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (file, token) =>
            {
                var relative = Path.GetRelativePath(sourceDir, file.FullName);
                var dest = Path.Combine(destDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await CopyFileAsync(file.FullName, dest, token);
            });
    }

    public static async Task CopyFileAsync(string source, string dest, CancellationToken ct = default)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
        await using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await input.CopyToAsync(output, ct);
        }
        // Preserve the source modification time like Python's shutil.copytree (copy2).
        // Must happen after the destination handle is closed, otherwise the OS
        // overwrites the timestamp when the handle is finalized.
        try
        {
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Deletes a directory safely, handling read-only files (mirrors rmtree_safe).</summary>
    public static void DeleteDirectorySafe(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
                // ignore
            }
        }
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Retry once after a short delay for transient locks.
            Thread.Sleep(200);
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>Rewrites a save path so C:\Users\&lt;olduser&gt;\ becomes the current user's profile.</summary>
    public static string FixSavePath(string saveDir)
    {
        var currentUser = Environment.GetEnvironmentVariable("USERNAME");
        if (string.IsNullOrEmpty(currentUser))
            return saveDir;
        return UserPathRegex().Replace(saveDir, $@"C:\Users\{currentUser}\");
    }

    /// <summary>Applies the mtime encoded in a WebDAV backup folder name to all files under targetDir.</summary>
    public static void ApplyBackupMTime(string backupName, string targetDir)
    {
        var (_, mtime) = BackupName.Parse(backupName);
        if (mtime <= 0 || !Directory.Exists(targetDir))
            return;
        var stamp = DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetLastWriteTimeUtc(file, stamp);
            }
            catch
            {
                // best effort
            }
        }
    }

    public static void TryOpenInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    public static void TryOpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    [GeneratedRegex(@"C:\\Users\\([^\\]+)\\", RegexOptions.IgnoreCase)]
    private static partial Regex UserPathRegex();
}
