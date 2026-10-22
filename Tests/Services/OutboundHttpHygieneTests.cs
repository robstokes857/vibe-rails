using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using VibeRails.Services;
using VibeRails.Services.AzureDevOps;
using VibeRails.Services.Jira;
using Xunit;

namespace Tests.Services;

public sealed class OutboundHttpHygieneTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(200);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, int> OversizedCases => new(
        from endpoint in new[] { "jira-test", "jira-search", "jira-board", "ado-get", "ado-post" }
        from declaredLength in new[] { -1, 1, 4 * 1024 * 1024 + 1 }
        select (endpoint, declaredLength));

    [Theory]
    [MemberData(nameof(OversizedCases))]
    public async Task TrackerBodiesAreBoundedBeforeParsing_EvenWithoutAnHonestLength(string endpoint, int declaredLength)
    {
        var body = new BodyStream();
        using var http = Client(body, declaredLength: declaredLength);
        var result = await CallTracker(endpoint, http, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("Failed", result.Outcome);
        Assert.Contains("4 MiB", result.Detail);
        Assert.Equal(declaredLength > 4 * 1024 * 1024 ? 0 : 4 * 1024 * 1024 + 1, body.BytesRead);
        Assert.True(body.Disposed);
    }

    public static TheoryData<string, int> SlowCases => new(
        from endpoint in new[] { "jira-test", "jira-search", "jira-board", "ado-get", "ado-post" }
        from bytesBeforeStall in new[] { 0, 64 }
        select (endpoint, bytesBeforeStall));

    [Theory]
    [MemberData(nameof(SlowCases))]
    public async Task TrackerDeadlineCoversTheWholeBody(string endpoint, int bytesBeforeStall)
    {
        var body = new BodyStream(bytesBeforeStall);
        using var http = Client(body);
        var watch = Stopwatch.StartNew();
        var result = await CallTracker(endpoint, http, Deadline, Ct);

        Assert.Equal("Failed", result.Outcome);
        Assert.Contains("did not answer within", result.Detail);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.True(body.Started);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData("jira-test")]
    [InlineData("jira-search")]
    [InlineData("jira-board")]
    [InlineData("ado-get")]
    [InlineData("ado-post")]
    public async Task CallerCancellationStillPropagates(string endpoint)
    {
        var body = new BodyStream(0);
        using var http = Client(body);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stop.CancelAfter(Deadline);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CallTracker(endpoint, http, TimeSpan.FromSeconds(5), stop.Token));
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData("jira-test", "Failed")]
    [InlineData("jira-search", "BadJql")]
    [InlineData("jira-board", "BadJql")]
    [InlineData("ado-get", "Failed")]
    [InlineData("ado-post", "BadQuery")]
    public async Task BadRequestsNeverReadOrReflectRemoteErrorProse(string endpoint, string outcome)
    {
        // A body which never completes must not delay returning a fixed 400 response.
        var body = new BodyStream(0);
        using var http = Client(body, HttpStatusCode.BadRequest);
        var result = await CallTracker(endpoint, http, Deadline, Ct);

        Assert.Equal(outcome, result.Outcome);
        Assert.False(body.Started);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(1024 * 1024 + 1)]
    public async Task UpdateResponseIsBounded(int declaredLength)
    {
        var body = new BodyStream();
        using var http = Client(body, declaredLength: declaredLength);

        Assert.Null(await new UpdateService(http).CheckForUpdateAsync(Ct));
        Assert.Equal(declaredLength > 1024 * 1024 ? 0 : 1024 * 1024 + 1, body.BytesRead);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task UpdateDeadlineCoversBodyAndDisposesResponse()
    {
        var body = new BodyStream(64);
        using var http = Client(body);
        var watch = Stopwatch.StartNew();

        Assert.Null(await new UpdateService(http, Deadline).CheckForUpdateAsync(Ct));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.True(body.Started);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public async Task BoundedJsonAllowsAnExactFit(int length, bool fits)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)' ');
        bytes[0] = (byte)'{';
        bytes[1] = (byte)'}';
        using var content = new ByteArrayContent(bytes);
        if (fits)
        {
            using var document = await BoundedHttpContent.ReadJsonAsync(content, 1024, Ct);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => BoundedHttpContent.ReadJsonAsync(content, 1024, Ct));
        }
    }

    private static async Task<(string Outcome, string? Detail)> CallTracker(
        string endpoint, HttpClient http, TimeSpan deadline, CancellationToken ct)
    {
        if (endpoint.StartsWith("jira-", StringComparison.Ordinal))
        {
            var client = new JiraCloudClient(http, deadline);
            const string site = "https://example.atlassian.net";
            if (endpoint == "jira-test")
            {
                var result = await client.TestAsync(site, "person@example.com", "secret", ct);
                return (result.Outcome.ToString(), result.Detail);
            }
            if (endpoint == "jira-search")
            {
                var result = await client.SearchAsync(site, "person@example.com", "secret", "project = TEST", null, null, ct);
                return (result.Outcome.ToString(), result.Detail);
            }
            var board = await client.GetBoardAsync(site, "person@example.com", "secret", "1", ct);
            return (board.Outcome.ToString(), board.Detail);
        }
        var ado = new AzureDevOpsClient(http, deadline);
        if (endpoint == "ado-post")
        {
            var result = await ado.QueryIdsAsync("org", "project", "team", "SELECT [System.Id] FROM WorkItems", "secret", ct);
            return (result.Outcome.ToString(), result.Detail);
        }
        var fields = await ado.GetFieldsAsync("org", "secret", ct);
        return (fields.Outcome.ToString(), fields.Detail);
    }

    private static HttpClient Client(BodyStream body, HttpStatusCode status = HttpStatusCode.OK, int declaredLength = -1) =>
        new(new BodyHandler(body, status, declaredLength)) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class BodyHandler(BodyStream body, HttpStatusCode status, int declaredLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (declaredLength >= 0)
                content.Headers.ContentLength = declaredLength;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    /// <summary>Generates bytes on demand; can stall before or during the body, without a large allocation.</summary>
    private sealed class BodyStream(int? stallAt = null) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started = true;
            if (stallAt is { } limit && BytesRead >= limit)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            var count = stallAt is { } end ? Math.Min(buffer.Length, end - BytesRead) : buffer.Length;
            buffer.Span[..count].Fill((byte)' ');
            BytesRead += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
