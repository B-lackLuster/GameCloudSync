using GameCloudSync.Core.Services;
using GameCloudSync.Core.Util;

namespace GameCloudSync.Core.Tests;

public class WebDavUrlTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("https://dav.example.com/dav", "https://dav.example.com/dav")]
    [InlineData("http://192.168.1.5:5005/dav", "http://192.168.1.5:5005/dav")]
    [InlineData("  https://dav.example.com/dav/  ", "https://dav.example.com/dav/")]
    [InlineData("dav.example.com/dav/", "https://dav.example.com/dav/")]
    [InlineData("192.168.1.5:5005", "https://192.168.1.5:5005")]
    [InlineData("localhost:8080/dav", "https://localhost:8080/dav")]
    public void Normalize_AcceptsAddressesAndAddsTheMissingScheme(string input, string expected)
    {
        Assert.Equal(WebDavUrlError.None, WebDavUrl.Normalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("ftp://example.com/dav", WebDavUrlError.UnsupportedScheme)]
    [InlineData("webdav://example.com/dav", WebDavUrlError.UnsupportedScheme)]
    [InlineData("htps://example.com/dav", WebDavUrlError.UnsupportedScheme)]
    [InlineData("https://", WebDavUrlError.Malformed)]
    [InlineData("https://exa mple.com/dav", WebDavUrlError.Malformed)]
    [InlineData("https://example.com/dav?token=1", WebDavUrlError.QueryOrFragment)]
    [InlineData("https://example.com/dav#top", WebDavUrlError.QueryOrFragment)]
    public void Normalize_RejectsAddressesThatCouldNeverWork(string input, WebDavUrlError expected)
    {
        Assert.Equal(expected, WebDavUrl.Normalize(input, out _));
    }

    [Fact]
    public void Describe_SaysSomethingForEveryError()
    {
        foreach (var error in Enum.GetValues<WebDavUrlError>().Where(e => e != WebDavUrlError.None))
            Assert.NotEmpty(WebDavUrl.Describe(error, "bad-address"));
        Assert.Empty(WebDavUrl.Describe(WebDavUrlError.None, ""));
    }

    [Fact]
    public void Client_AddsTheMissingSchemeInsteadOfFailingOnEveryRequest()
    {
        using var client = new WebDavClient("dav.example.com/dav/", "", "");
        Assert.Equal("https://dav.example.com/dav", client.BaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com/dav")]
    [InlineData("https://example.com/dav?x=1")]
    public void Client_RefusesAnAddressThatCannotWork(string url)
    {
        Assert.Throws<ArgumentException>(() => new WebDavClient(url, "", ""));
    }
}
