using System.Globalization;

namespace GameCloudSync.Core.Util;

public static class BackupName
{
    /// <summary>Parses a backup folder name into (timestamp text, encoded mtime or 0).</summary>
    public static (string Timestamp, long MTime) Parse(string name)
    {
        try
        {
            var ts = name.StartsWith("save_", StringComparison.Ordinal) ? name[5..] : name;
            var idx = ts.IndexOf("_mtime", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var mtimePart = ts[(idx + "_mtime".Length)..];
                return (ts[..idx], long.TryParse(mtimePart, out var mt) ? mt : 0);
            }
            return (ts, 0);
        }
        catch
        {
            return ("00000000_000000", 0);
        }
    }

    public static DateTime? ToDateTime(string timestamp)
    {
        // Two backups within the same second are told apart by a "_2"-style suffix.
        if (timestamp.Length > 15 && timestamp[15] == '_')
            timestamp = timestamp[..15];
        return DateTime.TryParseExact(
            timestamp,
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dt) ? dt : null;
    }

    public static string FormatConfigTime(string filename)
    {
        if (string.IsNullOrEmpty(filename))
            return "-";
        var ts = filename.StartsWith("config_", StringComparison.Ordinal) ? filename[7..] : filename;
        if (ts.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ts = ts[..^5];
        return ToDateTime(ts)?.ToString("yyyy-MM-dd HH:mm:ss") ?? filename;
    }
}
