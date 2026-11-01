using System.Text.RegularExpressions;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_log_{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_root, "logs", "app.log");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Write_AppendsTimestampedLines_AndCreatesTheFolder()
    {
        using (var log = new FileLog(LogPath))
        {
            log.Write("first");
            log.Write("second");
        }

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] first$", lines[0]);
        Assert.EndsWith("second", lines[1]);
    }

    [Fact]
    public void Write_ASecondRun_KeepsWhatTheFirstRunWrote()
    {
        using (var log = new FileLog(LogPath))
            log.Write("run one");
        using (var log = new FileLog(LogPath))
            log.Write("run two");

        var text = File.ReadAllText(LogPath);
        Assert.Contains("run one", text);
        Assert.Contains("run two", text);
    }

    [Fact]
    public void Write_MultilineMessage_IndentsTheContinuationLines()
    {
        using (var log = new FileLog(LogPath))
            log.Write("config was corrupt\nkept as: x.json");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Equal("    kept as: x.json", lines[1]);
    }

    [Fact]
    public void Write_WhenFull_RotatesAndKeepsOnlyOneOlderFile()
    {
        using (var log = new FileLog(LogPath, maxBytes: 400))
        {
            for (var i = 0; i < 40; i++)
                log.Write($"line {i:00} {new string('x', 30)}");
        }

        Assert.True(File.Exists(LogPath + ".1"));
        Assert.False(File.Exists(LogPath + ".2"));
        Assert.True(new FileInfo(LogPath).Length <= 400);
        Assert.Contains("line 39", File.ReadAllText(LogPath));
    }

    [Fact]
    public void Write_WhileAnotherProgramHasTheFileOpenForReading_StillWrites()
    {
        using var log = new FileLog(LogPath);
        log.Write("before");
        using var reader = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        log.Write("after");

        using var text = new StreamReader(reader);
        Assert.Contains("after", text.ReadToEnd());
    }

    [Fact]
    public async Task Write_FromManyThreads_KeepsEveryLineIntact()
    {
        using (var log = new FileLog(LogPath, maxBytes: 10_000_000))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                    log.Write($"worker {worker} message {i}");
            })));
        }

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(400, lines.Length);
        Assert.All(lines, line => Assert.Matches(new Regex(@"^\[[^\]]+\] worker \d message \d+$"), line));
    }

    [Fact]
    public void Write_WhenTheFileCannotBeCreated_DoesNotThrow()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "a file where a folder is needed");

        using var log = new FileLog(Path.Combine(blocker, "logs", "app.log"));

        log.Write("goes nowhere");
        log.Write("still fine");
    }
}
