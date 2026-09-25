using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Services.Board;
using VibeRails.Services.Jira;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Jira;

public sealed class JiraPullServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-jira-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly string _project;
    private readonly BoardStore _store;
    private readonly BoardService _board;
    private readonly MemoryJiraSecrets _secrets = new();
    private readonly ScriptedJira _jira = new();

    public JiraPullServiceTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project");
        _connectionString = $"Data Source={Path.Combine(_root, "board.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(_connectionString, _connectionString);
        _board = new BoardService(_store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaveNeverReturnsTheToken_AndABlankTokenKeepsTheSavedOne()
    {
        var service = Service();
        var board = await _store.CreateBoardAsync(_project, "Main", Ct);
        var saved = await service.SaveAsync(_project, board.Id, Save("token-one"), "token-one", Ct);

        Assert.True(saved.HasToken);
        Assert.Equal("token-one", _secrets.ReadToken(saved.Id));
        Assert.DoesNotContain("token-one", saved.ToString());

        var again = await service.SaveAsync(_project, board.Id, Save(null), "   ", Ct);
        Assert.Equal(saved.Id, again.Id);
        Assert.Equal("token-one", _secrets.ReadToken(saved.Id));
    }

    [Fact]
    public async Task DryRunCountsWithoutWriting_AndARealPullCreatesThenSkips()
    {
        var service = Service();
        var board = await BoardWithLanes();
        await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);
        _jira.Pages.Enqueue(Page(
            Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Fix login", "Bug", "Highest", "Backlog"),
            Issue("101", "PROJ-2", "2026-09-01T00:00:00.000Z", "Tell the story", "Story", "Low", "Review")));

        var dry = await service.PullAsync(_project, board.Id, dryRun: true, Ct);
        Assert.Equal(2, dry.Created);
        Assert.Empty(await _store.GetCardsAsync(_project, Ct, board.Id));

        _jira.Pages.Enqueue(Page(
            Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Fix login", "Bug", "Highest", "Backlog"),
            Issue("101", "PROJ-2", "2026-09-01T00:00:00.000Z", "Tell the story", "Story", "Low", "Review")));
        var wrote = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal("ok", wrote.Outcome);
        Assert.Equal(2, wrote.Created);

        var cards = await _store.GetCardsAsync(_project, Ct, board.Id);
        var bug = Assert.Single(cards, card => card.Title == "Fix login");
        Assert.Equal(BoardCardTypes.Bug, bug.Type);
        Assert.Equal("critical", bug.Priority);
        Assert.Null(bug.Assignee);
        var story = Assert.Single(cards, card => card.Title == "Tell the story");
        Assert.Equal(BoardCardTypes.Feature, story.Type);
        Assert.Equal("low", story.Priority);

        _jira.Pages.Enqueue(Page(
            Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Fix login", "Bug", "Highest", "Backlog")));
        var second = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public async Task NewerJiraUpdatePatchesMappedFields_MovesWithAutomationsSkipped_AndComments()
    {
        var service = Service();
        var board = await BoardWithLanes();
        await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);
        var review = (await _store.GetColumnsAsync(_project, Ct, board.Id)).Single(column => column.Name == "Review");

        _jira.Pages.Enqueue(Page(Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Fix login", "Bug", "High", "Backlog")));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);

        _jira.Pages.Enqueue(Page(Issue("100", "PROJ-1", "2026-09-02T00:00:00.000Z", "Fix login again", "Bug", "Low", "Review")));
        var report = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal(1, report.Updated);

        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id));
        Assert.Equal("Fix login again", card.Title);
        Assert.Equal("low", card.Priority);
        Assert.Equal(review.Id, card.ColumnId);
        var detail = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Contains(detail.Comments, comment => comment.Body.Contains("changed status in Jira", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownStatusParksInOneOverflowLaneAndCommentsOnce()
    {
        var service = Service();
        var board = await BoardWithLanes();
        await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);
        var issue = Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Waiting", "Task", "Medium", "In QA");
        _jira.Pages.Enqueue(Page(issue));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);

        var lanes = await _store.GetColumnsAsync(_project, Ct, board.Id);
        var overflow = Assert.Single(lanes, lane => lane.Name == "Jira");
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id));
        Assert.Equal(overflow.Id, card.ColumnId);
        var first = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Single(first.Comments);

        _jira.Pages.Enqueue(Page(issue with { Updated = DateTime.Parse("2026-09-03T00:00:00Z").ToUniversalTime() }));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        var second = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Single(second.Comments);
        Assert.Single((await _store.GetColumnsAsync(_project, Ct, board.Id)).Where(lane => lane.Name == "Jira"));
    }

    [Fact]
    public async Task ABadIssueIsCountedAndThePageContinues()
    {
        var service = Service();
        var board = await BoardWithLanes();
        await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);
        _jira.Pages.Enqueue(Page(
            Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "   ", "Task", "Medium", "Backlog"),
            Issue("101", "PROJ-2", "2026-09-01T00:00:00.000Z", "Kept", "Task", "Medium", "Backlog")));

        var report = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.Created);
        Assert.Equal("Kept", Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id)).Title);
    }

    [Fact]
    public async Task BadJqlDisablesTheFilterOnceAndUnauthorizedMarksTheConnectionExpired()
    {
        var service = Service();
        var board = await BoardWithLanes();
        var saved = await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);

        _jira.Outcome = JiraCallOutcome.BadJql;
        _jira.Detail = "Bounded query required.";
        var bad = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal("bad-jql", bad.Outcome);
        var disabled = (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!;
        Assert.False(disabled.Enabled);
        Assert.Equal("Bounded query required.", disabled.DisabledReason);

        await service.SaveAsync(_project, board.Id, Save("secret-token"), null, Ct);
        _jira.Outcome = JiraCallOutcome.Unauthorized;
        var expired = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal("expired", expired.Outcome);
        Assert.Equal(BoardJiraAuthStatus.Expired, (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!.AuthStatus);
        Assert.Equal(saved.Id, disabled.Id);
    }

    [Fact]
    public async Task LinkMatchesTheIssueIdAndKeepsTheLatestKey()
    {
        var service = Service();
        var board = await BoardWithLanes();
        await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);
        _jira.Pages.Enqueue(Page(Issue("100", "OLD-1", "2026-09-01T00:00:00.000Z", "Moved", "Task", "Medium", "Backlog")));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        _jira.Pages.Enqueue(Page(Issue("100", "NEW-9", "2026-09-02T00:00:00.000Z", "Moved", "Task", "Medium", "Backlog")));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);

        Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id));
        var connection = (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!;
        Assert.Equal("NEW-9", (await _store.FindJiraLinkAsync(connection.Id, "100", Ct))!.IssueKey);
    }

    [Fact]
    public async Task ChangingTheSiteStartsANewLinkNamespace_SoACollidingIssueIdNeverOverwritesAnOldCard()
    {
        var service = Service();
        var board = await BoardWithLanes();
        var first = await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);
        _jira.Pages.Enqueue(Page(Issue("100", "OLD-1", "2026-09-01T00:00:00.000Z", "From the old site", "Task", "Medium", "Backlog")));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);

        // The saved token is never sent to a different site.
        var other = Save(null) with { SiteUrl = "https://other.atlassian.net" };
        await Assert.ThrowsAsync<JiraConfigException>(() => service.SaveAsync(_project, board.Id, other, null, Ct));
        Assert.Equal(first.Id, (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!.Id);

        var moved = await service.SaveAsync(_project, board.Id, other, "secret-token", Ct);
        Assert.NotEqual(first.Id, moved.Id);
        Assert.Null(_secrets.ReadToken(first.Id));
        Assert.Contains("Site changed", moved.LastReport);

        // Jira issue ids are tenant-local: id 100 on the new site is a different issue.
        _jira.Pages.Enqueue(Page(Issue("100", "NEW-1", "2026-09-05T00:00:00.000Z", "From the new site", "Task", "Medium", "Backlog")));
        var report = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal(1, report.Created);
        var titles = (await _store.GetCardsAsync(_project, Ct, board.Id)).Select(card => card.Title).Order().ToList();
        Assert.Equal(["From the new site", "From the old site"], titles);
    }

    [Fact]
    public async Task CardAndLinkAreCreatedTogether_AndASecondCreateForTheSameIssueCreatesNothing()
    {
        var board = await BoardWithLanes();
        var lane = (await _store.GetColumnsAsync(_project, Ct, board.Id))[0];
        var card = new NewBoardCard(lane.Id, "One", "", null, "medium", null, [], false, null, "task", board.Id, false);
        var link = new BoardJiraLinkRecord("", "jira_site", "100", "PROJ-1", null, DateTime.UtcNow, DateTime.UtcNow);

        var created = await _store.CreateJiraCardAsync(_project, card, link, Ct);
        Assert.NotNull(created);
        Assert.Equal(created.Id, (await _store.FindJiraLinkAsync("jira_site", "100", Ct))!.CardId);

        // What a concurrent pull that lost the race sees: no second card.
        Assert.Null(await _store.CreateJiraCardAsync(_project, card with { Title = "Two" }, link, Ct));
        Assert.Equal("One", Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id)).Title);
    }

    [Fact]
    public async Task AnExistingCardMovesToTheOverflowLaneWhenJiraChangesItToAnUnknownStatus()
    {
        var service = Service();
        var board = await BoardWithLanes();
        await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);
        _jira.Pages.Enqueue(Page(
            Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Parked", "Task", "Medium", "In QA"),
            Issue("101", "PROJ-2", "2026-09-01T00:00:00.000Z", "Known", "Task", "Medium", "Backlog")));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        var overflow = Assert.Single(await _store.GetColumnsAsync(_project, Ct, board.Id), lane => lane.Name == "Jira");

        // The overflow lane already exists, so the lane match is Overflow (not Unresolved).
        _jira.Pages.Enqueue(Page(Issue("101", "PROJ-2", "2026-09-02T00:00:00.000Z", "Known", "Task", "Medium", "Blocked upstream")));
        var report = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal(1, report.Updated);

        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id), card => card.Title == "Known");
        Assert.Equal(overflow.Id, card.ColumnId);
        var detail = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Contains(detail.Comments, comment => comment.Body.Contains("\"Blocked upstream\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveRefusesAMissingBoard_AndDeletingABoardDropsItsConnectionAndToken()
    {
        var service = Service();
        await BoardWithLanes();
        await Assert.ThrowsAsync<JiraConfigException>(() => service.SaveAsync(_project, "brd_missing", Save(null), "secret-token", Ct));
        Assert.Empty(await _store.GetJiraConnectionsAsync(Ct));
        Assert.Equal(0, _secrets.Count);

        var sprint = await _store.CreateBoardAsync(_project, "Sprint", Ct);
        var saved = await service.SaveAsync(_project, sprint.Id, Save(null), "secret-token", Ct);
        Assert.NotNull(await _store.DeleteBoardAsync(_project, sprint.Id, Ct));
        Assert.Null(await _store.GetJiraConnectionAsync(_project, sprint.Id, Ct));

        await service.PullDueAsync(Ct);
        Assert.Null(_secrets.ReadToken(saved.Id));
        Assert.Equal(0, _jira.Searches);
    }

    [Fact]
    public async Task APullReportsBusy_AndTheSchedulerSkips_WhileAnotherProcessHoldsThePullLock()
    {
        var service = Service();
        var board = await BoardWithLanes();
        await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);

        using (var held = CrossProcessFileLock.TryAcquire(LockPath))
        {
            Assert.NotNull(held);
            var busy = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
            Assert.Equal("busy", busy.Outcome);
            await service.PullDueAsync(Ct);
        }

        Assert.Equal(0, _jira.Searches);
        Assert.Null((await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!.LastPullUtc);
        _jira.Pages.Enqueue(Page(Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "After release", "Task", "Medium", "Backlog")));
        Assert.Equal(1, (await service.PullAsync(_project, board.Id, dryRun: false, Ct)).Created);
    }

    [Fact]
    public void SiteUrlRejectsAnythingButAnHttpsOrigin()
    {
        Assert.Equal("https://acme.atlassian.net", JiraSite.Parse("https://acme.atlassian.net").Origin);
        Assert.Equal("https://acme.atlassian.net", JiraSite.Parse("https://acme.atlassian.net/").Origin);
        Assert.Throws<JiraConfigException>(() => JiraSite.Parse("http://acme.atlassian.net"));
        Assert.Throws<JiraConfigException>(() => JiraSite.Parse("https://user:token@acme.atlassian.net"));
        Assert.Throws<JiraConfigException>(() => JiraSite.Parse("https://acme.atlassian.net/jira"));
    }

    [Fact]
    public void SearchReadsTheNewEndpointAndStopsWhenThePageTokenIsAbsent()
    {
        const string body = """
            {"issues":[{"id":"100","key":"PROJ-1","fields":{
              "summary":"Fix login","description":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Hello"}]}]},
              "status":{"name":"Backlog"},"issuetype":{"name":"Bug"},"priority":{"name":"High"},
              "labels":["auth"],"assignee":{"displayName":"Ada"},"updated":"2026-09-01T00:00:00.000+0000",
              "customfield_10016":5}}],"nextPageToken":null}
            """;
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var issueElement = document.RootElement.GetProperty("issues")[0];
        Assert.True(JiraCloudClient.TryReadIssue(issueElement, "customfield_10016", out var issue));
        Assert.Equal("100", issue.Id);
        Assert.Equal("Hello", issue.DescriptionText);
        Assert.Equal(5, issue.StoryPoints);
        Assert.Equal("Ada", issue.AssigneeDisplay);
        Assert.Null(document.RootElement.GetProperty("nextPageToken").GetString());
    }

    private string LockPath => Path.Combine(_root, JiraPullLock.FileName);

    private JiraPullService Service() => new(_store, _board, _jira, _secrets, new JiraPullLock(LockPath));

    private async Task<BoardRecord> BoardWithLanes()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        return (await _store.GetBoardsAsync(_project, Ct))[0];
    }

    private static BoardJiraConnectionSave Save(string? token) =>
        new("https://acme.atlassian.net", "ada@example.com", null,
            "project = PROJ AND updated >= -30d ORDER BY updated DESC", true);

    private static JiraSearchPage Page(params JiraIssue[] issues) => new(issues, null);

    private static JiraIssue Issue(string id, string key, string updated, string summary, string type, string priority, string status) =>
        new(id, key, DateTime.Parse(updated).ToUniversalTime(), summary, "body", status, type, priority, ["one"], "Ada", null);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class MemoryJiraSecrets : IJiraSecretStore
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
        public bool HasToken(string connectionId) => _tokens.ContainsKey(connectionId);
        public string? ReadToken(string connectionId) => _tokens.TryGetValue(connectionId, out var token) ? token : null;
        public void SaveToken(string connectionId, string token) => _tokens[connectionId] = token;
        public void DeleteToken(string connectionId) => _tokens.Remove(connectionId);
        public int Count => _tokens.Count;

        public async Task PruneAsync(Func<CancellationToken, Task<IReadOnlyCollection<string>>> liveConnectionIds, CancellationToken cancellationToken)
        {
            var live = (await liveConnectionIds(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _tokens.Keys.Where(id => !live.Contains(id)).ToList())
                _tokens.Remove(id);
        }
    }

    private sealed class ScriptedJira : IJiraCloudClient
    {
        public Queue<JiraSearchPage> Pages { get; } = new();
        public JiraCallOutcome Outcome { get; set; } = JiraCallOutcome.Ok;
        public string? Detail { get; set; }
        public int Searches { get; private set; }

        public Task<JiraCallResult<JiraSearchPage>> SearchAsync(
            string siteUrl, string email, string apiToken, string jql, string? nextPageToken,
            string? storyPointsFieldId, CancellationToken cancellationToken)
        {
            Searches++;
            Assert.Equal("secret-token", apiToken);
            Assert.DoesNotContain("/rest/api/3/search?", siteUrl);
            if (Outcome != JiraCallOutcome.Ok)
                return Task.FromResult(new JiraCallResult<JiraSearchPage>(Outcome, null, Detail, TimeSpan.FromSeconds(1)));
            return Task.FromResult(new JiraCallResult<JiraSearchPage>(JiraCallOutcome.Ok, Pages.Dequeue(), null, null));
        }

        public Task<JiraCallResult<string>> TestAsync(string siteUrl, string email, string apiToken, CancellationToken cancellationToken) =>
            Task.FromResult(new JiraCallResult<string>(JiraCallOutcome.Ok, "Ada", null, null));
    }
}

