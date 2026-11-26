using System.IO.Enumeration;
using GameCloudSync.Core.Models;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Services;

public sealed record SaveSearchRoots(string Home, string AppData, string LocalAppData, string Documents,
    string Public, string ProgramData)
{
    public IReadOnlyList<string> UbisoftLaunchers { get; init; } = [];

    public static SaveSearchRoots CurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetEnvironmentVariable("PUBLIC") ?? "",
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
        { UbisoftLaunchers = WindowsInstallPaths.UbisoftLauncherRoots() };
}

public enum SaveMatchKind { Folder, EmptyFolder, File, AlreadyAdded }

public enum SaveMatchSource { Catalog, UbisoftConnect, Goldberg }

public sealed record SaveMatch(string Path, SaveMatchKind Kind, bool MayBeShared, string? ExistingGame = null,
    SaveMatchSource Source = SaveMatchSource.Catalog);

public sealed record SaveSearchResult(IReadOnlyList<SaveMatch> Matches, bool NeedsInstallFolder, bool HasUnsupportedRules);

public sealed class SaveLocationFinder(SaveSearchRoots roots)
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly EnumerationOptions DirectChildren = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public SaveSearchResult Find(CatalogGame game, string? installFolder, SteamInstallation? steam,
        IReadOnlyList<GameConfig> configuredGames, string cloudRoot, CancellationToken cancellationToken = default)
    {
        var matches = new Dictionary<string, SaveMatch>(Paths);
        var needsInstall = false;
        var unsupported = game.HasRegistrySaves;
        var budget = 3000;
        // This alternate layout has been verified for Shadows. Other numeric folders may belong to other games.
        if (steam == null && game.SteamIds.Contains(3159330u) && Path.IsPathFullyQualified(roots.AppData))
        {
            foreach (var path in Expand(roots.AppData, ["Goldberg UplayEmu Saves", "8006"], 0))
            {
                if (HasShadowsProgress(path, cancellationToken))
                    Record(path, true, SaveMatchSource.Goldberg);
            }
        }
        foreach (var rule in game.Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = rule.Path.Split('/');
            if (rule.Stores.Contains("uplay") && parts.Length == 4 && parts[0] == "<root>"
                && parts[1].Equals("savegames", StringComparison.OrdinalIgnoreCase) && parts[2] == "<storeUserId>"
                && parts[3].Length > 0 && parts[3].All(char.IsAsciiDigit))
            {
                if (steam == null || rule.Stores.Contains("steam"))
                    foreach (var launcher in roots.UbisoftLaunchers)
                        foreach (var path in Expand(launcher, parts, 1))
                            if (Directory.Exists(path)) Record(path, true, SaveMatchSource.UbisoftConnect);
                continue;
            }
            if (!rule.Stores.Contains("") && (steam == null || !rule.Stores.Contains("steam")))
                continue;

            var anchor = parts[0];
            var root = ResolveRoot(parts, steam?.Directory ?? installFolder, steam);
            if (string.IsNullOrEmpty(root.Directory))
            {
                needsInstall |= anchor == "<base>" && installFolder == null && steam == null;
                unsupported |= anchor != "<base>";
                continue;
            }
            if (parts.Length <= root.PrefixLength || parts.Length > 24 || !Path.IsPathFullyQualified(root.Directory)
                || !Supported(parts, root.PrefixLength, steam != null))
            {
                unsupported = true;
                continue;
            }

            foreach (var path in Expand(root.Directory, parts, root.PrefixLength))
            {
                if (matches.ContainsKey(path))
                    continue;
                if (File.Exists(path))
                {
                    matches.Add(path, new SaveMatch(path, SaveMatchKind.File, root.MayBeShared));
                    // A single hit shows the location of a file rule without listing an entire save history.
                    break;
                }
                Record(path, root.MayBeShared, SaveMatchSource.Catalog);
            }
        }

        return new SaveSearchResult(matches.Values.OrderBy(match => match.Kind)
            .ThenBy(match => match.Path, Paths).ToArray(), needsInstall, unsupported || budget <= 0);

        void Record(string path, bool mayBeShared, SaveMatchSource source)
        {
            if (matches.ContainsKey(path)) return;
            if (SavePathPolicy.Validate(path, game.Title, cloudRoot) != SavePathError.None)
            {
                unsupported = true;
                return;
            }
            var existing = SavePathPolicy.FindOverlappingGame(path, configuredGames);
            var kind = existing != null ? SaveMatchKind.AlreadyAdded
                : HasFiles(path, cancellationToken) ? SaveMatchKind.Folder : SaveMatchKind.EmptyFolder;
            matches.Add(path, new SaveMatch(path, kind, mayBeShared, existing?.Name, source));
        }

        IEnumerable<string> Expand(string parent, string[] parts, int index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget-- <= 0) yield break;
            var name = parts[index];
            var last = index == parts.Length - 1;
            if (name == "<storeGameId>")
                name = steam!.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var byAccount = name == "<storeUserId>";
            if (byAccount || name.IndexOfAny(['*', '?']) >= 0)
            {
                foreach (var entry in Entries(parent, budget))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (budget-- <= 0) yield break;
                    var leaf = Path.GetFileName(entry);
                    if (byAccount ? !IsAccountFolder(leaf) : !FileSystemName.MatchesSimpleExpression(name, leaf))
                        continue;
                    if (last && (!byAccount || Directory.Exists(entry))) yield return entry;
                    else if (Directory.Exists(entry))
                        foreach (var match in Expand(entry, parts, index + 1)) yield return match;
                }
                yield break;
            }
            var child = Path.Combine(parent, name);
            if (!IsOrdinaryEntry(child)) yield break;
            if (last) yield return child;
            else if (Directory.Exists(child))
                foreach (var match in Expand(child, parts, index + 1)) yield return match;
        }
    }

    private (string? Directory, int PrefixLength, bool MayBeShared) ResolveRoot(string[] parts,
        string? installFolder, SteamInstallation? steam) => parts[0] switch
    {
        "<base>" => (installFolder, 1, false),
        // Cloud save caches live under the Steam client root even if the game is installed on another disk.
        "<root>" when steam != null && parts.Length > 4 && parts[1].Equals("userdata", StringComparison.OrdinalIgnoreCase)
                       && parts[2] == "<storeUserId>" && (parts[3] == "<storeGameId>"
                           || parts[3] == steam.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            => (steam.ClientRoot, 1, false),
        "<home>" when parts.Length > 1 && parts[1].Equals("Documents", StringComparison.OrdinalIgnoreCase)
            => (roots.Documents, 2, true),
        "<home>" => (roots.Home, 1, true),
        "<winAppData>" => (roots.AppData, 1, true),
        "<winLocalAppData>" => (roots.LocalAppData, 1, true),
        "<winLocalAppDataLow>" => (string.IsNullOrEmpty(roots.Home) ? null : Path.Combine(roots.Home, "AppData", "LocalLow"), 1, true),
        "<winDocuments>" => (roots.Documents, 1, true),
        "<winPublic>" => (roots.Public, 1, true),
        "<winProgramData>" => (roots.ProgramData, 1, true),
        _ => (null, 1, true)
    };

    private static bool Supported(string[] parts, int start, bool isSteam)
    {
        // Require a literal game-specific prefix before expanding a wildcard or account placeholder.
        if (parts[start].IndexOfAny(['*', '?', '<']) >= 0)
            return false;
        for (var i = start; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == "<storeUserId>" || (isSteam && part == "<storeGameId>"))
                continue;
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.Contains("**")
                || part.IndexOfAny(['<', '>', '\\', ':', '|', '"', '[', ']']) >= 0
                || part.Any(char.IsControl))
                return false;
        }
        return true;
    }

    private static bool IsAccountFolder(string value) => Guid.TryParseExact(value, "D", out _)
        || (value.Length is > 0 and <= 32 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));

    private static bool HasShadowsProgress(string directory, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "ACShadows[?*].save", DirectChildren))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Paths.Equals(Path.GetFileName(file), "ACShadows[Options].save")) return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static bool IsOrdinaryEntry(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string[] Entries(string path, int limit)
    {
        try { return Directory.EnumerateFileSystemEntries(path, "*", DirectChildren).Take(limit).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static bool HasFiles(string directory, CancellationToken cancellationToken)
    {
        var pending = new Queue<string>();
        pending.Enqueue(directory);
        var budget = 256;
        while (pending.Count > 0 && budget > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Entries(pending.Dequeue(), budget))
            {
                budget--;
                if (Directory.Exists(path)) pending.Enqueue(path);
                else if (File.Exists(path) && !Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(path).Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
}
