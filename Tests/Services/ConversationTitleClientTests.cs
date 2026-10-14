using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services;

public sealed class ConversationTitleClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ConversationTitleSettings Enabled = new(true, "synthetic-api-key");

    [Fact]
    public async Task OnlyTheOpeningThousandCharactersAreSent()
    {
        // VIBE-157: the title API rejects more than 2,000 characters, so a full transcript was
        // refused every time. The owner chose the first 1,000 characters.
        var transcript = "User: fix the login redirect " + new string('x', 8000) + "\nAgent: complete trailing response";
        string? sentText = null;
        var handler = new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            sentText = json.RootElement.GetProperty("text").GetString();
            return Reply("{\"title\":\"Fix login configuration\"}");
        });

        var result = await Create(handler).GenerateAsync(transcript, Enabled, Ct);

        Assert.Equal(transcript[..ConversationTitleClient.MaxExcerptChars], sentText);
        Assert.Equal(1_000, ConversationTitleClient.MaxExcerptChars);
        Assert.Equal("Fix login configuration", result.Title);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sentText!))), result.ExcerptHash);
    }

    [Fact]
    public async Task RedactsBeforeTakingTheExcerptAndSendsTheHeaderCredential()
    {
        // "password=" ends at character 995, so cutting first would send the start of the value.
        var transcript = "User: " + new string('x', 979) + "\npassword=do-not-send-this\nAgent: trailing response";
        var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(ConversationTitleClient.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal(Enabled.ApiKey, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var sent = json.RootElement.GetProperty("text").GetString();
            Assert.Equal("User: " + new string('x', 979) + "\npassword=XXXXX", sent);
            Assert.DoesNotContain("do-no", sent);
            return Reply("""{"title":"Fix login after refresh"}""");
        });
        var client = Create(handler);
        Assert.Equal("Fix login after refresh", (await client.GenerateAsync(transcript, Enabled, Ct)).Title);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public void ExcerptNeverEndsInsideASurrogatePair()
    {
        var text = new string('a', ConversationTitleClient.MaxExcerptChars - 1) + "\U0001F600" + "tail";

        var excerpt = ConversationTitleClient.Excerpt(text);

        Assert.Equal(new string('a', ConversationTitleClient.MaxExcerptChars - 1), excerpt);
        Assert.Equal("short", ConversationTitleClient.Excerpt("short"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingHashSkipsTheExactRedactedExcerpt(bool surrogateBoundary)
    {
        var opening = surrogateBoundary
            ? new string('a', 999) + "\U0001F600"
            : "User: " + new string('x', 979) + "\npassword=first-secret";
        string? sent = null;
        var handler = new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            sent = json.RootElement.GetProperty("text").GetString();
            return Reply("{\"title\":\"First title\"}");
        });
        var client = Create(handler);
        var first = await client.GenerateAsync(opening + "\nAgent: first answer", Enabled, Ct);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sent!))), first.ExcerptHash);

        var refresh = await client.GenerateAsync(opening.Replace("first-secret", "changed-secret")
            + "\nAgent: different answer", Enabled, Ct, first.ExcerptHash);

        Assert.True(refresh.Unchanged);
        Assert.Null(refresh.Title);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("{\"password\":\"hunter2\"}")]
    [InlineData("Authorization: Bearer hunter2")]
    [InlineData("postgresql://user:hunter2@db.example/app")]
    [InlineData("tool --password hunter2")]
    [InlineData("<password>hunter2</password>")]
    [InlineData("deploy with synthetic-api-key")]
    public async Task CredentialsAreMaskedInTheRequestAndRejectedAsTitles(string credential)
    {
        string? sentText = null;
        var handler = new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            sentText = json.RootElement.GetProperty("text").GetString();
            return Reply(JsonSerializer.Serialize(new { title = credential }));
        });

        var result = await Create(handler).GenerateAsync(
            $"User: keep this context\n{credential}\nAgent: keep the answer", Enabled, Ct);

        Assert.NotNull(sentText);
        Assert.DoesNotContain("hunter2", sentText);
        Assert.DoesNotContain(Enabled.ApiKey, sentText);
        Assert.Contains("XXXXX", sentText);
        Assert.StartsWith("User: keep this context\n", sentText);
        Assert.EndsWith("\nAgent: keep the answer", sentText);
        Assert.Null(result.Title);
        Assert.False(result.KeyWide);
    }

    [Theory]
    [InlineData("Fix password reset after refresh")]
    [InlineData("Password reset email")]
    [InlineData("Token budget tuning")]
    [InlineData("I want my password alg to be BlowFish not this stupid PBKDF2 thing?")]
    [InlineData("Support Basic authorization headers")]
    [InlineData("Preserve cookie settings")]
    [InlineData("Preserve literal \\u0041 escapes")]
    [InlineData("Connect postgresql://db.example/app")]
    [InlineData("Use <configuration>localhost</configuration>")]
    public async Task OrdinaryCredentialDiscussionIsPreserved(string text)
    {
        var handler = new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(text, json.RootElement.GetProperty("text").GetString());
            return Reply(JsonSerializer.Serialize(new { title = text }));
        });

        Assert.Equal(text, (await Create(handler).GenerateAsync(text, Enabled, Ct)).Title);
    }

    [Theory]
    [InlineData(false, "synthetic-api-key")]
    [InlineData(true, "")]
    public async Task MissingPermissionOrKeyNeverSends(bool enabled, string key)
    {
        var settings = new ConversationTitleSettings(enabled, key);
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        var client = new ConversationTitleClient(new HttpClient(handler), () => settings);
        Assert.Null((await client.GenerateAsync("full transcript", settings, Ct)).Title);
        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [InlineData(401, 3600, false, true)]
    [InlineData(403, 3600, false, true)]
    [InlineData(404, 3600, false, true)]
    [InlineData(405, 3600, false, true)]
    [InlineData(429, 120, false, true)]
    [InlineData(503, 120, false, true)]
    [InlineData(502, 300, false, true)]
    [InlineData(504, 300, false, true)]
    [InlineData(500, 300, false, false)]
    [InlineData(413, 300, true, false)]
    [InlineData(302, 300, true, false)]
    public async Task ErrorsPreserveExistingTitleAndChooseBackoff(int code, int seconds, bool permanent, bool keyWide)
    {
        var handler = new Handler((_, _) =>
        {
            var response = Reply("untrusted error text containing synthetic-api-key", (HttpStatusCode)code);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        var result = await Create(handler).GenerateAsync("full transcript", Enabled, Ct);
        Assert.Null(result.Title);
        Assert.Null(result.ExcerptHash);
        Assert.False(result.Unchanged);
        Assert.Equal(TimeSpan.FromSeconds(seconds), result.RetryAfter);
        Assert.Equal(permanent, result.Permanent);
        Assert.Equal(keyWide, result.KeyWide);
    }

    [Fact]
    public async Task UnreachableEndpointPausesTheKey()
    {
        var result = await Create(new Handler((_, _) => throw new HttpRequestException("connection refused")))
            .GenerateAsync("full transcript", Enabled, Ct);
        Assert.Null(result.Title);
        Assert.True(result.KeyWide);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"title\":\" \"}")]
    [InlineData("{\"title\":42}")]
    [InlineData("{\"title\":\"line\\nline\"}")]
    [InlineData("{\"title\":\"synthetic-api-key\"}")]
    [InlineData("{\"title\":\"password=do-not-save-secret\"}")]
    [InlineData("{\"title\":\"\\u202ehidden\"}")]
    public async Task InvalidTitlesAreNeverReturned(string response)
    {
        var result = await Create(new Handler((_, _) => Task.FromResult(Reply(response))))
            .GenerateAsync("full transcript", Enabled, Ct);
        Assert.Null(result.Title);
    }

    [Theory]
    [InlineData(161)]
    [InlineData(9000)]
    public async Task OversizedTitlesOrResponsesAreRejected(int size)
    {
        var result = await Create(new Handler((_, _) => Task.FromResult(Reply("{\"title\":\"" + new string('a', size) + "\"}"))))
            .GenerateAsync("full transcript", Enabled, Ct);
        Assert.Null(result.Title);
    }

    [Fact]
    public async Task ResponseLimitAppliesWithoutContentLength()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[9000]))
        }));
        Assert.Null((await Create(handler).GenerateAsync("full transcript", Enabled, Ct)).Title);
    }

    [Fact]
    public async Task SettingsChangeDuringRequestDiscardsResult()
    {
        var settings = Enabled;
        var handler = new Handler((_, _) =>
        {
            settings = new(true, "replacement-key");
            return Task.FromResult(Reply("{\"title\":\"Late title\"}"));
        });
        var client = new ConversationTitleClient(new HttpClient(handler), () => settings);
        Assert.Null((await client.GenerateAsync("full transcript", Enabled, Ct)).Title);
    }

    [Fact]
    public async Task DisablingRemoteAccessCancelsInflightRequest()
    {
        var settings = Enabled;
        var handler = new Handler(async (_, ct) =>
        {
            settings = new(false, Enabled.ApiKey);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        });
        var client = new ConversationTitleClient(new HttpClient(handler), () => settings);
        Assert.Null((await client.GenerateAsync("full transcript", Enabled, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)).Title);
    }

    [Fact]
    public async Task TimeoutIsRetryableButShutdownPropagatesCancellation()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        });
        var client = new ConversationTitleClient(new HttpClient(handler), () => Enabled, TimeSpan.FromMilliseconds(20));
        var timedOut = await client.GenerateAsync("full transcript", Enabled, Ct);
        Assert.Null(timedOut.Title);
        Assert.False(timedOut.KeyWide); // A slow request is this conversation's problem.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync("full transcript", Enabled, cancelled.Token));
    }

    private static ConversationTitleClient Create(Handler handler) => new(new HttpClient(handler), () => Enabled);
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return send(request, cancellationToken);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
