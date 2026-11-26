using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace GameCloudSync.Core.Services;

public sealed record SteamInstallation(uint AppId, string Title, string Directory, string ClientRoot);

public static class SteamInstallations
{
    public static IReadOnlyList<SteamInstallation> Find(CancellationToken cancellationToken = default)
    {
        var roots = new[]
        {
            WindowsInstallPaths.ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            WindowsInstallPaths.ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        };
        return roots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(path => Read(path!, cancellationToken))
            .DistinctBy(game => (game.AppId, game.Directory.ToUpperInvariant()))
            .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<SteamInstallation> Read(string clientRoot, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(clientRoot) || !Directory.Exists(clientRoot))
            return [];
        clientRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientRoot));
        var libraryFolders = ReadVdf(Path.Combine(clientRoot, "steamapps", "libraryfolders.vdf"))?.Child("libraryfolders");
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { clientRoot };
        if (libraryFolders != null)
        {
            foreach (var node in libraryFolders.Entries)
            {
                if (uint.TryParse(node.Key, out _) && (node.Value.Text ?? node.Value.Child("path")?.Text) is { } path
                    && Path.IsPathFullyQualified(path) && Directory.Exists(path))
                    libraries.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
        }

        var games = new List<SteamInstallation>();
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamapps = Path.Combine(library, "steamapps");
            string[] manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var path in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = ReadVdf(path)?.Child("AppState");
                if (state?.Child("installdir")?.Text is not { Length: > 0 } folder
                    || state.Child("name")?.Text is not { Length: > 0 } name
                    || folder is "." or ".."
                    || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || !uint.TryParse(state.Child("appid")?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || id == 0 || !Path.GetFileName(path).Equals($"appmanifest_{id}.acf", StringComparison.OrdinalIgnoreCase))
                    continue;
                var install = Path.Combine(steamapps, "common", folder);
                if (Directory.Exists(install))
                    games.Add(new SteamInstallation(id, name, install, clientRoot));
            }
        }
        return games;
    }

    private static Vdf? ReadVdf(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 2_000_000)
                return null;
            return new VdfReader(File.ReadAllText(path)).Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private sealed record Vdf(string? Text = null)
    {
        public Dictionary<string, Vdf> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Vdf? Child(string key) => Entries.GetValueOrDefault(key);
    }

    // Steam's local manifests use Valve's text KeyValues format.
    private sealed class VdfReader(string input)
    {
        private int _offset;
        public Vdf Read() => ReadObject(0);

        private Vdf ReadObject(int depth)
        {
            if (depth > 16) throw new FormatException("Steam manifest is nested too deeply.");
            var node = new Vdf();
            while (ReadToken() is { } key)
            {
                if (key == "}" && depth > 0) return node;
                if (key is "{" or "}") throw new FormatException("Unexpected Steam manifest token.");
                var value = ReadToken();
                node.Entries[key] = value switch
                {
                    "{" => ReadObject(depth + 1),
                    null or "}" => throw new FormatException("Missing Steam manifest value."),
                    _ => new Vdf(value)
                };
            }
            return depth == 0 ? node : throw new FormatException("Unclosed Steam manifest block.");
        }

        private string? ReadToken()
        {
            while (_offset < input.Length)
            {
                var ch = input[_offset];
                if (char.IsWhiteSpace(ch)) { _offset++; continue; }
                if (ch != '/' || _offset + 1 >= input.Length || input[_offset + 1] != '/') break;
                while (_offset < input.Length && input[_offset] != '\n') _offset++;
            }

            if (_offset >= input.Length) return null;
            var start = _offset;
            var first = input[_offset++];
            if (first is '{' or '}') return first.ToString();
            if (first != '"')
            {
                while (_offset < input.Length && !char.IsWhiteSpace(input[_offset]) && input[_offset] is not ('{' or '}'))
                    _offset++;
                return input[start.._offset];
            }

            var value = new StringBuilder();
            while (_offset < input.Length)
            {
                var ch = input[_offset++];
                if (ch == '"') return value.ToString();
                if (ch == '\\' && _offset < input.Length && input[_offset] is '\\' or '"')
                    ch = input[_offset++];
                value.Append(ch);
            }
            throw new FormatException("Unclosed Steam manifest string.");
        }
    }
}
