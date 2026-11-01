using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public sealed class SyncBaselineStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gcs_baseline_{Guid.NewGuid():N}");

    public SyncBaselineStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public void GameNeverSyncedHere_HasNoBaseline()
    {
        Assert.Equal(0, new SyncBaselineStore(_dir).Get("Elden Ring"));
    }

    [Fact]
    public void Set_IsKeptOnDisk_AndNamesIgnoreCaseLikeFolders()
    {
        new SyncBaselineStore(_dir).Set("Elden Ring", 1_700_000_000);

        Assert.Equal(1_700_000_000, new SyncBaselineStore(_dir).Get("elden ring"));
    }

    [Fact]
    public void Rename_CarriesTheBaselineToTheNewName()
    {
        var store = new SyncBaselineStore(_dir);
        store.Set("Old name", 42);

        store.Rename("Old name", "New name");

        Assert.Equal(0, store.Get("Old name"));
        Assert.Equal(42, new SyncBaselineStore(_dir).Get("New name"));
    }

    [Fact]
    public void Remove_ForgetsTheGame()
    {
        var store = new SyncBaselineStore(_dir);
        store.Set("A", 42);
        store.Set("B", 43);

        store.Remove("a");

        var reloaded = new SyncBaselineStore(_dir);
        Assert.Equal(0, reloaded.Get("A"));
        Assert.Equal(43, reloaded.Get("B"));
    }

    [Fact]
    public void DamagedFile_CountsAsNeverSynced_AndIsWrittenAgain()
    {
        var store = new SyncBaselineStore(_dir);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Equal(0, store.Get("A"));
        store.Set("A", 7);

        Assert.Equal(7, new SyncBaselineStore(_dir).Get("A"));
    }

    [Fact]
    public void UnknownTime_IsNotStored()
    {
        var store = new SyncBaselineStore(_dir);

        store.Set("A", 0);

        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void FolderThatCantBeWritten_DoesNotThrow()
    {
        var store = new SyncBaselineStore(Path.Combine(_dir, "missing", "folder"));

        store.Set("A", 42);

        // Kept for this run; only remembering it across restarts is lost.
        Assert.Equal(42, store.Get("A"));
    }
}
