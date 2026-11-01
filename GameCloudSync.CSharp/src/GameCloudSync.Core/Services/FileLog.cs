using System.Text;

namespace GameCloudSync.Core.Services;

/// <summary>
/// Appends log lines to a file, keeping at most one rotated copy (<c>.1</c>) so the log never grows
/// without bound. Writing never throws: a log that can't be written must not break a backup.
/// </summary>
public sealed class FileLog : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _lock = new();
    private FileStream? _stream;
    private long _size;
    private DateTime _nextOpenAttemptUtc = DateTime.MinValue;

    public FileLog(string path, long maxBytes = Constants.MaxLogFileBytes)
    {
        _path = path;
        _maxBytes = Math.Max(1, maxBytes);
    }

    public string Path => _path;
    public string Directory => System.IO.Path.GetDirectoryName(_path) ?? "";

    public void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message.ReplaceLineEndings("\r\n    ")}\r\n";
        var bytes = Utf8.GetBytes(line);
        lock (_lock)
        {
            try
            {
                if (!EnsureOpen())
                    return;
                if (_size > 0 && _size + bytes.Length > _maxBytes)
                    Rotate();
                _stream!.Write(bytes);
                _stream.Flush();
                _size += bytes.Length;
            }
            catch
            {
                // Disk full, folder removed, file locked: drop this line and try to reopen later.
                CloseStream();
                _nextOpenAttemptUtc = DateTime.UtcNow + ReopenDelay;
            }
        }
    }

    private bool EnsureOpen()
    {
        if (_stream is not null)
            return true;
        if (DateTime.UtcNow < _nextOpenAttemptUtc)
            return false;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            _stream = Open(FileMode.Append);
            _size = _stream.Length;
            return true;
        }
        catch
        {
            _nextOpenAttemptUtc = DateTime.UtcNow + ReopenDelay;
            return false;
        }
    }

    private FileStream Open(FileMode mode) =>
        // Other programs (a text editor, a log viewer) may read the file while the app runs.
        new(_path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    private void Rotate()
    {
        CloseStream();
        var rotated = _path + ".1";
        try
        {
            File.Move(_path, rotated, overwrite: true);
            _stream = Open(FileMode.Create);
        }
        catch
        {
            // Couldn't rename (the file is held open elsewhere): start over in place instead.
            _stream = Open(FileMode.Create);
        }
        _size = 0;
    }

    private void CloseStream()
    {
        try { _stream?.Dispose(); } catch { /* nothing left to flush */ }
        _stream = null;
    }

    public void Dispose()
    {
        lock (_lock)
            CloseStream();
    }
}
