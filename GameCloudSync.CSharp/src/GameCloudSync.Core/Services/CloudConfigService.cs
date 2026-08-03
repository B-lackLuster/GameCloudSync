using System.Text.Encodings.Web;
using System.Text.Json;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

/// <summary>
/// Backs up / restores / merges the game configuration through the cloud
/// (OneDrive local folder + WebDAV), keeping at most MAX_CONFIG_BACKUPS copies.
/// </summary>
public sealed class CloudConfigService
{
    private readonly Func<string, string, WebDavClient> _webDavFactory;

    public CloudConfigService(Func<string, string, WebDavClient>? webDavFactory = null)
    {
        _webDavFactory = webDavFactory ?? ((url, password) => new WebDavClient(url, "", password));
    }

    public async Task<List<string>> BackupConfigToCloudAsync(
        AppConfig config,
        string webDavPassword,
        Action<string> log,
        CancellationToken ct = default)
    {
        if (config.Games.Count == 0)
            return new List<string>();

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupName = $"config_{timestamp}.json";
        var payload = config.Games.Select(g => new
        {
            name = g.Name,
            save = g.Save,
            paused = g.Paused,
            backup_methods = g.BackupMethods,
            max_backups = g.MaxBackups
        }).ToList();
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        var okTargets = new List<string>();

        // OneDrive
        if (!string.IsNullOrEmpty(config.CloudRoot))
        {
            try
            {
                var cfDir = Path.Combine(config.CloudRoot, Constants.CloudConfigBackupDir);
                Directory.CreateDirectory(cfDir);
                await File.WriteAllTextAsync(Path.Combine(cfDir, backupName), json, ct);
                var existing = Directory.EnumerateFiles(cfDir, "config_*.json")
                    .OrderBy(f => File.GetLastWriteTimeUtc(f))
                    .ToList();
                foreach (var old in existing.Take(Math.Max(0, existing.Count - Constants.MaxConfigBackups)))
                {
                    try
                    {
                        File.Delete(old);
                    }
                    catch (Exception ex)
                    {
                        log(Localization.T("Log.CfgOneDriveCleanFail", old, ex.Message));
                    }
                }
                okTargets.Add("OneDrive");
            }
            catch (Exception ex)
            {
                log(Localization.T("Log.CfgOneDriveWriteFail", ex.Message));
            }
        }

        // WebDAV
        if (!string.IsNullOrEmpty(config.WebDavUrl))
        {
            try
            {
                using var wc = _webDavFactory(config.WebDavUrl, webDavPassword);
                var tmpDir = Path.Combine(Path.GetTempPath(), $"gcs_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tmpDir);
                try
                {
                    var tmpFile = Path.Combine(tmpDir, backupName);
                    await File.WriteAllTextAsync(tmpFile, json, ct);
                    if (!await wc.MkColAsync(Constants.CloudConfigBackupDir, ct))
                    {
                        log(Localization.T("Log.CfgWebDavMkcolFail", Constants.CloudConfigBackupDir));
                    }
                    else if (await wc.PutFileAsync(tmpFile, $"{Constants.CloudConfigBackupDir}/{backupName}", ct))
                    {
                        var items = await wc.PropFindAsync(Constants.CloudConfigBackupDir, ct: ct);
                        var cfFiles = items.Where(i => i.Name.StartsWith("config_", StringComparison.Ordinal))
                            .Select(i => i.Name)
                            .OrderByDescending(n => n)
                            .ToList();
                        foreach (var oldName in cfFiles.Skip(Constants.MaxConfigBackups))
                        {
                            try
                            {
                                await wc.DeletePathAsync($"{Constants.CloudConfigBackupDir}/{oldName}", ct);
                            }
                            catch (Exception ex)
                            {
                                log(Localization.T("Log.CfgWebDavCleanFail", oldName, ex.Message));
                            }
                        }
                        okTargets.Add("WebDAV");
                    }
                    else
                    {
                        log(Localization.T("Log.CfgWebDavUploadFail", backupName));
                    }
                }
                finally
                {
                    FileUtil.DeleteDirectorySafe(tmpDir);
                }
            }
            catch (Exception ex)
            {
                log(Localization.T("Log.CfgWebDavError", ex.Message));
            }
        }

        if (okTargets.Count > 0)
            log(Localization.T("Log.CfgBackedUp", string.Join(", ", okTargets)));
        return okTargets;
    }

    public async Task<List<CloudConfigSummary>> ListLatestSourcesAsync(
        AppConfig config,
        string webDavPassword,
        CancellationToken ct = default)
    {
        var oneDrive = await LoadOneDriveLatestAsync(config, ct);
        var webDav = await LoadWebDavLatestAsync(config, webDavPassword, ct);
        return new List<CloudConfigSummary> { oneDrive, webDav };
    }

    private async Task<CloudConfigSummary> LoadOneDriveLatestAsync(AppConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.CloudRoot))
            return new CloudConfigSummary("OneDrive", "", Array.Empty<GameConfig>(), Localization.T("Common.NotConfigured"));
        var cfDir = Path.Combine(config.CloudRoot, Constants.CloudConfigBackupDir);
        if (!Directory.Exists(cfDir))
            return new CloudConfigSummary("OneDrive", "", Array.Empty<GameConfig>(), Localization.T("Common.NoConfigFound"));
        try
        {
            var files = Directory.EnumerateFiles(cfDir, "config_*.json")
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .OrderByDescending(f => f)
                .ToList();
            if (files.Count == 0)
                return new CloudConfigSummary("OneDrive", "", Array.Empty<GameConfig>(), Localization.T("Common.NoConfigFound"));
            var fn = files[0]!;
            var games = ParseConfigGames(await File.ReadAllTextAsync(Path.Combine(cfDir, fn), ct));
            var status = games.Count > 0 ? Localization.T("Common.Restorable") : Localization.T("Common.ConfigEmpty");
            return new CloudConfigSummary("OneDrive", fn, games, status);
        }
        catch (Exception ex)
        {
            return new CloudConfigSummary("OneDrive", "", Array.Empty<GameConfig>(), Localization.T("Common.ReadFailed", ex.Message));
        }
    }

    private async Task<CloudConfigSummary> LoadWebDavLatestAsync(AppConfig config, string webDavPassword, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(config.WebDavUrl))
            return new CloudConfigSummary("WebDAV", "", Array.Empty<GameConfig>(), Localization.T("Common.NotConfigured"));
        try
        {
            using var wc = _webDavFactory(config.WebDavUrl, webDavPassword);
            var items = await wc.PropFindAsync(Constants.CloudConfigBackupDir, ct: ct);
            var files = items.Where(i => i.Name.StartsWith("config_", StringComparison.Ordinal))
                .Select(i => i.Name)
                .OrderByDescending(n => n)
                .ToList();
            if (files.Count == 0)
                return new CloudConfigSummary("WebDAV", "", Array.Empty<GameConfig>(), Localization.T("Common.NoConfigFound"));
            var fn = files[0];
            var tmpDir = Path.Combine(Path.GetTempPath(), $"gcs_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);
            try
            {
                var tmpFile = Path.Combine(tmpDir, fn);
                if (!await wc.GetFileAsync($"{Constants.CloudConfigBackupDir}/{fn}", tmpFile, ct))
                    return new CloudConfigSummary("WebDAV", fn, Array.Empty<GameConfig>(), Localization.T("Common.DownloadFailed"));
                var games = ParseConfigGames(await File.ReadAllTextAsync(tmpFile, ct));
                var status = games.Count > 0 ? Localization.T("Common.Restorable") : Localization.T("Common.ConfigEmpty");
                return new CloudConfigSummary("WebDAV", fn, games, status);
            }
            finally
            {
                FileUtil.DeleteDirectorySafe(tmpDir);
            }
        }
        catch (Exception ex)
        {
            return new CloudConfigSummary("WebDAV", "", Array.Empty<GameConfig>(), Localization.T("Common.ReadFailed", ex.Message));
        }
    }

    public static IReadOnlyList<GameConfig> ParseConfigGames(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            JsonElement gamesElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                gamesElement = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("games", out var g) &&
                     g.ValueKind == JsonValueKind.Array)
            {
                gamesElement = g;
            }
            else
            {
                return Array.Empty<GameConfig>();
            }

            var games = new List<GameConfig>();
            foreach (var item in gamesElement.EnumerateArray())
            {
                var game = new GameConfig
                {
                    Name = GetString(item, "name"),
                    Save = GetString(item, "save"),
                    Paused = item.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True,
                    MaxBackups = item.TryGetProperty("max_backups", out var mb) && mb.TryGetInt32(out var max)
                        ? max
                        : Constants.DefaultMaxBackups
                };
                if (item.TryGetProperty("backup_methods", out var bm) && bm.ValueKind == JsonValueKind.Array)
                {
                    var methods = new List<string>();
                    foreach (var m in bm.EnumerateArray())
                    {
                        if (m.ValueKind == JsonValueKind.String)
                            methods.Add(m.GetString() ?? "");
                    }
                    game.BackupMethods = methods;
                }
                if (!string.IsNullOrEmpty(game.Name))
                    games.Add(game);
            }
            return games;
        }
        catch
        {
            return Array.Empty<GameConfig>();
        }
    }

    private static string GetString(JsonElement item, string property)
    {
        return item.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";
    }
}
