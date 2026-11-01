using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GameCloudSync.Core.Services;

/// <summary>
/// Remembers, per game, the save time this PC and the cloud last had in common (after a backup or a
/// restore that worked). When both sides changed since then, neither may simply replace the other.
/// Kept apart from config.json because it describes this PC only: it must not travel with an exported
/// or cloud-backed-up config, and the monitor writes it from its own thread.
/// </summary>
public sealed class SyncBaselineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, long>? _entries;

    public SyncBaselineStore(string dataDir)
    {
        _path = Path.Combine(dataDir, Constants.SyncStateFileName);
    }

    public string FilePath => _path;

    /// <returns>The newest save time (Unix seconds) at the last sync, or 0 when this PC never synced the game.</returns>
    public long Get(string gameName)
    {
        lock (_lock)
            return Entries().TryGetValue(gameName, out var mtime) ? mtime : 0;
    }

    public void Set(string gameName, long mtime)
    {
        if (string.IsNullOrEmpty(gameName) || mtime <= 0)
            return;
        lock (_lock)
        {
            var entries = Entries();
            if (entries.TryGetValue(gameName, out var known) && known == mtime)
                return;
            entries[gameName] = mtime;
            Save(entries);
        }
    }

    /// <summary>Forgets a game, e.g. when it is deleted or now saves to another folder.</summary>
    public void Remove(string gameName)
    {
        lock (_lock)
        {
            if (Entries().Remove(gameName))
                Save(Entries());
        }
    }

    /// <summary>Keeps the baseline of a renamed game; its backups are stored under the new name from now on.</summary>
    public void Rename(string oldName, string newName)
    {
        lock (_lock)
        {
            var entries = Entries();
            if (!entries.Remove(oldName, out var mtime))
                return;
            entries[newName] = mtime;
            Save(entries);
        }
    }

    private Dictionary<string, long> Entries() => _entries ??= Load();

    private Dictionary<string, long> Load()
    {
        // Names are folder names, which ignore case on Windows.
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_path))
                return result;
            var stored = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(_path));
            foreach (var (name, mtime) in stored ?? new Dictionary<string, long>())
            {
                if (mtime > 0)
                    result[name] = mtime;
            }
        }
        catch
        {
            // A damaged file only means "never synced here": the next sync writes it again.
        }
        return result;
    }

    private void Save(Dictionary<string, long> entries)
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Failing to remember a sync must never fail the backup or restore it follows.
        }
    }
}
