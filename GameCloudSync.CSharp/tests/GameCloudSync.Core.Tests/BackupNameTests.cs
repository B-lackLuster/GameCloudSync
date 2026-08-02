using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public class BackupNameTests
{
    [Fact]
    public void Parse_NewFormat_ReturnsTimestampAndMTime()
    {
        var (ts, mtime) = BackupName.Parse("save_20260512_143000_mtime1715500200");
        Assert.Equal("20260512_143000", ts);
        Assert.Equal(1715500200, mtime);
    }

    [Fact]
    public void Parse_OldFormat_ReturnsZeroMTime()
    {
        var (ts, mtime) = BackupName.Parse("save_20260512_143000");
        Assert.Equal("20260512_143000", ts);
        Assert.Equal(0, mtime);
    }

    [Fact]
    public void ToDateTime_ParsesTimestamp()
    {
        var dt = BackupName.ToDateTime("20260512_143000");
        Assert.NotNull(dt);
        Assert.Equal(2026, dt!.Value.Year);
        Assert.Equal(14, dt.Value.Hour);
    }
}