public sealed class JiraCloudClientHttpTests
{
    [Fact]
    public async Task SearchCallsTheJqlEndpointAndHonorsRetryAfter()
    {
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "", "2");
        using var http = new HttpClient(handler);
        var client = new JiraCloudClient(http);

        var limited = await client.SearchAsync("https://acme.atlassian.net", "ada@example.com", "secret",
            "project = PROJ", null, null, TestContext.Current.CancellationToken);
        Assert.Equal(JiraCallOutcome.RateLimited, limited.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(2), limited.RetryAfter);
        Assert.Contains("/rest/api/3/search/jql?", handler.LastPath, StringComparison.Ordinal);
        Assert.DoesNotContain("/rest/api/3/search?", handler.LastPath, StringComparison.Ordinal);
        Assert.StartsWith("Basic ", handler.LastAuthorization, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadJqlReportsTheJiraMessage()
    {
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"errorMessages":["Unbounded JQL queries are not allowed."]}""");
        using var http = new HttpClient(handler);
        var client = new JiraCloudClient(http);

        var result = await client.SearchAsync("https://acme.atlassian.net", "ada@example.com", "secret",
            "order by key desc", null, null, TestContext.Current.CancellationToken);
        Assert.Equal(JiraCallOutcome.BadJql, result.Outcome);
        Assert.Equal("Unbounded JQL queries are not allowed.", result.Detail);
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public string? LastPath { get; private set; }
        public string? LastAuthorization { get; private set; }

        public void Enqueue(HttpStatusCode status, string body, string? retryAfter = null)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfter is not null)
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            _responses.Enqueue(response);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.PathAndQuery;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
