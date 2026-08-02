using GameCloudSync.Core;
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
            key.SetValue(Constants.AutostartRegName, $"\"{executablePath}\"");
        }
        else
        {
            key.DeleteValue(Constants.AutostartRegName, throwOnMissingValue: false);
        }
    }
}
