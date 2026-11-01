using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_datadir_{Guid.NewGuid():N}");
    private readonly string _exeDir;
    private readonly string _fallback;

    public DataDirectoryTests()
    {
        _exeDir = Path.Combine(_root, "Program Files", "GameCloudSync");
        _fallback = Path.Combine(_root, "AppData", "GameCloudSync");
        Directory.CreateDirectory(_exeDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public void WritableProgramFolder_StaysPortable()
    {
        Assert.Equal(_exeDir, DataDirectory.Resolve(_exeDir, _fallback));

        Assert.False(Directory.Exists(_fallback));
        // The write test cleans up after itself.
        Assert.Empty(Directory.EnumerateFileSystemEntries(_exeDir));
    }

    [Fact]
    public void ReadOnlyProgramFolder_MovesToTheFallback_TakingConfigAndPasswordAlong()
    {
        File.WriteAllText(Path.Combine(_exeDir, Constants.ConfigFileName), "{ \"games\": [] }");
        File.WriteAllText(Path.Combine(_exeDir, Constants.CredFileName), "sealed");

        var dir = DataDirectory.Resolve(_exeDir, _fallback, canWrite: _ => false);

        Assert.Equal(_fallback, dir);
        Assert.Equal("{ \"games\": [] }", File.ReadAllText(Path.Combine(_fallback, Constants.ConfigFileName)));
        Assert.Equal("sealed", File.ReadAllText(Path.Combine(_fallback, Constants.CredFileName)));
    }

    [Fact]
    public void FallbackThatAlreadyHasAConfig_KeepsIt()
    {
        Directory.CreateDirectory(_fallback);
        File.WriteAllText(Path.Combine(_fallback, Constants.ConfigFileName), "saved in AppData");
        File.WriteAllText(Path.Combine(_exeDir, Constants.ConfigFileName), "left beside the exe");

        DataDirectory.Resolve(_exeDir, _fallback, canWrite: _ => false);

        Assert.Equal("saved in AppData", File.ReadAllText(Path.Combine(_fallback, Constants.ConfigFileName)));
    }

    [Fact]
    public void IsWritable_FolderThatDoesNotExist_IsFalse()
    {
        Assert.False(DataDirectory.IsWritable(Path.Combine(_root, "missing")));
        Assert.True(DataDirectory.IsWritable(_exeDir));
    }
}
