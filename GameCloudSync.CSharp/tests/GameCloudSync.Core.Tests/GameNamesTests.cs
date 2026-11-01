using System.Net;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public sealed class GameNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_names_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private static readonly List<GameConfig> Library = new()
    {
        new GameConfig { Name = "Elden Ring" },
        new GameConfig { Name = "艾尔登法环" }
    };

    [Theory]
    [InlineData("Hollow Knight")]
    [InlineData("仁王3")]
    [InlineData("Game v1.2")]
    public void Validate_ReasonableNames_AreAccepted(string name)
    {
        Assert.Equal(GameNameError.None, GameNames.Validate(name, Library));
    }

    [Theory]
    [InlineData("elden ring")]
    [InlineData("ELDEN RING")]
    [InlineData("艾尔登法环")]
    public void Validate_SameNameIgnoringCase_IsDuplicate(string name)
    {
        // Windows folders ignore case, so these would share one backup folder.
        Assert.Equal(GameNameError.Duplicate, GameNames.Validate(name, Library));
    }

    [Fact]
    public void Validate_EditedGameMayKeepOrRecaseItsOwnName()
    {
        Assert.Equal(GameNameError.None, GameNames.Validate("ELDEN RING", Library, self: Library[0]));
        Assert.Equal(GameNameError.Duplicate, GameNames.Validate("艾尔登法环", Library, self: Library[0]));
    }

    [Theory]
    [InlineData("A:B")]
    [InlineData("save/slot")]
    [InlineData(@"save\slot")]
    [InlineData("what?")]
    [InlineData("star*")]
    [InlineData("<tag>")]
    [InlineData("a|b")]
    [InlineData("\"quoted\"")]
    [InlineData("Game.")]
    [InlineData("..")]
    [InlineData(" padded")]
    public void Validate_NamesThatAreNotSafeFolderNames_AreRejected(string name)
    {
        Assert.Equal(GameNameError.InvalidChars, GameNames.Validate(name, Library));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.save")]
    [InlineData("LPT1")]
    [InlineData(Constants.CloudConfigBackupDir)]
    public void Validate_ReservedNames_AreRejected(string name)
    {
        Assert.Equal(GameNameError.Reserved, GameNames.Validate(name, Library));
    }

    [Fact]
    public void Validate_EmptyAndOverlongNames_AreRejected()
    {
        Assert.Equal(GameNameError.Empty, GameNames.Validate("   ", Library));
        Assert.Equal(GameNameError.TooLong, GameNames.Validate(new string('x', GameNames.MaxLength + 1), Library));
    }

    [Fact]
    public async Task RenameStorage_MovesOneDriveFolderToTheNewName()
    {
        var oldBackup = Path.Combine(_root, "Old Name", "save_20260101_120000");
        Directory.CreateDirectory(oldBackup);
        await File.WriteAllTextAsync(Path.Combine(oldBackup, "slot.sav"), "progress");

        var failures = await new Services.BackupService()
            .RenameGameStorageAsync("Old Name", "New Name", new AppConfig { CloudRoot = _root }, "");

        Assert.Empty(failures);
        Assert.False(Directory.Exists(Path.Combine(_root, "Old Name")));
        Assert.Equal("progress", await File.ReadAllTextAsync(Path.Combine(_root, "New Name", "save_20260101_120000", "slot.sav")));
    }

    [Fact]
    public async Task RenameStorage_ExistingTargetFolder_IsReportedAndNothingMoves()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Old Name", "save_1"));
        Directory.CreateDirectory(Path.Combine(_root, "New Name", "save_2"));

        var failures = await new Services.BackupService()
            .RenameGameStorageAsync("Old Name", "New Name", new AppConfig { CloudRoot = _root }, "");

        Assert.Single(failures);
        Assert.True(Directory.Exists(Path.Combine(_root, "Old Name", "save_1")));
        Assert.False(Directory.Exists(Path.Combine(_root, "New Name", "save_1")));
    }

    [Fact]
    public async Task RenameStorage_MovesWebDavCollectionWithoutOverwriting()
    {
        var server = new FakeWebDavServer();

        var failures = await server.CreateBackupService()
            .RenameGameStorageAsync("Old Name", "New Name", new AppConfig { WebDavUrl = server.Url }, "");

        Assert.Empty(failures);
        var move = Assert.Single(server.Requests);
        Assert.Equal(("MOVE", "/dav/Old Name/", "/dav/New Name/", "F"),
            (move.Method, move.Path, move.Destination, move.Overwrite));
    }

    [Fact]
    public async Task RenameStorage_WebDavTargetExists_IsReported()
    {
        var server = new FakeWebDavServer
        {
            Respond = _ => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.PreconditionFailed))
        };

        var failures = await server.CreateBackupService()
            .RenameGameStorageAsync("Old Name", "New Name", new AppConfig { WebDavUrl = server.Url }, "");

        Assert.Single(failures);
    }
}
