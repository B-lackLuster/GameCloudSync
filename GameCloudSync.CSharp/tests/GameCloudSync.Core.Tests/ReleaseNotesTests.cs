using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public class ReleaseNotesTests
{
    [Fact]
    public void Parse_ReleaseNotesAsWrittenOnGitHub()
    {
        const string body = "## v2.2.2 — 添加第一个游戏后自动开始监控\n\n" +
                            "v2.2.1 以来的改动。与 v2.2.1 数据完全兼容：用新的 exe 替换旧的即可。\n\n" +
                            "### 修复\n" +
                            "- 以空列表启动后，加进来的游戏会**立即**开始监控\n" +
                            "- 恢复存档途中添加游戏，不会在正被替换的存档文件夹上开始监控\n\n" +
                            "`GameCloudSync.exe` 为自包含单文件。SHA256：`b552cae0`\n";

        var lines = ReleaseNotes.Parse(body);

        Assert.Equal(
        [
            new NoteLine(NoteKind.Heading, "v2.2.2 — 添加第一个游戏后自动开始监控"),
            new NoteLine(NoteKind.Text, "v2.2.1 以来的改动。与 v2.2.1 数据完全兼容：用新的 exe 替换旧的即可。"),
            new NoteLine(NoteKind.Heading, "修复"),
            new NoteLine(NoteKind.Bullet, "以空列表启动后，加进来的游戏会立即开始监控"),
            new NoteLine(NoteKind.Bullet, "恢复存档途中添加游戏，不会在正被替换的存档文件夹上开始监控"),
            new NoteLine(NoteKind.Text, "GameCloudSync.exe 为自包含单文件。SHA256：b552cae0")
        ], lines);
    }

    [Fact]
    public void Parse_KeepsLinkTextAndDropsImagesAndTags()
    {
        var lines = ReleaseNotes.Parse(
            "* See [the README](https://example.test/readme) for details\r\n" +
            "![screenshot](https://example.test/shot.png)\r\n" +
            "+ Works on <b>Windows 11</b>\r\n" +
            "> Data stays compatible");

        Assert.Equal(
        [
            new NoteLine(NoteKind.Bullet, "See the README for details"),
            new NoteLine(NoteKind.Bullet, "Works on Windows 11"),
            new NoteLine(NoteKind.Text, "Data stays compatible")
        ], lines);
    }

    [Fact]
    public void Parse_SkipsRulesAndKeepsCodeAsWritten()
    {
        var lines = ReleaseNotes.Parse("Intro\n\n---\n* * *\n```\ndotnet publish -c Release\n```\n___");

        Assert.Equal(
        [
            new NoteLine(NoteKind.Text, "Intro"),
            new NoteLine(NoteKind.Text, "dotnet publish -c Release")
        ], lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\n  \n")]
    public void Parse_NoNotes_GivesNoLines(string? body)
    {
        Assert.Empty(ReleaseNotes.Parse(body));
    }
}
