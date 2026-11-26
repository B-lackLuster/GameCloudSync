using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GameCloudSync.Core.Services;

public sealed record GameSaveRule(string Path, string[] Stores);

public sealed record CatalogGame(string Title, string[] ChineseNames, string[] Aliases,
    uint[] SteamIds, GameSaveRule[] Rules, bool HasRegistrySaves, string? ChineseTitle = null)
{
    public string DisplayTitle(bool chinese) => chinese ? ChineseTitle ?? Title : Title;
}

public sealed class GameSaveCatalog
{
    private record Snapshot(int Version, string Revision, CatalogGame[] Games);
    private record SearchEntry(CatalogGame Game, string[] Titles, string[] Aliases);

    private static readonly Lazy<GameSaveCatalog> Embedded = new(Load);
    private readonly SearchEntry[] _index;
    private readonly ILookup<uint, CatalogGame> _steam;

    private GameSaveCatalog(Snapshot snapshot)
    {
        _index = snapshot.Games.Select(game => new SearchEntry(game,
            SearchKeys(game.ChineseNames.Prepend(game.Title)), SearchKeys(game.Aliases))).ToArray();
        _steam = snapshot.Games.SelectMany(game => game.SteamIds.Select(id => (id, game)))
            .ToLookup(pair => pair.id, pair => pair.game);
    }

    public static GameSaveCatalog Default => Embedded.Value;

    public IReadOnlyList<CatalogGame> Search(string query, int limit = 40)
    {
        var key = SearchKey(query);
        if (key.Length == 0)
            return [];
        if (uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && _steam.Contains(id))
            return _steam[id].Take(limit).ToArray();
        return _index.Select(entry => (entry.Game, Score: MatchScore(entry, key)))
            .Where(entry => entry.Score < 4)
            .OrderBy(entry => entry.Score)
            .ThenBy(entry => entry.Game.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(entry => entry.Game)
            .ToArray();
    }

    public IEnumerable<CatalogGame> FromSteam(uint id) => _steam[id];

    public static string SearchKey(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .EnumerateRunes().Where(Rune.IsLetterOrDigit).Select(rune => Rune.ToUpperInvariant(rune).ToString()));

    private static string[] SearchKeys(IEnumerable<string> names) => names.Select(SearchKey)
        .Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static int MatchScore(SearchEntry entry, string query)
    {
        if (entry.Titles.Contains(query, StringComparer.Ordinal)) return 0;
        if (entry.Aliases.Contains(query, StringComparer.Ordinal)) return 1;
        if (entry.Titles.Any(name => name.StartsWith(query, StringComparison.Ordinal))
            || entry.Aliases.Any(name => name.StartsWith(query, StringComparison.Ordinal))) return 2;
        return entry.Titles.Any(name => name.Contains(query, StringComparison.Ordinal))
            || entry.Aliases.Any(name => name.Contains(query, StringComparison.Ordinal)) ? 3 : 4;
    }

    private static GameSaveCatalog Load()
    {
        using var asset = typeof(GameSaveCatalog).Assembly.GetManifestResourceStream("SaveCatalog.json.gz")
            ?? throw new InvalidDataException("The embedded save catalog is missing.");
        using var stream = new GZipStream(asset, CompressionMode.Decompress);
        var data = JsonSerializer.Deserialize<Snapshot>(stream);
        if (data?.Version != 2 || data.Games.Length == 0)
            throw new InvalidDataException("Unsupported save catalog format.");
        return new GameSaveCatalog(data);
    }
}
