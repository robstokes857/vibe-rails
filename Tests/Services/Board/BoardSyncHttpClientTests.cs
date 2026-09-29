using System.Net;
using System.Text;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardSyncHttpClientTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

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
    [InlineData(HttpStatusCode.NotFound, "board_not_found", "board_not_found", "off and on")]
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
