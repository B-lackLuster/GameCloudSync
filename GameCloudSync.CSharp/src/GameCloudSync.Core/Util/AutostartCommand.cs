namespace GameCloudSync.Core.Util;

/// <summary>Builds and recognizes the command line kept in the Windows "Run" key.</summary>
public static class AutostartCommand
{
    public static string Build(string exePath) => $"\"{exePath}\" {Constants.StartMinimizedArg}";

    /// <summary>
    /// True for the exact form older versions wrote (only the quoted exe path). Entries pointing at
    /// another location or carrying other arguments belong to someone else and are left alone.
    /// </summary>
    public static bool IsLegacy(string? stored, string exePath) =>
        stored is not null && string.Equals(stored.Trim(), $"\"{exePath}\"", StringComparison.OrdinalIgnoreCase);

    public static bool StartsMinimized(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, Constants.StartMinimizedArg, StringComparison.OrdinalIgnoreCase));
}
