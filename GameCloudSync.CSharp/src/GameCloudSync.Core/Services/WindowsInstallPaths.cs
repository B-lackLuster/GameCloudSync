using Microsoft.Win32;

namespace GameCloudSync.Core.Services;

internal static class WindowsInstallPaths
{
    public static string? ReadRegistry(RegistryKey root, string path, string value)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(value) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> UbisoftLauncherRoots()
    {
        const string key = @"SOFTWARE\Ubisoft\Launcher";
        var candidates = new[]
        {
            ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir"),
            ReadRegistry(Registry.LocalMachine, key, "InstallDir"),
            ReadRegistry(Registry.CurrentUser, key, "InstallDir"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Ubisoft", "Ubisoft Game Launcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ubisoft", "Ubisoft Game Launcher")
        };
        return candidates.OfType<string>().Where(path => Path.IsPathFullyQualified(path) && Directory.Exists(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
