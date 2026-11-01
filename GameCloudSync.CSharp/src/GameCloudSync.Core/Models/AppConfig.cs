using System.Text.Json.Serialization;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Models;

public sealed class AppConfig
{
    public string CloudRoot { get; set; } = "";
    public bool Autostart { get; set; }
    [JsonPropertyName("language")]
    public string Language { get; set; } = Localization.Chinese;
    [JsonPropertyName("webdav_url")]
    public string WebDavUrl { get; set; } = "";
    [JsonPropertyName("webdav_username")]
    public string WebDavUsername { get; set; } = "";
    public int DefaultMaxBackups { get; set; } = Constants.DefaultMaxBackups;
    public string DefaultDownloadSource { get; set; } = Constants.SourceAsk;
    public bool BackgroundMode { get; set; } = true;
    public bool AutoSyncOnStartup { get; set; }
    public bool AutoBackupConfigToCloud { get; set; }
    public string DefaultConfigSource { get; set; } = Constants.SourceAsk;
    [JsonPropertyName("debug_webdav_log")]
    public bool DebugWebDavLog { get; set; }
    /// <summary>The "still running in the tray" notice was shown the first time the window was closed.</summary>
    public bool TrayHintShown { get; set; }
    public List<GameConfig> Games { get; set; } = new();
}
