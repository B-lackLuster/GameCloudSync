using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public class FileUtilTests
{
    [Fact]
    public void FixSavePath_ReplacesOldUserWithThisProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var fixedPath = FileUtil.FixSavePath(@"C:\Users\olduser\Documents\My Games\EldenRing");

        Assert.Equal(Path.Combine(profile, "Documents", "My Games", "EldenRing"), fixedPath);
    }

    [Fact]
    public void FixSavePath_ExpandsAppDataStyleVariables()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA")!;

        Assert.Equal(Path.Combine(appData, "Game", "Saves"), FileUtil.FixSavePath(@"%APPDATA%\Game\Saves"));
    }

    [Fact]
    public void FixSavePath_LeavesProfilesEveryUserSharesAlone()
    {
        Assert.Equal(@"C:\Users\Public\Documents\Game", FileUtil.FixSavePath(@"C:\Users\Public\Documents\Game"));
        Assert.Equal(@"D:\Users\someone\Saves", FileUtil.FixSavePath(@"D:\Users\someone\Saves"));
    }

    [Fact]
    public void ExpandSavePath_LeavesPlainPathsAndUnknownVariablesAlone()
    {
        Assert.Equal(@"D:\Games\Saves", FileUtil.ExpandSavePath(@"D:\Games\Saves"));
        Assert.Equal(@"%GCS_NO_SUCH_VARIABLE%\Saves", FileUtil.ExpandSavePath(@"%GCS_NO_SUCH_VARIABLE%\Saves"));
        Assert.Equal("", FileUtil.ExpandSavePath(""));
    }

    [Fact]
    public void FormatSize_HandlesUnits()
    {
        Assert.Equal("500 B", FileUtil.FormatSize(500));
        Assert.Equal("1.50 KB", FileUtil.FormatSize(1536));
        Assert.Equal("2.00 MB", FileUtil.FormatSize(2 * 1024 * 1024));
    }

    [Fact]
    public void GetLatestMTime_EmptyOrMissingDirectory_ReturnsZero()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"gcs_missing_{Guid.NewGuid():N}");
        Assert.Equal(0, FileUtil.GetLatestMTime(missing));

        var empty = Path.Combine(Path.GetTempPath(), $"gcs_empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Equal(0, FileUtil.GetLatestMTime(empty));
        }
        finally
        {
            Directory.Delete(empty);
        }

        var withFile = Path.Combine(Path.GetTempPath(), $"gcs_file_{Guid.NewGuid():N}");
        Directory.CreateDirectory(withFile);
        try
        {
            var f = Path.Combine(withFile, "save.dat");
            File.WriteAllText(f, "x");
            Assert.True(FileUtil.GetLatestMTime(withFile) > 0);
        }
        finally
        {
            Directory.Delete(withFile, true);
        }
    }

    [Fact]
    public async Task CopyDirectoryAsync_PreservesFilesAndMTime()
    {
        var src = Path.Combine(Path.GetTempPath(), $"gcs_src_{Guid.NewGuid():N}");
        var dst = Path.Combine(Path.GetTempPath(), $"gcs_dst_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            var file = Path.Combine(src, "sub", "save.dat");
            await File.WriteAllTextAsync(file, "data");
            var stamp = new DateTime(2020, 5, 12, 14, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(file, stamp);

            await FileUtil.CopyDirectoryAsync(src, dst);

            var copied = Path.Combine(dst, "sub", "save.dat");
            Assert.True(File.Exists(copied));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(copied));
        }
        finally
        {
            Directory.Delete(src, true);
            Directory.Delete(dst, true);
        }
    }
}
