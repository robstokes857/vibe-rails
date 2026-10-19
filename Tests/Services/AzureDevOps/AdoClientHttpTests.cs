using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VibeRails.Services.AzureDevOps;
using Xunit;

namespace Tests.Services.AzureDevOps;

public sealed class AdoClientHttpTests
{
    private const string Org = "contoso";
    private const string Project = "Fabrikam Fiber";
    private const string Team = "Core Team";
    private const string QueryId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryCallGoesToDevAzureComWithBasicPatAuthAndNoRedirectToSignIn()
    {
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"p1","name":"Fabrikam Fiber"}""");
        handler.Enqueue(HttpStatusCode.OK, """
            {"id":"b1","name":"Stories","columns":[
              {"name":"New","columnType":"incoming","stateMappings":{"User Story":"New","Bug":"New"}},
              {"name":"Active","columnType":"inProgress","isSplit":true,"stateMappings":{"User Story":"Active","Bug":"Active"}},
              {"name":"Closed","columnType":"outgoing","stateMappings":{"User Story":"Closed","Bug":"Closed"}}]}
            """);
        handler.Enqueue(HttpStatusCode.OK, """
            {"field":{"referenceName":"System.AreaPath"},"defaultValue":"Fabrikam Fiber\\Core",
             "values":[{"value":"Fabrikam Fiber\\Core","includeChildren":true}]}
            """);
        handler.Enqueue(HttpStatusCode.OK, """{"count":2,"value":[{"referenceName":"System.Title"},{"referenceName":"Microsoft.VSTS.Scheduling.StoryPoints"}]}""");
        handler.Enqueue(HttpStatusCode.OK, """{"id":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","name":"My bugs","queryType":"flat","isFolder":false}""");
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http);

        Assert.Equal(new AdoProject("p1", "Fabrikam Fiber"), (await client.TestAsync(Org, Project, "secret-pat", Ct)).Value);
        Assert.Equal("/contoso/_apis/projects/Fabrikam%20Fiber?api-version=7.1", handler.Paths[^1]);

        var board = (await client.GetBoardAsync(Org, Project, Team, "Backlog items", "secret-pat", Ct)).Value!;
        Assert.Equal("/contoso/Fabrikam%20Fiber/Core%20Team/_apis/work/boards/Backlog%20items?api-version=7.1", handler.Paths[^1]);
        Assert.Equal("Stories", board.Name);
        Assert.Equal(["New", "Active", "Closed"], board.Columns.Select(column => column.Name));
        Assert.True(board.Columns[1].IsSplit);
        Assert.True(board.Columns[2].IsOutgoing);
        Assert.Equal("Active", board.Columns[1].StateMappings["bug"]);
        Assert.Equal(["User Story", "Bug"], board.Types);

        var scope = (await client.GetTeamFieldValuesAsync(Org, Project, Team, "secret-pat", Ct)).Value!;
        Assert.Equal("/contoso/Fabrikam%20Fiber/Core%20Team/_apis/work/teamsettings/teamfieldvalues?api-version=7.1", handler.Paths[^1]);
        Assert.Equal("System.AreaPath", scope.Field);
        Assert.Equal(new AdoTeamFieldValue("Fabrikam Fiber\\Core", true), Assert.Single(scope.Values));

        var fields = (await client.GetFieldsAsync(Org, "secret-pat", Ct)).Value!;
        Assert.Equal("/contoso/_apis/wit/fields?api-version=7.1", handler.Paths[^1]);
        Assert.True(fields.Has("microsoft.vsts.scheduling.storypoints"));
        Assert.False(fields.Has("Microsoft.VSTS.Scheduling.Effort"));

        var query = (await client.GetQueryAsync(Org, Project, QueryId.ToUpperInvariant(), "secret-pat", Ct)).Value!;
        Assert.Equal($"/contoso/Fabrikam%20Fiber/_apis/wit/queries/{QueryId}?api-version=7.1", handler.Paths[^1]);
        Assert.Equal("My bugs", query.Name);

        Assert.All(handler.Hosts, host => Assert.Equal("dev.azure.com", host));
        Assert.All(handler.Authorizations, value => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":secret-pat")), value));
        Assert.All(handler.FedAuth, value => Assert.Equal("Suppress", value));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WiqlPostsTheQueryInTheTeamsContext_AndASavedQueryListsTreeTargetsOnce()
    {
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"queryType":"flat","workItems":[{"id":3},{"id":1},{"id":3}]}""");
        handler.Enqueue(HttpStatusCode.OK, """
            {"queryType":"tree","workItemRelations":[
              {"rel":null,"source":null,"target":{"id":10}},
              {"rel":"System.LinkTypes.Hierarchy-Forward","source":{"id":10},"target":{"id":11}},
              {"rel":null,"source":null,"target":{"id":11}}]}
            """);
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http);

        var ids = await client.QueryIdsAsync(Org, Project, Team, "SELECT [System.Id] FROM WorkItems WHERE [System.Title] = 'it''s'", "pat", Ct);
        Assert.Equal([3, 1], ids.Value!.Ids);
        Assert.Equal(HttpMethod.Post, handler.Methods[^1]);
        Assert.Equal("/contoso/Fabrikam%20Fiber/Core%20Team/_apis/wit/wiql?$top=2000&api-version=7.1", handler.Paths[^1]);
        using (var body = JsonDocument.Parse(handler.Bodies[^1]))
            Assert.Equal("SELECT [System.Id] FROM WorkItems WHERE [System.Title] = 'it''s'", body.RootElement.GetProperty("query").GetString());

        var tree = await client.RunSavedQueryAsync(Org, Project, QueryId, "pat", Ct);
        Assert.Equal([10, 11], tree.Value!.Ids);
        Assert.Equal(HttpMethod.Get, handler.Methods[^1]);
        Assert.Equal($"/contoso/Fabrikam%20Fiber/_apis/wit/wiql/{QueryId}?$top=2000&api-version=7.1", handler.Paths[^1]);
    }

    [Fact]
    public async Task WorkItemsBatch_AsksOnlyForTheGivenFields_AndReadsEachField()
    {
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"count":3,"value":[
              {"id":7,"rev":4,"fields":{
                "System.Title":"Fix sign-in","System.WorkItemType":"Bug","System.State":"Active",
                "System.ChangedDate":"2026-09-01T10:20:30.123Z","System.Description":"<div>Broken</div>",
                "System.Tags":"ui; login","System.AssignedTo":{"displayName":"Ada Lovelace","uniqueName":"ada@example.com"},
                "System.BoardColumn":"Code Review","Microsoft.VSTS.Common.Priority":2,
                "Microsoft.VSTS.TCM.ReproSteps":"<p>Open it</p>","Microsoft.VSTS.Scheduling.StoryPoints":3.0}},
              null,
              {"id":8,"rev":1,"fields":{"System.Title":"Older shape","System.WorkItemType":"Task","System.State":"New",
                "System.ChangedDate":"2026-09-02T00:00:00Z","System.AssignedTo":"Grace Hopper <grace@example.com>"}}]}
            """);
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http);
        string[] fields = [.. AdoFields.Always, AdoFields.Priority, AdoFields.ReproSteps, AdoFields.BoardColumn, AdoFields.StoryPoints];

        var items = (await client.GetWorkItemsAsync(Org, Project, [7, 99, 8], fields, "pat", Ct)).Value!;

        Assert.Equal("/contoso/Fabrikam%20Fiber/_apis/wit/workitemsbatch?api-version=7.1", handler.Paths[^1]);
        using (var body = JsonDocument.Parse(handler.Bodies[^1]))
        {
            Assert.Equal([7, 99, 8], body.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()));
            Assert.Equal(fields, body.RootElement.GetProperty("fields").EnumerateArray().Select(field => field.GetString()));
            Assert.Equal("omit", body.RootElement.GetProperty("errorPolicy").GetString());
        }
        Assert.Equal(2, items.Count);
        var bug = items[0];
        Assert.Equal((7, 4), (bug.Id, bug.Rev));
        Assert.Equal(new DateTime(2026, 9, 1, 10, 20, 30, 123, DateTimeKind.Utc), bug.ChangedDate);
        Assert.Equal(DateTimeKind.Utc, bug.ChangedDate.Kind);
        Assert.Equal(("Fix sign-in", "Bug", "Active"), (bug.Title, bug.WorkItemType, bug.State));
        Assert.Equal("<div>Broken</div>", bug.DescriptionHtml);
        Assert.Equal("<p>Open it</p>", bug.ReproStepsHtml);
        Assert.Equal(2, bug.Priority);
        Assert.Equal("ui; login", bug.Tags);
        Assert.Equal("Ada Lovelace", bug.AssigneeDisplay);
        Assert.Equal("Code Review", bug.BoardColumn);
        Assert.Equal(3.0, bug.Estimates[AdoFields.StoryPoints]);
        Assert.Equal("Grace Hopper", items[1].AssigneeDisplay);
        Assert.Empty(items[1].Estimates);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetWorkItemsAsync(Org, Project, Enumerable.Range(1, AzureDevOpsClient.BatchSize + 1).ToList(), fields, "pat", Ct));
        await Assert.ThrowsAsync<AdoConfigException>(() =>
            client.GetWorkItemsAsync(Org, Project, [1], ["System.Title\"],\"x"], "pat", Ct));
    }

    /// <summary>
    /// A bad or expired token is not always a 401: Azure DevOps can answer 203 or 302 with its
    /// HTML sign-in page. Each of those is Unauthorized, never a parse failure or a followed redirect.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "application/json")]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, "text/html")]
    [InlineData(HttpStatusCode.Redirect, "text/html")]
    [InlineData(HttpStatusCode.OK, "text/html")]
    [InlineData(HttpStatusCode.OK, null)]
    public async Task ASignInPageInsteadOfJson_IsUnauthorized(HttpStatusCode status, string? mediaType)
    {
        var handler = new QueueHandler();
        handler.Enqueue(status, "<html><body>Sign in to your account</body></html>", mediaType: mediaType);
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http);

        var result = await client.TestAsync(Org, Project, "expired-pat", Ct);
        Assert.Equal(AdoCallOutcome.Unauthorized, result.Outcome);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task RefusalsAreTyped_RateLimitsReadRetryAfter_AndOnlyTheWiqlCallsCallA400ABadQuery()
    {
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, "{}");
        handler.Enqueue(HttpStatusCode.NotFound, "{}");
        handler.Enqueue(HttpStatusCode.TooManyRequests, "{}", retryAfter: "7");
        handler.Enqueue(HttpStatusCode.BadRequest, """{"message":"TF51005: The query references a field that does not exist."}""");
        handler.Enqueue(HttpStatusCode.BadRequest, """{"message":"TF51535: Cannot find field Custom.Gone."}""");
        handler.Enqueue(HttpStatusCode.MovedPermanently, "");
        handler.Enqueue(HttpStatusCode.InternalServerError, "");
        handler.Enqueue(HttpStatusCode.OK, "{not json");
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http);

        Assert.Equal(AdoCallOutcome.Forbidden, (await client.GetBoardAsync(Org, Project, Team, "Stories", "pat", Ct)).Outcome);
        Assert.Equal(AdoCallOutcome.NotFound, (await client.GetBoardAsync(Org, Project, Team, "Stories", "pat", Ct)).Outcome);
        var limited = await client.QueryIdsAsync(Org, Project, Team, "SELECT [System.Id] FROM WorkItems", "pat", Ct);
        Assert.Equal(AdoCallOutcome.RateLimited, limited.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(7), limited.RetryAfter);
        var bad = await client.QueryIdsAsync(Org, Project, Team, "SELECT nonsense", "pat", Ct);
        Assert.Equal(AdoCallOutcome.BadQuery, bad.Outcome);
        Assert.Equal("TF51005: The query references a field that does not exist.", bad.Detail);
        var batch = await client.GetWorkItemsAsync(Org, Project, [1], ["Custom.Gone"], "pat", Ct);
        Assert.Equal(AdoCallOutcome.Failed, batch.Outcome);
        Assert.Equal("TF51535: Cannot find field Custom.Gone.", batch.Detail);
        Assert.Equal(AdoCallOutcome.Failed, (await client.GetFieldsAsync(Org, "pat", Ct)).Outcome);
        Assert.Equal(AdoCallOutcome.Failed, (await client.GetFieldsAsync(Org, "pat", Ct)).Outcome);
        Assert.Equal(AdoCallOutcome.Failed, (await client.GetFieldsAsync(Org, "pat", Ct)).Outcome);
        Assert.Equal(8, handler.Paths.Count);
    }

    /// <summary>
    /// Review R1: HttpClient.Timeout stops at the headers when the body is streamed, so a body that
    /// stalls after them must still end the call, as a failure, or it holds the pull lock forever.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ABodyThatStallsAfterItsHeaders_FailsAtTheDeadline(HttpStatusCode status)
    {
        var handler = new StallingHandler(status);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new AzureDevOpsClient(http, TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await client.QueryIdsAsync(Org, Project, Team, "SELECT [System.Id] FROM WorkItems", "pat", Ct);

        Assert.Equal(AdoCallOutcome.Failed, result.Outcome);
        Assert.Contains("did not answer within", result.Detail, StringComparison.Ordinal);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Assert.True(handler.BodyStarted);
    }

    [Fact]
    public async Task TheCallersCancellationIsNotAFailure()
    {
        using var http = new HttpClient(new StallingHandler(HttpStatusCode.OK)) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new AzureDevOpsClient(http, TimeSpan.FromMinutes(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stop.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetFieldsAsync(Org, "pat", stop.Token));
    }

    [Fact]
    public async Task AnUnreachableHostIsAFailedCall()
    {
        using var http = new HttpClient(new ThrowingHandler());
        var result = await new AzureDevOpsClient(http).TestAsync(Org, Project, "pat", Ct);
        Assert.Equal(AdoCallOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task InvalidSegmentsNeverReachTheNetwork()
    {
        var handler = new QueueHandler();
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http);

        await Assert.ThrowsAsync<AdoConfigException>(() => client.TestAsync("evil.example/x", Project, "pat", Ct));
        await Assert.ThrowsAsync<AdoConfigException>(() => client.TestAsync(Org, "..", "pat", Ct));
        await Assert.ThrowsAsync<AdoConfigException>(() => client.GetTeamFieldValuesAsync(Org, Project, "a/b", "pat", Ct));
        await Assert.ThrowsAsync<AdoConfigException>(() => client.GetQueryAsync(Org, Project, "../../_apis/projects", "pat", Ct));
        Assert.Empty(handler.Paths);
    }

    /// <summary>Answers at once with JSON headers, then never sends the body.</summary>
    private sealed class StallingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public bool BodyStarted => _body.Started;
        private readonly StallingStream _body = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(_body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class StallingStream : Stream
    {
        public bool Started { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started = true;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known.");
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<string> Paths { get; } = [];
        public List<string> Hosts { get; } = [];
        public List<string> Authorizations { get; } = [];
        public List<string> FedAuth { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public List<string> Bodies { get; } = [];

        public void Enqueue(HttpStatusCode status, string body, string? retryAfter = null, string? mediaType = "application/json")
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = mediaType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
            var response = new HttpResponseMessage(status) { Content = content };
            if (retryAfter is not null)
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            _responses.Enqueue(response);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Hosts.Add(request.RequestUri.Host);
            Methods.Add(request.Method);
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            FedAuth.Add(request.Headers.TryGetValues("X-TFS-FedAuthRedirect", out var values) ? string.Join(",", values) : string.Empty);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }
}
