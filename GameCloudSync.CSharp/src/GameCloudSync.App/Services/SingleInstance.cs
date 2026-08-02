using GameCloudSync.Core;
using System.IO;

namespace GameCloudSync.App.Services;

/// <summary>
/// Single-instance guard. When a second instance starts it writes the wakeup
/// file so the first instance can show its window (same protocol as Python).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private Mutex? _mutex;

    public bool TryAcquire(string appDir, string wakeupFileName)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, Constants.MutexName, out var createdNew);
            if (createdNew)
                return true;
        }
        catch
        {
            // Global\ mutex may require privileges; retry with a session-local name.
            try
            {
                _mutex = new Mutex(initiallyOwned: true, "Local\\GameCloudSync_Unique_Mutex", out var createdNew);
                if (createdNew)
                    return true;
            }
            catch
            {
                // fall through
            }
        }

        try
        {
            File.WriteAllText(Path.Combine(appDir, wakeupFileName), "wakeup");
        }
        catch
        {
            // ignore
        }
        return false;
    }

    public void Dispose()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // ignore
        }
        _mutex?.Dispose();
    }
}
