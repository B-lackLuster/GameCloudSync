using System.Text;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir;

    public ConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gcs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var store = new ConfigStore(_dir);
        var config = new AppConfig
        {
            CloudRoot = @"C:\Users\me\OneDrive\GameCloud",
            WebDavUrl = "https://example.com/dav",
            WebDavUsername = "testuser",
            DebugWebDavLog = true,
            DefaultDownloadSource = Constants.MethodWebDav,
            Games = new List<GameConfig>
            {
                new()
                {
                    Name = "艾尔登法环",
                    Save = @"D:\Saves\EldenRing",
                    BackupMethods = new List<string> { Constants.MethodOneDrive, Constants.MethodWebDav },
                    MaxBackups = 5
                }
            }
        };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(config.CloudRoot, loaded.CloudRoot);
        Assert.Equal(config.WebDavUrl, loaded.WebDavUrl);
        Assert.Equal(config.WebDavUsername, loaded.WebDavUsername);
        Assert.True(loaded.DebugWebDavLog);
        Assert.Equal(Constants.MethodWebDav, loaded.DefaultDownloadSource);
        Assert.Single(loaded.Games);
        Assert.Equal("艾尔登法环", loaded.Games[0].Name);
        Assert.Equal(5, loaded.Games[0].MaxBackups);
        Assert.Equal(2, loaded.Games[0].BackupMethods.Count);
    }

    [Fact]
    public void Load_LegacyPlainList_WrapsIntoConfig()
    {
        var json = """
            [
              { "name": "Game A", "save": "C:\\Saves\\A" }
            ]
            """;
        File.WriteAllText(Path.Combine(_dir, Constants.ConfigFileName), json, new UTF8Encoding(false));

        var config = new ConfigStore(_dir).Load();

        Assert.Single(config.Games);
        Assert.Equal("Game A", config.Games[0].Name);
        Assert.Contains(Constants.MethodOneDrive, config.Games[0].BackupMethods);
    }

    [Fact]
    public void Load_CorruptedFile_FallsBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_dir, Constants.ConfigFileName), "{ not json", new UTF8Encoding(false));
        var config = new ConfigStore(_dir).Load();
        Assert.Empty(config.Games);
        Assert.Equal("", config.CloudRoot);
    }
}
