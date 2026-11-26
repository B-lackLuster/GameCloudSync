using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public sealed class SaveDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gcs-find-saves-").FullName;

    [Fact]
    public void EmbeddedWindowsCatalogFindsChineseTitleAndSteamId()
    {
        var catalog = GameSaveCatalog.Default;
        var game = Assert.Single(catalog.Search("星露谷物語"));
        Assert.Equal("Stardew Valley", game.Title);
        Assert.Same(game, Assert.Single(catalog.FromSteam(413150u)));
        Assert.Contains(game.Rules, r => r.Path == "<winAppData>/StardewValley/Saves");
        Assert.DoesNotContain(game.Rules, r => r.Path.StartsWith("<xdgConfig>", StringComparison.Ordinal));
    }

    [Fact]
    public void WikidataTitleCanFindTheOriginalSaveRules()
    {
        var catalog = GameSaveCatalog.Default;
        var game = catalog.Search("小丑牌")[0];
        Assert.Equal("Balatro", game.Title);
        Assert.Equal("小丑牌", game.DisplayTitle(chinese: true));
        Assert.Same(game, Assert.Single(catalog.FromSteam(2379780u)));
        Assert.Contains(game.Rules, rule => rule.Path == "<winAppData>/Balatro");
    }

    [Fact]
    public void ExactChineseNameRanksAboveASequelAndNicknamesKeepTheDisplayTitle()
    {
        var catalog = GameSaveCatalog.Default;
        Assert.Equal("Hades", catalog.Search("哈迪斯")[0].Title);
        Assert.Equal("Hades II", catalog.Search("哈迪斯 ２")[0].Title);
        var nickname = catalog.Search("老头环")[0];
        Assert.Equal("Elden Ring", nickname.Title);
        Assert.Equal("艾尔登法环", nickname.DisplayTitle(chinese: true));
    }

    [Fact]
    public void ChineseEditionNamesDisambiguateTwoGamesWithTheSameAlias()
    {
        var catalog = GameSaveCatalog.Default;
        var titles = catalog.Search("侠盗猎车手5").Select(game => game.Title).ToArray();
        Assert.Contains("Grand Theft Auto V", titles);
        Assert.Contains("Grand Theft Auto V Enhanced", titles);
        Assert.Equal("Grand Theft Auto V", catalog.Search("GTA5 传承版")[0].Title);
        Assert.Equal("Grand Theft Auto V Enhanced", catalog.Search("GTA5 增强版")[0].Title);
    }

    [Fact]
    public void SearchesTheSelectedCopyAndKeepsCommonPathsIdentifiable()
    {
        var steamInstall = Path.Combine(_root, "steam", "copy");
        var otherInstall = Path.Combine(_root, "other", "copy");
        var expectedSteam = WriteSave(steamInstall, "saves");
        var expectedOther = WriteSave(otherInstall, "saves");
        var shared = WriteSave(_root, "Example", "Saves");
        var steamOnly = WriteSave(_root, "SteamOnly", "Saves");
        var steam = new SteamInstallation(123, "Example", steamInstall, Path.Combine(_root, "steam"));
        var game = Game(new("<base>/saves", [""]), new("<winAppData>/Example/Saves", [""]),
            new("<winAppData>/SteamOnly/Saves", ["steam"]));

        var finder = Finder();
        var other = finder.Find(game, otherInstall, null, [], "");
        Assert.Equal(2, other.Matches.Count);
        Assert.Contains(other.Matches, match => match.Path == expectedOther && !match.MayBeShared);
        Assert.Contains(other.Matches, match => match.Path == shared && match.MayBeShared);
        var installed = finder.Find(game, null, steam, [], "");
        Assert.Contains(installed.Matches, match => match.Path == expectedSteam && !match.MayBeShared);
        Assert.Contains(installed.Matches, match => match.Path == steamOnly);
        Assert.DoesNotContain(installed.Matches, match => match.Path == expectedOther);
        Assert.True(finder.Find(game, null, null, [], "").NeedsInstallFolder);
    }

    [Fact]
    public void SharedOrNestedDirectoriesCannotBecomeAnotherBackupEntry()
    {
        var folder = WriteSave(_root, "Game", "Saves");
        var game = Game(new GameSaveRule("<winAppData>/Game/Saves", [""]));
        var existing = new GameConfig { Name = "Other copy", Save = folder.ToUpperInvariant() + @"\" };
        var result = Finder().Find(game, null, null, [existing], "");
        var match = Assert.Single(result.Matches);
        Assert.Equal(SaveMatchKind.AlreadyAdded, match.Kind);
        Assert.Equal(existing.Name, match.ExistingGame);
        Assert.Same(existing, SavePathPolicy.FindOverlappingGame(Path.Combine(folder, "nested"), [existing]));
        Assert.Same(existing, SavePathPolicy.FindOverlappingGame(@"\\?\" + folder, [existing]));
        Assert.Null(SavePathPolicy.FindOverlappingGame(folder + "2", [existing]));
    }

    [Fact]
    public void SaveFilesStayReferencesAndEmptyFoldersAreNotConfirmed()
    {
        var folder = WriteSave(_root, "Game");
        var empty = Directory.CreateDirectory(Path.Combine(_root, "Empty")).FullName;
        File.WriteAllText(Path.Combine(empty, "desktop.ini"), "irrelevant");
        var game = Game(new("<winAppData>/Game/*.sav", [""]), new("<winAppData>/Empty", [""]),
            new("<winAppData>/Game/../Game", [""]));

        var result = Finder().Find(game, null, null, [], "");
        Assert.True(result.HasUnsupportedRules);
        Assert.Contains(result.Matches, match => match.Path == Path.Combine(folder, "game.sav") && match.Kind == SaveMatchKind.File);
        Assert.Contains(result.Matches, match => match.Path == empty && match.Kind == SaveMatchKind.EmptyFolder);
        Assert.DoesNotContain(result.Matches, match => match.Kind == SaveMatchKind.Folder);
    }

    [Fact]
    public void SteamUserDataOnlyMatchesSelectedAppAndListsEachAccount()
    {
        var client = Path.Combine(_root, "client");
        var copy = Path.Combine(_root, "library", "copy");
        var account1 = WriteSave(client, "userdata", "111", "123", "remote");
        var account2 = WriteSave(client, "userdata", "222", "123", "remote");
        WriteSave(client, "userdata", "111", "456", "remote");
        var steam = new SteamInstallation(123, "Example", copy, client);
        var game = Game(new("<root>/userdata/<storeUserId>/123/remote", ["steam"]),
            new("<root>/userdata/<storeUserId>/456/remote", ["steam"]));

        var matches = Finder().Find(game, null, steam, [], "").Matches;
        Assert.Equal([account1, account2], matches.Select(match => match.Path));
    }

    [Fact]
    public void ShadowsListsUbisoftAccountsAndTheVerifiedAlternateCopySeparately()
    {
        var game = GameSaveCatalog.Default.Search("刺客信条影")[0];
        var launcher = Path.Combine(_root, "custom Ubisoft root");
        const string account = "12345678-1234-4234-8234-123456789abc";
        var connectSave = WriteSave(launcher, "savegames", account, "8006");
        var steamSave = WriteSave(launcher, "savegames", account, "1081");
        var secondAccount = WriteSave(launcher, "savegames", Guid.NewGuid().ToString(), "8006");
        WriteSave(launcher, "savegames", account, "6100");
        var alternate = Directory.CreateDirectory(Path.Combine(_root, "Goldberg UplayEmu Saves", "8006")).FullName;
        File.WriteAllText(Path.Combine(alternate, "ACShadows[AutoSave01].save"), "progress");
        var finder = new SaveLocationFinder(new SaveSearchRoots(_root, _root, _root, _root, _root, _root)
            { UbisoftLaunchers = [launcher] });

        var result = finder.Find(game, null, null, [], "");
        Assert.False(result.HasUnsupportedRules);
        Assert.Equal(4, result.Matches.Count);
        Assert.All(result.Matches, match => Assert.Equal(SaveMatchKind.Folder, match.Kind));
        Assert.All(new[] { connectSave, secondAccount, steamSave }, path =>
            Assert.Contains(result.Matches, match => match.Path == path && match.Source == SaveMatchSource.UbisoftConnect));
        Assert.Contains(result.Matches, match => match.Path == alternate && match.Source == SaveMatchSource.Goldberg);

        var steam = new SteamInstallation(3159330, game.Title, Path.Combine(_root, "game"), Path.Combine(_root, "steam"));
        Assert.Equal(steamSave, Assert.Single(finder.Find(game, null, steam, [], "").Matches).Path);
        var existing = new GameConfig { Name = "Saved copy", Save = alternate };
        Assert.Contains(finder.Find(game, null, null, [existing], "").Matches,
            match => match.Path == alternate && match.Kind == SaveMatchKind.AlreadyAdded && match.ExistingGame == existing.Name);
        Assert.DoesNotContain(finder.Find(GameSaveCatalog.Default.Search("Assassin's Creed Mirage")[0], null, null, [], "").Matches,
            match => match.Path == alternate);
    }

    [Fact]
    public void ShadowsAlternateNeedsAProgressFileInTheExactGameFolder()
    {
        var game = Assert.Single(GameSaveCatalog.Default.FromSteam(3159330));
        var alternate = WriteSave(_root, "Goldberg UplayEmu Saves", "8006");
        File.WriteAllText(Path.Combine(alternate, "ACShadows[Options].save"), "settings");
        var wrongFolder = WriteSave(_root, "Goldberg UplayEmu Saves", "8007");
        File.WriteAllText(Path.Combine(wrongFolder, "ACShadows[AutoSave01].save"), "progress");
        Assert.Empty(Finder().Find(game, null, null, [], "").Matches);

        File.WriteAllText(Path.Combine(alternate, "ACShadows[ManualSave01].save"), "progress");
        Assert.Equal(alternate, Assert.Single(Finder().Find(game, null, null, [], "").Matches).Path);
    }

    [Fact]
    public void ReadsInstalledCopiesFromAllSteamLibraries()
    {
        var client = Path.Combine(_root, "client");
        var secondary = Path.Combine(_root, "other library");
        var steamapps = Directory.CreateDirectory(Path.Combine(client, "steamapps")).FullName;
        var otherApps = Directory.CreateDirectory(Path.Combine(secondary, "steamapps")).FullName;
        File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{VdfPath(client)}}" }
                "1" "{{VdfPath(secondary)}}"
            }
            """);
        var firstCopy = Directory.CreateDirectory(Path.Combine(steamapps, "common", "First")).FullName;
        var secondCopy = Directory.CreateDirectory(Path.Combine(otherApps, "common", "Second")).FullName;
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_123.acf"), """
            "AppState" { "appid" "123" "name" "Example \"Game\"" "installdir" "First" }
            """);
        File.WriteAllText(Path.Combine(otherApps, "appmanifest_123.acf"), """
            "AppState" { "appid" "123" "name" "Example \"Game\"" "installdir" "Second" }
            """);
        File.WriteAllText(Path.Combine(otherApps, "appmanifest_999.acf"), """
            "AppState" { "appid" "998" "name" "Wrong app ID" "installdir" "Second" }
            """);
        File.WriteAllText(Path.Combine(otherApps, "appmanifest_777.acf"), """ "AppState" { "appid" """);

        var installs = SteamInstallations.Read(client);
        Assert.Equal(new[] { firstCopy, secondCopy }, installs.Select(install => install.Directory));
        Assert.All(installs, install =>
        {
            Assert.Equal(123u, install.AppId);
            Assert.Equal("Example \"Game\"", install.Title);
            Assert.Equal(client, install.ClientRoot);
        });
    }

    private SaveLocationFinder Finder() => new(new SaveSearchRoots(_root, _root, _root, _root, _root, _root));

    private static CatalogGame Game(params GameSaveRule[] rules) => new("Example", [], [], [123], rules, false);

    private static string VdfPath(string path) => path.Replace(@"\", @"\\");

    private static string WriteSave(string root, params string[] segments)
    {
        var dir = Directory.CreateDirectory(Path.Combine([root, .. segments])).FullName;
        File.WriteAllText(Path.Combine(dir, "game.sav"), "game save");
        return dir;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
