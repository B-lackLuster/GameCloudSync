using GameCloudSync.Core;
using GameCloudSync.Core.Util;
using Microsoft.Win32;

namespace GameCloudSync.App.Services;

public static class AutoStartService
{
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Constants.AutostartRegPath);
            return key?.GetValue(Constants.AutostartRegName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enable, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Constants.AutostartRegPath);
        if (enable)
        {
            key.SetValue(Constants.AutostartRegName, AutostartCommand.Build(executablePath));
        }
        else
        {
            key.DeleteValue(Constants.AutostartRegName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Older versions registered the bare exe path, which opens the main window at every login.
    /// An entry in exactly that form for this exe gets the start-minimized argument; anything else is left alone.
    /// </summary>
    public static void UpgradeLegacyEntry(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Constants.AutostartRegPath, writable: true);
            var stored = key?.GetValue(Constants.AutostartRegName) as string;
            if (key is not null && AutostartCommand.IsLegacy(stored, executablePath))
                key.SetValue(Constants.AutostartRegName, AutostartCommand.Build(executablePath));
        }
        catch
        {
            // The old entry keeps working as it was.
        }
    }
}
