namespace GameCloudSync.Core.Models;

public sealed class GameConfig
{
    public string Name { get; set; } = "";
    public string Save { get; set; } = "";
    public bool Paused { get; set; }
    public List<string> BackupMethods { get; set; } = new() { Constants.MethodOneDrive };
    public int MaxBackups { get; set; } = Constants.DefaultMaxBackups;

    public bool HasMethod(string method) => BackupMethods.Contains(method);

    public string MethodsDisplay =>
        string.Join(" | ", BackupMethods.Select(m => m switch
        {
            Constants.MethodOneDrive => "OneDrive",
            Constants.MethodWebDav => "WebDAV",
            _ => m
        }));
}
