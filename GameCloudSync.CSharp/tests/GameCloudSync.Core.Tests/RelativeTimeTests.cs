using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0);

    // The words depend on the UI language, which other tests switch; the numbers do not.
    [Theory]
    [InlineData(5 * 60, "5")]
    [InlineData(59 * 60 + 30, "59")]
    [InlineData(3 * 3600 + 120, "3")]
    [InlineData(2 * 86400 + 3600, "2")]
    public void Format_ShowsTheAgeInTheLargestFittingUnit(int secondsAgo, string expectedNumber)
    {
        var text = RelativeTime.Format(Now.AddSeconds(-secondsAgo), Now);
        Assert.Contains(expectedNumber, text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(-120)]
    public void Format_ForTheLastMinuteOrTheFuture_HasNoNumber(int secondsAgo)
    {
        Assert.DoesNotMatch(@"\d", RelativeTime.Format(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void Format_AMonthOrMoreAgo_ShowsTheDate()
    {
        Assert.Equal("2026-09-01", RelativeTime.Format(new DateTime(2026, 9, 1, 8, 30, 0), Now));
    }
}
