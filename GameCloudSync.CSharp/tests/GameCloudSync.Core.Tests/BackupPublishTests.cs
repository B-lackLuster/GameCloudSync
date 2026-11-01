using System.Net;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public sealed class BackupPublishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_pub_{Guid.NewGuid():N}");
    private readonly string _save;
    private readonly string _cloud;

    public BackupPublishTests()
    {
        _save = Path.Combine(_root, "save");
        _cloud = Path.Combine(_root, "cloud");
        Directory.CreateDirectory(_save);
        Directory.CreateDirectory(_cloud);
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(_save, $"slot{i:00}.sav"), new string('x', 50_000));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private GameConfig Game(params string[] methods) => new()
    {
        Name = "game",
        Save = _save,
        BackupMethods = methods.ToList()
    };

    [Fact]
    public async Task OneDrive_FileHeldOpenByRunningGame_IsStillBackedUp()
    {
        var config = new AppConfig { CloudRoot = _cloud };
        // Games commonly keep their save open for writing while allowing others to read it.
        await using var held = new FileStream(Path.Combine(_save, "slot05.sav"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.ReadWrite);

        var result = await new BackupService().PerformBackupAsync(Game(Constants.MethodOneDrive), config, "", _ => { });

        Assert.Equal(new[] { "OneDrive" }, result);
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(_cloud, "game")));
        Assert.Equal(12, Directory.GetFiles(backup).Length);
    }

    [Fact]
    public async Task OneDrive_FailedCopy_LeavesNoBackupThatLooksComplete()
    {
        var config = new AppConfig { CloudRoot = _cloud };
        var service = new BackupService();
        Assert.Single(await service.PerformBackupAsync(Game(Constants.MethodOneDrive), config, "", _ => { }));
        var good = Assert.Single(Directory.GetDirectories(Path.Combine(_cloud, "game")));
        await Task.Delay(1100);

        List<string> result;
        await using (new FileStream(Path.Combine(_save, "slot07.sav"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await service.PerformBackupAsync(Game(Constants.MethodOneDrive), config, "", _ => { });
        }

        Assert.Empty(result);
        Assert.Equal(new[] { good }, Directory.GetDirectories(Path.Combine(_cloud, "game")));
        var latest = await service.FindLatestSourceAsync(Game(Constants.MethodOneDrive), config, "");
        Assert.Equal(good, latest!.Path);
    }

    [Fact]
    public async Task WebDav_UploadsUnderIncompleteNameThenRenames()
    {
        var server = new FakeWebDavServer();
        var config = new AppConfig { WebDavUrl = server.Url };

        var result = await server.CreateBackupService().PerformBackupAsync(Game(Constants.MethodWebDav), config, "", _ => { });

        Assert.Equal(new[] { "WebDAV" }, result);
        Assert.Equal(12, server.Puts.Count());
        Assert.All(server.Puts, put => Assert.StartsWith("/dav/game/incomplete_save_", put.Path));
        var move = Assert.Single(server.Requests, r => r.Method == "MOVE");
        Assert.StartsWith("/dav/game/incomplete_save_", move.Path);
        Assert.Matches(@"^/dav/game/save_\d{8}_\d{6}_mtime\d+/$", move.Destination);
        Assert.Equal("T", move.Overwrite);
    }

    [Fact]
    public async Task WebDav_FailedUpload_IsNeverPublishedAndIsRemoved()
    {
        var server = new FakeWebDavServer
        {
            Respond = req => Task.FromResult<HttpResponseMessage?>(req.Method.Method == "PUT" && req.RequestUri!.AbsolutePath.EndsWith("slot03.sav")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : null)
        };

        var result = await server.CreateBackupService()
            .PerformBackupAsync(Game(Constants.MethodWebDav), new AppConfig { WebDavUrl = server.Url }, "", _ => { });

        Assert.Empty(result);
        Assert.DoesNotContain(server.Requests, r => r.Method == "MOVE");
        Assert.DoesNotContain(server.Requests, r => r.Path.StartsWith("/dav/game/save_"));
        var delete = Assert.Single(server.Requests, r => r.Method == "DELETE");
        Assert.Matches(@"^/dav/game/incomplete_save_.*/$", delete.Path);
    }

    [Fact]
    public async Task WebDav_ServerWithoutMove_FallsBackToUploadInPlace()
    {
        var server = new FakeWebDavServer
        {
            Respond = req => Task.FromResult<HttpResponseMessage?>(req.Method.Method == "MOVE"
                ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                : null)
        };
        var service = server.CreateBackupService();
        var config = new AppConfig { WebDavUrl = server.Url };

        Assert.Equal(new[] { "WebDAV" }, await service.PerformBackupAsync(Game(Constants.MethodWebDav), config, "", _ => { }));
        Assert.Equal(12, server.Puts.Count(p => p.Path.StartsWith("/dav/game/save_")));
        Assert.Contains(server.Requests, r => r.Method == "DELETE" && r.Path.StartsWith("/dav/game/incomplete_save_"));

        // Remembered for the server: the next backup skips the doomed MOVE attempt.
        server.Requests.Clear();
        await Task.Delay(1100);
        Assert.Equal(new[] { "WebDAV" }, await service.PerformBackupAsync(Game(Constants.MethodWebDav), config, "", _ => { }));
        Assert.DoesNotContain(server.Requests, r => r.Method == "MOVE" || r.Path.Contains("incomplete_"));
    }

    [Fact]
    public async Task WebDav_GameCanKeepSavingWhileUploadIsInFlight()
    {
        var uploadStarted = new TaskCompletionSource();
        var releaseUpload = new TaskCompletionSource();
        var server = new FakeWebDavServer
        {
            Respond = async req =>
            {
                if (req.Method.Method == "PUT")
                {
                    uploadStarted.TrySetResult();
                    await releaseUpload.Task;
                }
                return null;
            }
        };
        var backup = server.CreateBackupService()
            .PerformBackupAsync(Game(Constants.MethodWebDav), new AppConfig { WebDavUrl = server.Url }, "", _ => { });
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Uploads read a private copy, so the game's own files are free to be rewritten.
        foreach (var file in Directory.GetFiles(_save))
            await File.WriteAllTextAsync(file, "newer progress");

        releaseUpload.SetResult();
        Assert.Equal(new[] { "WebDAV" }, await backup);
    }
}
