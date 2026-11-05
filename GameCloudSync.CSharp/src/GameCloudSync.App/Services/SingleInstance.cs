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

    /// <param name="waitForPrevious">
    /// How long a running instance is given to exit first; an update starts the new version while the old one closes.
    /// </param>
    public bool TryAcquire(string appDir, string wakeupFileName, TimeSpan waitForPrevious = default)
    {
        var deadline = DateTime.UtcNow + waitForPrevious;
        while (!TryCreate())
        {
            if (DateTime.UtcNow >= deadline)
            {
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
            Thread.Sleep(200);
        }
        return true;
    }

    private bool TryCreate()
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
        // Holding on to the other instance's mutex would keep it in existence after that instance has exited.
        _mutex?.Dispose();
        _mutex = null;
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
