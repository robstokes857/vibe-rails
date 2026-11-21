using System.Net;
using System.Text;
using Tests.Services.Integrations;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardSyncHttpClientTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3600, 60)]
    public async Task RateLimitRetryAfterIsBoundedWithoutReadingRemoteProse(int seconds, int expected)
    {
        var client = Client(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("PRIVATE server error") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return response;
        }));
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PullAsync("id", 0, 200, Ct));
        Assert.Equal(429, error.Status);
        Assert.Equal(TimeSpan.FromSeconds(expected), error.RetryAfter);
        Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Fact]
    public async Task RemoteManagementPagesAndDeletesUsingPinnedCredentials()
    {
        var requests = new List<string>();
        var client = Client(new Handler(request =>
        {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            return new(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Get ? "[]" : "{\"deleted\":true}") };
        }));
        Assert.Empty(await client.DiscoverPageAsync(100, Ct, client.DestinationKey!));
        await client.DeleteRemoteBoardAsync("remote", Ct, client.DestinationKey!);
        Assert.Equal(["GET /api/v1/boards/?offset=100", "DELETE /api/v1/boards/remote"], requests);
        await Assert.ThrowsAsync<BoardSyncClientException>(() => client.DeleteRemoteBoardAsync("remote", Ct, "changed"));
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"deleted\":false}")]
    public async Task RemoteDeletionRequiresAnExplicitAcknowledgement(string body)
    {
        var client = Client(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) }));
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.DeleteRemoteBoardAsync("remote", Ct, client.DestinationKey!));
        Assert.Equal("invalid_response", error.Code);
    }

    [Fact]
    public async Task RemoteLaunchUsesPinnedHeaderCredentialsAndSourceGeneratedWireTypes()
    {
        var id = Guid.NewGuid(); var board = Guid.NewGuid(); var instance = Guid.NewGuid();
        var sent = new List<string>();
        var client = Client(new Handler(request =>
        {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.Equal(HttpMethod.Post, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            sent.Add(path);
            return new(HttpStatusCode.OK) { Content = new StringContent(path.EndsWith("/poll", StringComparison.Ordinal)
                ? $$$"""{"command":{"id":"{{{id}}}","boardId":"{{{board}}}","cardId":"card","requiredSeq":12}}"""
                : """{"saved":true}""") };
        }));
        var poll = await client.PollLaunchAsync(new(instance, "Desktop", [board]), Ct, client.DestinationKey!);
        Assert.Equal(new BoardLaunchCommand(id, board, "card", 12), poll.Command);
        Assert.True((await client.CompleteLaunchAsync(id, new(instance, "started", Guid.NewGuid().ToString()), Ct, client.DestinationKey!)).Saved);
        Assert.Equal(["/api/v1/boards/launches/poll", $"/api/v1/boards/launches/{id}/result"], sent);
        await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PollLaunchAsync(new(instance, "Desktop", [board]), Ct, "changed"));
        Assert.Equal(2, sent.Count);
    }

    [Theory]
    [InlineData("http://example.com/api", false)]
    [InlineData("https://example.com/api", true)]
    [InlineData("http://127.0.0.1:5555/api", true)]
    [InlineData("https://user:password@example.com/api", false)]
    [InlineData("https://example.com/api?key=secret", false)]
    [InlineData("https://example.com/api#fragment", false)]
    [InlineData("file:///C:/data", false)]
    public void EndpointValidation(string url, bool valid) => Assert.Equal(valid, BoardSyncEndpoint.Resolve(url, null) is not null);

    [Fact]
    public async Task CredentialsAreHeaderOnlyAndResponsesAreParsed()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.Equal("https://example.com/api/v1/boards/remote/entries?after=7&limit=200", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[],"lastSeq":7,"hasMore":false}""") };
        });
        var client = Client(handler);
        var result = await client.PullAsync("remote", 7, 200, Ct, client.DestinationKey);
        Assert.Empty(result.Entries);
        Assert.Equal(7, result.LastSeq);
    }

    [Fact]
    public async Task SharingUsesPinnedHeaderCredentialsAndAotWireTypes()
    {
        var sent = new List<string>();
        var handler = new Handler(request =>
        {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            var path = request.RequestUri!.AbsolutePath;
            sent.Add(request.Method + " " + path);
            var body = path.EndsWith("/boards/", StringComparison.Ordinal)
                ? """[{"remoteBoardId":"remote","name":"Shared","keyPrefix":"VB","displayPrefix":"TEAM","lanes":[{"id":"lane","name":"Todo","color":null,"position":0}],"isOwner":false,"lastSeq":4}]"""
                : request.Method == HttpMethod.Get ? """{"limit":3,"invitations":[{"id":"invite","email":"a@example.com","status":"pending"}]}"""
                : """{"saved":true,"message":"Invitation saved."}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var client = Client(handler);
        var discovered = await client.DiscoverAsync(Ct, client.DestinationKey);
        Assert.Equal("Shared", Assert.Single(discovered).Name);
        // Minimal API Results.Ok receives the concrete runtime List in an AOT host.
        var json = System.Text.Json.JsonSerializer.Serialize((object)discovered, VibeRails.DTOs.AppJsonSerializerContext.Default.Options);
        Assert.Contains("\"remoteBoardId\":\"remote\"", json);
        Assert.Equal(3, (await client.GetSharingAsync("remote", Ct, client.DestinationKey)).Limit);
        Assert.True((await client.SaveInviteAsync("remote", null, new("a@example.com"), Ct, client.DestinationKey)).Saved);
        await client.SaveInviteAsync("remote", "invite", new("b@example.com"), Ct, client.DestinationKey);
        await client.RemoveInviteAsync("remote", "invite", Ct, client.DestinationKey);
        Assert.Equal(["GET /api/v1/boards/", "GET /api/v1/boards/remote/sharing", "POST /api/v1/boards/remote/sharing",
            "PUT /api/v1/boards/remote/sharing/invite", "DELETE /api/v1/boards/remote/sharing/invite"], sent);
    }

    [Fact]
    public async Task ChangedCredentialFailsBeforeSending()
    {
        var handler = new Handler(_ => throw new Xunit.Sdk.XunitException("Must not send"));
        var key = "first";
        var client = new BoardSyncHttpClient(new Factory(handler), new Uri("https://example.com/api"), () => key);
        var destination = client.DestinationKey;
        key = "second";
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PullAsync("id", 0, 200, Ct, destination));
        Assert.Equal("destination_changed", error.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":1,\"cardId\":\"another\"}")]
    [InlineData("{\"schema\":2,\"cardId\":\"card_1\"}")]
    public async Task ActivityRequiresAnExplicitMatchingAcknowledgement(string body)
    {
        var client = Client(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.EndsWith("/remote/cards/card_1/activity", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PutActivityAsync("remote", "card_1", new(1, [], [], [], [], []), Ct, client.DestinationKey));
        Assert.Equal("invalid_response", error.Code);
    }

    [Fact]
    public async Task AChangedEndpointIsNeverUsedUnderTheOldApproval_AndIsUsedOnceApprovedAgain()
    {
        var sent = new List<string>();
        var handler = new Handler(request =>
        {
            sent.Add(request.RequestUri!.Host);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[],"lastSeq":0,"hasMore":false}""") };
        });
        var endpoint = new Uri("https://old.example.com/api");
        var client = new BoardSyncHttpClient(new Factory(handler), () => endpoint, () => "key");
        var approved = client.DestinationKey;
        await client.PullAsync("id", 0, 20, Ct, approved);

        // The frontend URL moved after the client was built: the old approval stops the upload
        // instead of the client carrying on to the host it started with.
        endpoint = new Uri("https://new.example.com/api");
        Assert.Equal(endpoint, client.Endpoint);
        Assert.NotEqual(approved, client.DestinationKey);
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PullAsync("id", 0, 20, Ct, approved));
        Assert.Equal("destination_changed", error.Code);

        await client.PullAsync("id", 0, 20, Ct, client.DestinationKey);
        Assert.Equal(["old.example.com", "new.example.com"], sent);
    }

    [Fact]
    public async Task RemoteErrorsNeverExposeResponseBodies()
    {
        var client = Client(new Handler(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"SECRET on remote server","code":"invalid_entry"}""")
        }));
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PullAsync("id", 0, 200, Ct));
        Assert.DoesNotContain("SECRET", error.Message);
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task RejectedEntryMetadataIsParsedWithoutRemoteProse()
    {
        var client = Client(new Handler(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"SECRET on remote server","code":"invalid_entry","entryId":"log_123"}""")
        }));
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PushAsync("id", new(null, null, null, []), Ct));
        Assert.Equal("invalid_entry", error.Code);
        Assert.Equal("log_123", error.EntryId);
        Assert.DoesNotContain("SECRET", error.Message);
    }

    [Theory]
    [InlineData("{\"code\":\"invalid_entry\"}")]
    [InlineData("{\"code\":\"unknown\",\"entryId\":\"log_123\"}")]
    [InlineData("{\"code\":\"invalid_entry\",\"entryId\":\"<script>\"}")]
    [InlineData("{\"code\":\"invalid_entry\",\"entryId\":7}")]
    [InlineData("{\"code\":\"invalid_entry\",\"code\":\"invalid_entry\",\"entryId\":\"log_123\"}")]
    [InlineData("[]")]
    [InlineData("invalid JSON")]
    public async Task MalformedOrUnknownErrorsCannotQuarantineEntries(string json)
    {
        var client = Client(new Handler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent(json) }));
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PushAsync("id", new(null, null, null, []), Ct));
        Assert.Equal("http_400", error.Code);
        Assert.Null(error.EntryId);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "board_not_found", "board_not_found", "no longer has access")]
    [InlineData(HttpStatusCode.Conflict, "write_conflict", "write_conflict", "retry")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request", "invalid_request", "invalid")]
    [InlineData(HttpStatusCode.NotFound, "write_conflict", "http_404", "HTTP 404")]
    [InlineData(HttpStatusCode.NotFound, "invalid_entry", "http_404", "HTTP 404")]
    [InlineData(HttpStatusCode.NotFound, "made_up_code", "http_404", "HTTP 404")]
    [InlineData(HttpStatusCode.NotFound, null, "http_404", "HTTP 404")]
    [InlineData(HttpStatusCode.InternalServerError, "board_not_found", "http_500", "HTTP 500")]
    public async Task RecognisedErrorCodesGiveRecoveryGuidanceWithoutRemoteProse(HttpStatusCode status, string? code, string expectedCode, string expectedFragment)
    {
        // A recognised code only counts on the status the server documents for it; the message is
        // always the desktop's own wording, never the remote `error` text.
        var body = code is null ? "SECRET plain text, not JSON" : $$"""{"error":"SECRET on remote server","code":"{{code}}"}""";
        var client = Client(new Handler(_ => new(status) { Content = new StringContent(body) }));
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PushAsync("id", new(null, null, null, []), Ct));
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal((int)status, error.Status);
        Assert.Null(error.EntryId);
        Assert.Contains(expectedFragment, error.Message);
        Assert.DoesNotContain("SECRET", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ErrorMetadataIsBoundedWithOrWithoutContentLength(bool knownLength)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"code\":\"invalid_entry\",\"entryId\":\"log_123\",\"error\":\""
            + new string('x', BoardSyncHttpClient.MaxErrorResponseBytes) + "\"}");
        var client = Client(new Handler(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(knownLength ? new MemoryStream(bytes) : new NonSeekableStream(bytes))
                { Headers = { ContentLength = knownLength ? bytes.Length : null } }
        }));
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PushAsync("id", new(null, null, null, []), Ct));
        Assert.Equal("http_400", error.Code);
        Assert.Null(error.EntryId);
    }

    [Fact]
    public async Task ResponseLengthIsBoundedBeforeReadingTheBody()
    {
        var client = Client(new Handler(_ =>
        {
            var content = new StringContent("{}");
            content.Headers.ContentLength = BoardSyncHttpClient.MaxResponseBytes + 1L;
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PullAsync("id", 0, 200, Ct));
        Assert.Equal("response_too_large", error.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContentPartsRequireAnIdentityMatchingVerifiedAcknowledgement(bool verified)
    {
        var bytes = new byte[] { 1, 2, 3 };
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var part = new BoardSyncContentPartWire(sha, bytes.Length);
        var client = Client(new Handler(request =>
        {
            Assert.Equal("https://example.com/api/v1/board-content/parts/" + sha, request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { sha256 = sha, bytes = bytes.Length, checksumsVerified = verified })) };
        }));
        if (verified) await client.PutContentPartAsync(part, bytes, Ct, client.DestinationKey);
        else await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PutContentPartAsync(part, bytes, Ct, client.DestinationKey));
    }

    [Fact]
    public async Task ReadOnlyBrowsingDoesNotRequireUploadPermission()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var safety = new RemoteSafetyClient(() => throw new InvalidOperationException("Read must not gate on upload permission"),
            () => new Uri("https://example.com"), () => "test-key");
        var client = new BoardSyncHttpClient(new Factory(handler), () => new Uri("https://example.com/api/v1/boards"), () => "test-key", safety);
        Assert.Empty(await client.DiscoverAsync(Ct));
    }

    // ------------------------------------------------------------------ Pro plan denials

    [Theory]
    [InlineData("account", BoardSyncProPause.AccountMessage, true)]
    [InlineData("board_owner", BoardSyncProPause.BoardOwnerMessage, false)]
    public async Task ProRequiredIsATypedDenial_NotARejectedKey_AndOnlyTheAccountPayerIsNoted(string payer, string message, bool noted)
    {
        var entitlements = new FakeAccountEntitlements();
        var client = Client(new Handler(_ => Denied(
            $$"""{"error":"SECRET upgrade prose","code":"pro_required","feature":"remote_boards","payer":"{{payer}}","upgradePath":"/Billing?feature=remote-boards"}""")),
            entitlements);

        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PushAsync("id", new(null, null, null, []), Ct));

        Assert.Equal("pro_required", error.Code);
        Assert.Equal(403, error.Status);
        Assert.Equal("remote_boards", error.Feature);
        Assert.Equal(payer, error.Payer);
        Assert.Equal(message, error.Message); // the desktop's own words
        Assert.DoesNotContain("API key", error.Message);
        Assert.DoesNotContain("SECRET", error.Message);
        Assert.DoesNotContain("/Billing", error.Message);
        // An invited member of a Board whose owner is not Pro is never treated as a Free account.
        Assert.Equal(noted ? new[] { "remote_boards" } : [], entitlements.Noted);
    }

    [Theory]
    // The header alone is enough: the body may be unreadable, oversized or absent.
    [InlineData(true, "<html>not json</html>", "remote_boards", "account")]
    [InlineData(true, "", "remote_boards", "account")]
    // The body code alone is enough too.
    [InlineData(false, """{"code":"pro_required","feature":"remote_boards","payer":"board_owner"}""", "remote_boards", "board_owner")]
    // A feature or payer this version does not know falls back to this client's own feature and the account.
    [InlineData(true, """{"code":"pro_required","feature":"something_new","payer":"somebody"}""", "remote_boards", "account")]
    [InlineData(true, """{"code":"pro_required","feature":7,"payer":null}""", "remote_boards", "account")]
    // Another known feature named by the server is kept as named.
    [InlineData(true, """{"code":"pro_required","feature":"card_sharing","payer":"account"}""", "card_sharing", "account")]
    public async Task ProRequiredIsRecognisedFromTheHeaderOrTheBodyCode(bool header, string body, string feature, string payer)
    {
        var client = Client(new Handler(_ => Denied(body, header)), new FakeAccountEntitlements());

        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PullAsync("id", 0, 200, Ct));

        Assert.Equal(("pro_required", feature, payer), (error.Code, error.Feature, error.Payer));
    }

    [Theory]
    // Any other 403 is still the account-key problem it was before.
    [InlineData(403, false, """{"code":"capability_required"}""", "unauthorized")]
    [InlineData(403, false, "forbidden", "unauthorized")]
    // The denial is a 403: the code on another status changes nothing.
    [InlineData(401, true, """{"code":"pro_required","feature":"remote_boards","payer":"account"}""", "unauthorized")]
    [InlineData(500, true, """{"code":"pro_required","feature":"remote_boards","payer":"account"}""", "http_500")]
    public async Task OtherRefusalsKeepTheirExistingMapping_AndNoteNothing(int status, bool header, string body, string expectedCode)
    {
        var entitlements = new FakeAccountEntitlements();
        var client = Client(new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
            if (header) response.Headers.Add("X-VibeRails-Denial", "pro_required");
            return response;
        }), entitlements);

        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PushAsync("id", new(null, null, null, []), Ct));

        Assert.Equal(expectedCode, error.Code);
        Assert.Null(error.Feature);
        Assert.Empty(entitlements.Noted);
    }

    [Fact]
    public async Task TheAccountSafetyPauseKeepsItsPrecedenceOverAPlanDenial()
    {
        var entitlements = new FakeAccountEntitlements();
        var client = Client(new Handler(_ => Denied("""{"code":"board_remote_sync_paused","feature":"remote_boards","payer":"account"}""")), entitlements);

        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => client.PushAsync("id", new(null, null, null, []), Ct));

        Assert.Equal("board_remote_sync_paused", error.Code);
        Assert.Empty(entitlements.Noted);
    }

    [Fact]
    public async Task EveryPaidCallReportsTheDenial_IncludingLaunchPollsAndContentParts()
    {
        var entitlements = new FakeAccountEntitlements();
        var client = Client(new Handler(_ => Denied("""{"code":"pro_required","feature":"remote_boards","payer":"account"}""")), entitlements);
        var bytes = new byte[] { 1, 2, 3 };
        var part = new BoardSyncContentPartWire(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length);

        foreach (var call in new Func<Task>[]
        {
            () => client.PublishAsync(new("board", "Board", "VB", []), Ct),
            () => client.PollLaunchAsync(new(Guid.NewGuid(), "Desktop", [Guid.NewGuid()]), Ct, client.DestinationKey!),
            () => client.PutActivityAsync("remote", "card_1", new(1, [], [], [], [], []), Ct, client.DestinationKey),
            () => client.PutContentPartAsync(part, bytes, Ct, client.DestinationKey),
            () => client.SaveInviteAsync("remote", null, new("a@example.com"), Ct, client.DestinationKey)
        })
            Assert.Equal("pro_required", (await Assert.ThrowsAsync<BoardSyncClientException>(call)).Code);
        Assert.Equal(5, entitlements.Noted.Count);

        // A content part refused for any other reason keeps its own code.
        var plain = Client(new Handler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("""{"code":"capability_required"}""") }));
        Assert.Equal("content_upload_failed", (await Assert.ThrowsAsync<BoardSyncClientException>(() => plain.PutContentPartAsync(part, bytes, Ct, plain.DestinationKey))).Code);
    }

    private static HttpResponseMessage Denied(string body, bool header = true)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (header) response.Headers.Add("X-VibeRails-Denial", "pro_required");
        return response;
    }

    private static BoardSyncHttpClient Client(Handler handler, FakeAccountEntitlements entitlements) =>
        new(new Factory(handler), () => new Uri("https://example.com/api/v1/boards"), () => "test-key", null, entitlements);
    private static BoardSyncHttpClient Client(Handler handler) => new(new Factory(handler), new Uri("https://example.com/api/v1/boards"), () => "test-key");
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
