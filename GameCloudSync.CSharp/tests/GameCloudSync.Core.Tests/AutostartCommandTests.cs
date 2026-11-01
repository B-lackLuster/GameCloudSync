using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public class AutostartCommandTests
{
    [Fact]
    public void Build_QuotesThePathAndAddsTheMinimizedArgument()
    {
        Assert.Equal("\"C:\\Games Sync\\GameCloudSync.exe\" --minimized",
            AutostartCommand.Build(@"C:\Games Sync\GameCloudSync.exe"));
    }

    [Fact]
    public void IsLegacy_IsTrueOnlyForTheExactFormOlderVersionsWrote()
    {
        const string exe = @"C:\Apps\GameCloudSync.exe";
        Assert.True(AutostartCommand.IsLegacy("\"C:\\Apps\\GameCloudSync.exe\"", exe));
        Assert.True(AutostartCommand.IsLegacy("  \"c:\\apps\\gamecloudsync.EXE\"  ", exe));

        Assert.False(AutostartCommand.IsLegacy(AutostartCommand.Build(exe), exe));
        Assert.False(AutostartCommand.IsLegacy("\"D:\\Other\\GameCloudSync.exe\"", exe));
        Assert.False(AutostartCommand.IsLegacy("\"C:\\Apps\\GameCloudSync.exe\" --something", exe));
        Assert.False(AutostartCommand.IsLegacy(null, exe));
    }

    [Theory]
    [InlineData("--minimized", true)]
    [InlineData("--MINIMIZED", true)]
    [InlineData("--other", false)]
    public void StartsMinimized_RecognizesTheArgumentAnywhereInTheList(string arg, bool expected)
    {
        Assert.Equal(expected, AutostartCommand.StartsMinimized(new[] { "--first", arg }));
    }

    [Fact]
    public void StartsMinimized_WithoutArguments_IsFalse()
    {
        Assert.False(AutostartCommand.StartsMinimized(Array.Empty<string>()));
    }
}
