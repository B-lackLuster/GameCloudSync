using System.Net;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public sealed class BackupFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_fail_{Guid.NewGuid():N}");
    private readonly string _save;
    private readonly string _cloud;

    public BackupFailureTests()
    {
        _save = Path.Combine(_root, "save");
        _cloud = Path.Combine(_root, "cloud");
        Directory.CreateDirectory(_save);
        Directory.CreateDirectory(_cloud);
        File.WriteAllText(Path.Combine(_save, "slot1.sav"), "progress");
        File.WriteAllText(Path.Combine(_save, "slot2.sav"), "more progress");
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

    private static void NoLog(string _) { }

    private static FakeWebDavServer ServerAnswering(string method, HttpStatusCode status, string pathEndsWith = "") => new()
    {
        Respond = req => Task.FromResult<HttpResponseMessage?>(
            req.Method.Method == method && req.RequestUri!.AbsolutePath.TrimEnd('/').EndsWith(pathEndsWith)
                ? new HttpResponseMessage(status)
                : null)
    };

    // ---------------- Backup runs ----------------

    [Fact]
    public void TargetsFor_ListsTheConfiguredTargetsInTheOrderTheyRun()
    {
        var config = new AppConfig { CloudRoot = _cloud, WebDavUrl = "https://example.test/dav" };

        Assert.Equal(new[] { "OneDrive", "WebDAV" }, BackupService.TargetsFor(Game("webdav", "onedrive"), config));
        Assert.Equal(new[] { "WebDAV" }, BackupService.TargetsFor(Game("webdav"), config));
        Assert.Empty(BackupService.TargetsFor(Game("webdav"), new AppConfig { CloudRoot = _cloud }));
    }

    [Fact]
    public async Task OneDriveRootThatIsGone_FailsInsteadOfCreatingAFolderNobodySyncs()
    {
        var config = new AppConfig { CloudRoot = Path.Combine(_root, "OneDrive-gone") };

        var result = await new BackupService().RunBackupAsync(Game("onedrive"), config, "", NoLog);

        Assert.Empty(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("OneDrive", failure.Target);
        Assert.Contains(config.CloudRoot, failure.Message);
        Assert.False(Directory.Exists(config.CloudRoot));
    }

    [Fact]
    public async Task SaveFolderThatIsGone_FailsEveryTargetWithoutTouchingThem()
    {
        var server = new FakeWebDavServer();
        var game = Game("onedrive", "webdav");
        game.Save = Path.Combine(_root, "no-such-folder");
        var config = new AppConfig { CloudRoot = _cloud, WebDavUrl = server.Url };

        var result = await server.CreateBackupService().RunBackupAsync(game, config, "", NoLog);

        Assert.Empty(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("OneDrive, WebDAV", failure.Target);
        Assert.Contains(game.Save, failure.Message);
        Assert.Empty(server.Requests);
        Assert.Empty(Directory.GetDirectories(_cloud));
    }

    [Fact]
    public async Task SaveFolderFarTooBroad_IsRefusedBeforeAnythingIsCopied()
    {
        var game = Game("onedrive");
        game.Save = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = new AppConfig { CloudRoot = _cloud };

        var result = await new BackupService().RunBackupAsync(game, config, "", NoLog);

        Assert.Empty(result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Empty(Directory.GetDirectories(_cloud));
    }

    [Fact]
    public async Task MalformedWebDavAddress_FailsOnlyWebDav_AndOneDriveStillSucceeds()
    {
        var config = new AppConfig { CloudRoot = _cloud, WebDavUrl = "ftp://example.test/dav" };

        var result = await new BackupService().RunBackupAsync(Game("onedrive", "webdav"), config, "", NoLog);

        Assert.Equal(new[] { "OneDrive" }, result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("WebDAV", failure.Target);
        Assert.NotEmpty(failure.Message);
    }

    [Fact]
    public async Task WebDavLoginRefused_IsReportedWithTheStatus()
    {
        var server = ServerAnswering("MKCOL", HttpStatusCode.Unauthorized);
        var config = new AppConfig { WebDavUrl = server.Url };

        var result = await server.CreateBackupService().RunBackupAsync(Game("webdav"), config, "", NoLog);

        Assert.Empty(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("WebDAV", failure.Target);
        Assert.Contains("401", failure.Message);
        Assert.Contains("401", result.FailureSummary);
    }

    [Fact]
    public async Task OnlyTargets_RestrictsTheRun()
    {
        var server = new FakeWebDavServer();
        var config = new AppConfig { CloudRoot = _cloud, WebDavUrl = server.Url };
        var game = Game("onedrive", "webdav");
        var service = server.CreateBackupService();

        var webDavOnly = await service.RunBackupAsync(game, config, "", NoLog, new[] { "WebDAV" });
        Assert.Equal(new[] { "WebDAV" }, webDavOnly.Succeeded);
        Assert.Empty(Directory.GetDirectories(_cloud));

        server.Requests.Clear();
        var oneDriveOnly = await service.RunBackupAsync(game, config, "", NoLog, new[] { "OneDrive" });
        Assert.Equal(new[] { "OneDrive" }, oneDriveOnly.Succeeded);
        Assert.Empty(server.Requests);
        Assert.Single(Directory.GetDirectories(Path.Combine(_cloud, "game")));
    }

    [Fact]
    public async Task GameWithoutAConfiguredTarget_IsNotAFailure()
    {
        var result = await new BackupService().RunBackupAsync(Game("webdav"), new AppConfig { CloudRoot = _cloud }, "", NoLog);

        Assert.Empty(result.Succeeded);
        Assert.False(result.HasFailures);
    }

    [Fact]
    public async Task PerformBackup_StillReturnsTheTargetsThatSucceeded()
    {
        var config = new AppConfig { CloudRoot = _cloud, WebDavUrl = "ftp://example.test/dav" };

        var succeeded = await new BackupService().PerformBackupAsync(Game("onedrive", "webdav"), config, "", NoLog);

        Assert.Equal(new[] { "OneDrive" }, succeeded);
    }

    // ---------------- Status ----------------

    [Fact]
    public async Task Status_SaveFolderFarTooBroad_IsAnError()
    {
        var game = Game("onedrive");
        game.Save = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var status = await new BackupService().GetSyncStatusAsync(game, new AppConfig { CloudRoot = _cloud }, "", false);

        Assert.Equal(SyncStatusKind.Error, status.Kind);
        Assert.NotEmpty(status.Detail!);
    }

    [Fact]
    public async Task Status_OneDriveRootThatIsGone_IsAnErrorWithThePath()
    {
        var config = new AppConfig { CloudRoot = Path.Combine(_root, "OneDrive-gone") };

        var status = await new BackupService().GetSyncStatusAsync(Game("onedrive"), config, "", false);

        Assert.Equal(SyncStatusKind.Error, status.Kind);
        Assert.Contains(config.CloudRoot, status.Detail);
    }

    [Fact]
    public async Task Status_MalformedWebDavAddress_IsAnErrorEvenWithoutCheckingTheServer()
    {
        var config = new AppConfig { WebDavUrl = "ftp://example.test/dav" };

        var status = await new BackupService().GetSyncStatusAsync(Game("webdav"), config, "", checkWebDav: false);

        Assert.Equal(SyncStatusKind.Error, status.Kind);
    }

    [Fact]
    public async Task Status_WebDavLoginRefused_IsAnErrorNotWaitingForTheFirstBackup()
    {
        var server = ServerAnswering("PROPFIND", HttpStatusCode.Unauthorized);

        var status = await server.CreateBackupService().GetSyncStatusAsync(
            Game("webdav"), new AppConfig { WebDavUrl = server.Url }, "", checkWebDav: true);

        Assert.Equal(SyncStatusKind.Error, status.Kind);
        Assert.Contains("401", status.Detail);
    }

    [Fact]
    public async Task Status_GameFolderNotOnTheServerYet_IsNotAnError()
    {
        var server = ServerAnswering("PROPFIND", HttpStatusCode.NotFound, pathEndsWith: "/dav/game");

        var status = await server.CreateBackupService().GetSyncStatusAsync(
            Game("webdav"), new AppConfig { WebDavUrl = server.Url }, "", checkWebDav: true);

        Assert.Equal(SyncStatusKind.LocalNew, status.Kind);
    }

    [Fact]
    public async Task Status_ServerFailingForTheGameFolder_IsAnErrorWithTheStatus()
    {
        var server = ServerAnswering("PROPFIND", HttpStatusCode.InternalServerError, pathEndsWith: "/dav/game");

        var status = await server.CreateBackupService().GetSyncStatusAsync(
            Game("webdav"), new AppConfig { WebDavUrl = server.Url }, "", checkWebDav: true);

        Assert.Equal(SyncStatusKind.Error, status.Kind);
        Assert.Contains("500", status.Detail);
    }

    [Fact]
    public async Task Status_WithAProbeAlreadyInHand_DoesNotAskTheServerAgain()
    {
        var server = new FakeWebDavServer();

        var status = await server.CreateBackupService().GetSyncStatusAsync(
            Game("webdav"), new AppConfig { WebDavUrl = server.Url }, "", checkWebDav: true,
            webDavProbe: new WebDavProbe(false, 401, "login refused"));

        Assert.Equal(SyncStatusKind.Error, status.Kind);
        Assert.Equal("login refused", status.Detail);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Status_ReportsWhenTheNewestBackupWasMade()
    {
        foreach (var name in new[] { "save_20260511_090000", "save_20260512_143000" })
        {
            var dir = Path.Combine(_cloud, "game", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "slot1.sav"), "progress");
        }
        // Directory times decide which backup is newest, as they do when the app lists them.
        Directory.SetLastWriteTimeUtc(Path.Combine(_cloud, "game", "save_20260511_090000"), new DateTime(2026, 5, 11, 9, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(Path.Combine(_cloud, "game", "save_20260512_143000"), new DateTime(2026, 5, 12, 14, 30, 0, DateTimeKind.Utc));

        var status = await new BackupService().GetSyncStatusAsync(Game("onedrive"), new AppConfig { CloudRoot = _cloud }, "", false);

        Assert.Equal(new DateTime(2026, 5, 12, 14, 30, 0), status.LastBackup);
    }

    [Fact]
    public async Task ListWebDavBackups_ReturnsNothingOnErrors_WhileTheStrictVariantThrows()
    {
        var server = ServerAnswering("PROPFIND", HttpStatusCode.InternalServerError);
        var service = server.CreateBackupService();
        var config = new AppConfig { WebDavUrl = server.Url };

        Assert.Empty(await service.ListWebDavBackupsAsync(Game("webdav"), config, ""));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.ListWebDavBackupsOrThrowAsync(Game("webdav"), config, ""));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task ListWebDavBackups_StrictVariantTreatsAMissingGameFolderAsNoBackups()
    {
        var server = ServerAnswering("PROPFIND", HttpStatusCode.NotFound);

        var backups = await server.CreateBackupService()
            .ListWebDavBackupsOrThrowAsync(Game("webdav"), new AppConfig { WebDavUrl = server.Url }, "");

        Assert.Empty(backups);
    }

    // ---------------- Restore safety net ----------------

    [Fact]
    public async Task Restore_ToAFolderFarTooBroad_IsRefusedAndTouchesNothing()
    {
        var game = Game("onedrive");
        game.Save = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var backup = Path.Combine(_cloud, "game", "save_20260512_143000");
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(Path.Combine(backup, "slot1.sav"), "from the cloud");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new BackupService(snapshotRoot: Path.Combine(_root, "snapshots"))
            .RestoreFromOneDriveAsync(game, "save_20260512_143000", new AppConfig { CloudRoot = _cloud }, NoLog));

        Assert.False(Directory.Exists(Path.Combine(_root, "snapshots")));
    }
}
