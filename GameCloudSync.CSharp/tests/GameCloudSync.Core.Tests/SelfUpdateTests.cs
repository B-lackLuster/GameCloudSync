using System.Diagnostics;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public sealed class SelfUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_selfupdate_{Guid.NewGuid():N}");
    private readonly string _exe;
    private readonly string _download;

    public SelfUpdateTests()
    {
        Directory.CreateDirectory(_root);
        _exe = Path.Combine(_root, "GameCloudSync.exe");
        _download = SelfUpdate.DownloadPath(_exe);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Install_PutsTheNewVersionInPlaceAndKeepsTheOldAside()
    {
        File.WriteAllText(_exe, "2.2.2");
        File.WriteAllText(_download, "2.3.0");

        SelfUpdate.Install(_exe, _download);

        Assert.Equal("2.3.0", File.ReadAllText(_exe));
        Assert.Equal("2.2.2", File.ReadAllText(SelfUpdate.OldPath(_exe)));
        Assert.False(File.Exists(_download));
    }

    [Fact]
    public void Install_ReplacesWhatAnEarlierUpdateLeftBehind()
    {
        File.WriteAllText(_exe, "2.3.0");
        File.WriteAllText(SelfUpdate.OldPath(_exe), "2.2.2");
        File.WriteAllText(_download, "2.4.0");

        SelfUpdate.Install(_exe, _download);

        Assert.Equal("2.4.0", File.ReadAllText(_exe));
        Assert.Equal("2.3.0", File.ReadAllText(SelfUpdate.OldPath(_exe)));
    }

    [Fact]
    public void Install_Failing_LeavesTheProgramAsItWas()
    {
        File.WriteAllText(_exe, "2.2.2");

        // The download is gone (removed by a virus scanner, say).
        Assert.ThrowsAny<IOException>(() => SelfUpdate.Install(_exe, _download));

        Assert.Equal("2.2.2", File.ReadAllText(_exe));
        Assert.False(File.Exists(SelfUpdate.OldPath(_exe)));
    }

    [Fact]
    public void Install_WorksWhileTheProgramRuns()
    {
        // A copy of cmd.exe stands in for the running app: Windows keeps a running program from being deleted or
        // overwritten, which is exactly what an update has to get around.
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), _exe);
        File.WriteAllText(_download, "new version");
        using var running = Process.Start(new ProcessStartInfo(_exe, "/c ping -n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;
        try
        {
            Assert.ThrowsAny<Exception>(() => File.Delete(_exe));

            SelfUpdate.Install(_exe, _download);

            Assert.Equal("new version", File.ReadAllText(_exe));
            Assert.True(File.Exists(SelfUpdate.OldPath(_exe)));
        }
        finally
        {
            running.Kill(entireProcessTree: true);
            running.WaitForExit();
        }
    }

    [Fact]
    public void Revert_GivesThePreviousVersionItsNameBack()
    {
        File.WriteAllText(_exe, "2.2.2");
        File.WriteAllText(_download, "2.3.0");
        SelfUpdate.Install(_exe, _download);

        Assert.True(SelfUpdate.Revert(_exe));

        Assert.Equal("2.2.2", File.ReadAllText(_exe));
        Assert.False(File.Exists(SelfUpdate.OldPath(_exe)));
    }

    [Fact]
    public void Revert_WithoutPreviousVersion_ChangesNothing()
    {
        File.WriteAllText(_exe, "2.3.0");

        Assert.False(SelfUpdate.Revert(_exe));

        Assert.Equal("2.3.0", File.ReadAllText(_exe));
    }

    [Fact]
    public async Task CleanUp_RemovesThePreviousVersionAndAnUnfinishedDownload()
    {
        File.WriteAllText(_exe, "2.3.0");
        File.WriteAllText(SelfUpdate.OldPath(_exe), "2.2.2");
        File.WriteAllText(_download, "half");

        Assert.True(await SelfUpdate.CleanUpAsync(_exe, TimeSpan.FromSeconds(5)));

        Assert.False(File.Exists(SelfUpdate.OldPath(_exe)));
        Assert.False(File.Exists(_download));
        Assert.Equal("2.3.0", File.ReadAllText(_exe));
    }

    [Fact]
    public async Task CleanUp_WaitsForThePreviousVersionToExit()
    {
        var old = SelfUpdate.OldPath(_exe);
        File.WriteAllText(old, "2.2.2");
        var stillRunning = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read);

        var cleanUp = SelfUpdate.CleanUpAsync(_exe, TimeSpan.FromSeconds(10));
        await Task.Delay(600);
        Assert.False(cleanUp.IsCompleted);
        await stillRunning.DisposeAsync();

        Assert.True(await cleanUp);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task CleanUp_NothingLeftBehind_ReportsNothing()
    {
        File.WriteAllText(_exe, "2.3.0");

        Assert.False(await SelfUpdate.CleanUpAsync(_exe, TimeSpan.FromSeconds(1)));
    }
}
