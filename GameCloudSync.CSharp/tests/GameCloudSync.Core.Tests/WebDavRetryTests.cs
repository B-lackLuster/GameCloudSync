using System.Net;
using System.Text;
using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Tests;

public sealed class WebDavRetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gcs_retry_{Guid.NewGuid():N}");

    public WebDavRetryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    /// <summary>Answers each request through a script that knows which request number this is.</summary>
    private sealed class ScriptedHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                lock (Bodies)
                    Bodies.Add(body);
            }
            return await respond(call, request, ct);
        }
    }

    private static ScriptedHandler Always(HttpStatusCode status) =>
        new((_, _, _) => Task.FromResult(new HttpResponseMessage(status)));

    private static WebDavClient Client(HttpMessageHandler handler, int maxAttempts = 3, int timeoutSeconds = 30) =>
        new("https://example.test/dav", "", "", messageHandler: handler, timeoutSeconds: timeoutSeconds,
            maxAttempts: maxAttempts, retryDelay: TimeSpan.Zero);

    private static HttpResponseMessage Listing(string entries) => new((HttpStatusCode)207)
    {
        Content = new StringContent($"<d:multistatus xmlns:d=\"DAV:\">{entries}</d:multistatus>", Encoding.UTF8, "application/xml")
    };

    private static string Entry(string href, bool directory = false) => $"""
        <d:response><d:href>{href}</d:href><d:propstat><d:prop>
        <d:resourcetype>{(directory ? "<d:collection/>" : "")}</d:resourcetype>
        </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        """;

    [Fact]
    public async Task ServerBusy_IsRepeatedUntilItAnswers()
    {
        var handler = new ScriptedHandler((call, _, _) => Task.FromResult(new HttpResponseMessage(
            call < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created)));
        using var client = Client(handler);

        Assert.True(await client.MkColAsync("game"));
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    [InlineData(1)]
    public async Task ServerThatKeepsRefusing_IsAskedOnlyAsOftenAsAllowed(int maxAttempts)
    {
        var handler = Always(HttpStatusCode.TooManyRequests);
        using var client = Client(handler, maxAttempts);

        Assert.False(await client.MkColAsync("game"));
        Assert.Equal(maxAttempts, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnswersThatAnotherTryCannotChange_AreNotRepeated(HttpStatusCode status)
    {
        var handler = Always(status);
        using var client = Client(handler);

        Assert.False(await client.MkColAsync("game"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DroppedConnection_IsRepeated_AndTheFileIsSentInFullEachTime()
    {
        var file = Path.Combine(_root, "slot.sav");
        await File.WriteAllTextAsync(file, "all of the save");
        var handler = new ScriptedHandler((call, _, _) => call == 1
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        using var client = Client(handler);

        Assert.True(await client.PutFileAsync(file, "game/slot.sav"));

        Assert.Equal(2, handler.Calls);
        Assert.Equal(new[] { "all of the save", "all of the save" }, handler.Bodies);
    }

    [Fact]
    public async Task ConnectionThatKeepsDropping_ReachesTheCallerAfterTheLastAttempt()
    {
        var handler = new ScriptedHandler((_, _, _) => throw new HttpRequestException("connection reset"));
        using var client = Client(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PropFindAsync("game"));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task ServerThatCouldNotBeReached_IsWrittenOffForTheRestOfTheRun()
    {
        var handler = new ScriptedHandler((_, _, _) => throw new HttpRequestException("No such host is known."));
        using var client = Client(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.MkColAsync("game"));
        Assert.Equal(3, handler.Calls);

        // The clean-up and the remaining files of the same run fail at once instead of repeating the wait.
        var ex = await Assert.ThrowsAsync<WebDavException>(() => client.DeleteCollectionAsync("game/incomplete_save"));
        await Assert.ThrowsAsync<WebDavException>(() => client.PutFileAsync(Path.Combine(_root, "slot.sav"), "game/slot.sav"));
        Assert.Equal(3, handler.Calls);
        Assert.Contains("No such host is known.", ex.Message);
        Assert.Equal(0, ex.StatusCode);
    }

    [Fact]
    public async Task WrittenOffServer_DoesNotAffectTheNextClient()
    {
        var up = false;
        var handler = new ScriptedHandler((_, _, _) => up
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created))
            : throw new HttpRequestException("connection refused"));
        using var client = Client(handler, maxAttempts: 1);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.MkColAsync("game"));
        await Assert.ThrowsAsync<WebDavException>(() => client.MkColAsync("game"));
        Assert.Equal(1, handler.Calls);

        // A different client (the next backup) starts with a clean slate.
        up = true;
        using var next = Client(handler, maxAttempts: 1);
        Assert.True(await next.MkColAsync("game"));
    }

    [Fact]
    public async Task LockedLocalFile_DoesNotWriteOffTheServer()
    {
        var file = Path.Combine(_root, "locked.sav");
        await File.WriteAllTextAsync(file, "x");
        var handler = Always(HttpStatusCode.Created);
        using var client = Client(handler, maxAttempts: 2);

        await using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => client.PutFileAsync(file, "game/locked.sav"));

        Assert.True(await client.MkColAsync("game"));
    }

    [Fact]
    public async Task LocalFileThatIsGone_IsNotRetried()
    {
        var handler = Always(HttpStatusCode.Created);
        using var client = Client(handler);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            client.PutFileAsync(Path.Combine(_root, "missing.sav"), "game/missing.sav"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ServerThatNeverAnswers_TimesOutAndIsTriedAgainBeforeGivingUp()
    {
        var handler = new ScriptedHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = Client(handler, maxAttempts: 2, timeoutSeconds: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PropFindAsync("game"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancellationByTheCaller_IsNotMistakenForATimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ScriptedHandler(async (_, _, ct) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = Client(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PropFindAsync("game", ct: cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Move_IsNeverRepeated_BecauseALostAnswerCannotBeTold()
    {
        var busy = Always(HttpStatusCode.ServiceUnavailable);
        using (var client = Client(busy))
            Assert.Equal(503, await client.MoveAsync("a", "b", collection: true, overwrite: true));
        Assert.Equal(1, busy.Calls);

        var dropped = new ScriptedHandler((_, _, _) => throw new HttpRequestException("connection reset"));
        using (var client = Client(dropped))
            await Assert.ThrowsAsync<HttpRequestException>(() => client.MoveAsync("a", "b", collection: true, overwrite: true));
        Assert.Equal(1, dropped.Calls);
    }

    [Theory]
    [InlineData(207, true)]
    [InlineData(200, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(405, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    public async Task Probe_SaysWhetherTheServerAnswered_AndAsksOnlyOnce(int status, bool ok)
    {
        var handler = Always((HttpStatusCode)status);
        using var client = Client(handler);

        var probe = await client.ProbeAsync();

        Assert.Equal(ok, probe.Ok);
        Assert.Equal(status, probe.Status);
        if (ok)
            Assert.Empty(probe.Message);
        else
            Assert.Contains(status.ToString(), probe.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Probe_ServerThatCannotBeReached_ReportsWhy()
    {
        var handler = new ScriptedHandler((_, _, _) => throw new HttpRequestException("No such host is known."));
        using var client = Client(handler);

        var probe = await client.ProbeAsync();

        Assert.False(probe.Ok);
        Assert.Equal(0, probe.Status);
        Assert.Contains("No such host is known.", probe.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Probe_ServerThatNeverAnswers_ReportsATimeout()
    {
        var handler = new ScriptedHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = Client(handler, timeoutSeconds: 1);

        var probe = await client.ProbeAsync();

        Assert.False(probe.Ok);
        Assert.Equal(0, probe.Status);
        Assert.NotEmpty(probe.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PropFind_RefusedByTheServer_CarriesTheStatusOnTheException()
    {
        using var client = Client(Always(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.PropFindAsync("game"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task UploadDir_FileRefused_ThrowsWithTheStatusAndTheFileName()
    {
        var local = Path.Combine(_root, "up");
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "a.sav"), "x");
        var handler = new ScriptedHandler((_, request, _) => Task.FromResult(new HttpResponseMessage(
            request.Method.Method == "PUT" ? HttpStatusCode.Forbidden : HttpStatusCode.Created)));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<WebDavException>(() => client.UploadDirAsync(local, "game/save_1"));

        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("403", ex.Message);
        Assert.Contains("a.sav", ex.Message);
    }

    [Fact]
    public async Task UploadDir_FolderRefused_ThrowsBeforeAnyFileIsSent()
    {
        var local = Path.Combine(_root, "up");
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "a.sav"), "x");
        var puts = 0;
        var handler = new ScriptedHandler((_, request, _) =>
        {
            if (request.Method.Method == "PUT")
                Interlocked.Increment(ref puts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<WebDavException>(() => client.UploadDirAsync(local, "game/save_1"));

        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(0, puts);
    }

    [Fact]
    public async Task Download_FileRefused_ThrowsWithTheStatus()
    {
        var handler = new ScriptedHandler((_, request, _) => Task.FromResult(request.Method.Method == "PROPFIND"
            ? Listing(Entry("/dav/game/", true) + Entry("/dav/game/a.sav"))
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<WebDavException>(() => client.DownloadDirAsync("game", Path.Combine(_root, "down")));

        Assert.Equal(500, ex.StatusCode);
        Assert.IsAssignableFrom<IOException>(ex);
    }
}
