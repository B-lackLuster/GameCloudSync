using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Util;

public static class RelativeTime
{
    /// <summary>"just now", "5 min ago", "3 h ago", "2 d ago" — or the date once it is a month or more ago.</summary>
    public static string Format(DateTime then, DateTime now)
    {
        var age = now - then;
        // A backup stamped a little in the future (clocks differ between PCs) is still "just now".
        if (age < TimeSpan.FromMinutes(1))
            return Localization.T("Time.JustNow");
        if (age < TimeSpan.FromHours(1))
            return Localization.T("Time.MinutesAgo", (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1))
            return Localization.T("Time.HoursAgo", (int)age.TotalHours);
        if (age < TimeSpan.FromDays(30))
            return Localization.T("Time.DaysAgo", (int)age.TotalDays);
        return then.ToString("yyyy-MM-dd");
    }

    /// <summary>A file time in Unix seconds, as local "yyyy-MM-dd HH:mm:ss".</summary>
    public static string FormatUnix(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
