using System.Diagnostics.CodeAnalysis;
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

    /// <summary>Copy of the last successfully saved config, used to recover from a corrupt config.json.</summary>
    public string BackupPath => _configPath + ".bak";

    /// <summary>Where a corrupt config.json was moved by <see cref="Load"/>, so the user can be told.</summary>
    public string? CorruptConfigPath { get; private set; }

    /// <summary>True when the corrupt config.json was replaced by <see cref="BackupPath"/>.</summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>
    /// Loads config.json. A corrupt file is moved aside and replaced by the last good copy; a file
    /// that cannot be read at all throws instead, because starting empty would overwrite it on the
    /// next save.
    /// </summary>
    public AppConfig Load()
    {
        lock (_lock)
        {
            var text = ReadIfExists(_configPath);
            if (text is null)
                return CreateDefault();
            if (TryParse(text, out var config))
                return config;

            var corruptPath = $"{_configPath}.corrupt-{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Move(_configPath, corruptPath, overwrite: true);
            CorruptConfigPath = corruptPath;
            if (TryLoadBackup(out var recovered))
            {
                File.Copy(BackupPath, _configPath, overwrite: true);
                RecoveredFromBackup = true;
                return recovered;
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
            try
            {
                File.Copy(_configPath, BackupPath, overwrite: true);
            }
            catch
            {
                // The config itself was saved; the next save refreshes the backup.
            }
        }
    }

    private static string? ReadIfExists(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 3)
            {
                // Briefly locked by another process (antivirus, sync client); try again.
                Thread.Sleep(100);
            }
        }
    }

    private bool TryLoadBackup([NotNullWhen(true)] out AppConfig? config)
    {
        config = null;
        try
        {
            return File.Exists(BackupPath) && TryParse(File.ReadAllText(BackupPath), out config);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParse(string text, [NotNullWhen(true)] out AppConfig? config)
    {
        try
        {
            // Very old versions stored a plain game list at the root.
            config = IsPlainList(text)
                ? CreateDefault(JsonSerializer.Deserialize<List<GameConfig>>(text, SerializerOptions) ?? new List<GameConfig>())
                : JsonSerializer.Deserialize<AppConfig>(text, SerializerOptions);
            if (config is null)
                return false;
            EnsureDefaults(config);
            return true;
        }
        catch
        {
            config = null;
            return false;
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
        config.SkippedUpdateVersion ??= "";
        config.DefaultDownloadSource = string.IsNullOrEmpty(config.DefaultDownloadSource)
            ? Constants.SourceAsk
            : config.DefaultDownloadSource;
        config.DefaultConfigSource = string.IsNullOrEmpty(config.DefaultConfigSource)
            ? Constants.SourceAsk
            : config.DefaultConfigSource;
        config.Games ??= new List<GameConfig>();
        config.Games.RemoveAll(game => game is null);
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
