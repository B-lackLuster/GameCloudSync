using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GameCloudSync.Core.Models;

namespace GameCloudSync.Core.Services;

public sealed class ConfigStore
{
    private readonly string _configPath;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ConfigStore(string appDir)
    {
        AppDir = appDir;
        _configPath = Path.Combine(appDir, Constants.ConfigFileName);
    }

    public string AppDir { get; }
    public string ConfigPath => _configPath;

    public AppConfig Load()
    {
        lock (_lock)
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    var text = File.ReadAllText(_configPath);
                    // Very old versions stored a plain game list at the root.
                    if (IsPlainList(text))
                    {
                        var games = JsonSerializer.Deserialize<List<GameConfig>>(text, SerializerOptions) ?? new List<GameConfig>();
                        return CreateDefault(games);
                    }

                    var config = JsonSerializer.Deserialize<AppConfig>(text, SerializerOptions) ?? CreateDefault();
                    EnsureDefaults(config);
                    return config;
                }
                catch
                {
                    // Fall back to defaults rather than crashing on a corrupted file.
                    return CreateDefault();
                }
            }
            return CreateDefault();
        }
    }

    /// <summary>Atomically writes the config: temp file + replace, like the Python original.</summary>
    public void Save(AppConfig config)
    {
        lock (_lock)
        {
            var tmp = _configPath + ".tmp";
            var json = JsonSerializer.Serialize(config, SerializerOptions);
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            File.Move(tmp, _configPath, overwrite: true);
        }
    }

    private static bool IsPlainList(string jsonText)
    {
        try
        {
            return JsonNode.Parse(jsonText) is JsonArray;
        }
        catch
        {
            return false;
        }
    }

    private static AppConfig CreateDefault() => CreateDefault(new List<GameConfig>());

    private static AppConfig CreateDefault(List<GameConfig> games) => new()
    {
        Games = games
    };

    private static void EnsureDefaults(AppConfig config)
    {
        config.Language = string.IsNullOrEmpty(config.Language) ? Localization.Chinese : config.Language;
        config.WebDavUrl ??= "";
        config.WebDavUsername ??= "";
        config.DefaultDownloadSource = string.IsNullOrEmpty(config.DefaultDownloadSource)
            ? Constants.SourceAsk
            : config.DefaultDownloadSource;
        config.DefaultConfigSource = string.IsNullOrEmpty(config.DefaultConfigSource)
            ? Constants.SourceAsk
            : config.DefaultConfigSource;
        config.Games ??= new List<GameConfig>();
        foreach (var game in config.Games)
        {
            game.Name ??= "";
            game.Save ??= "";
            game.BackupMethods ??= new List<string> { Constants.MethodOneDrive };
            if (game.BackupMethods.Count == 0)
                game.BackupMethods.Add(Constants.MethodOneDrive);
            if (game.MaxBackups <= 0)
                game.MaxBackups = config.DefaultMaxBackups > 0 ? config.DefaultMaxBackups : Constants.DefaultMaxBackups;
        }
    }
}
