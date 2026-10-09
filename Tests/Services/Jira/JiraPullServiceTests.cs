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
    public async Task UnchangedIssueRepairsMissingDescriptionWithoutResettingLocalFields()
    {
        var service = Service();
        var board = await BoardWithLanes();
        var connection = await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct);
        var issue = Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Jira title", "Story", "Medium", "Backlog");
        _jira.Pages.Enqueue(Page(issue with { DescriptionText = "" }));
        await service.PullAsync(_project, connection.BoardId, false, Ct);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, connection.BoardId));
        await _board.UpdateCardAsync(_project, card.Id, new VibeRails.DTOs.UpdateBoardCardRequest(Title: "Local title"), Ct);
        var lane = (await _store.GetColumnsAsync(_project, Ct, connection.BoardId)).First(l => l.Id != card.ColumnId);
        await _store.MoveCardAsync(_project, card.Id, lane.Id, null, Ct);
        _jira.Pages.Enqueue(Page(issue));
        Assert.Equal(1, (await service.PullAsync(_project, connection.BoardId, false, Ct)).Updated);
        var fixedCard = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Card;
        Assert.Equal("body", fixedCard.Description);
        Assert.Equal("Local title", fixedCard.Title);
        Assert.Equal(lane.Id, fixedCard.ColumnId);
    }

    [Fact]
    public async Task ExistingConnectionSeparatesAtomically_PreservingCardsAndLaneChoices()
    {
        var source = await BoardWithLanes();
        var lane = (await _store.GetColumnsAsync(_project, Ct, source.Id))[0];
        var native = await _store.CreateCardAsync(_project, new NewBoardCard(lane.Id, "Local work", "", null,
            "medium", null, [], false, null, BoardId: source.Id), Ct);
        var imported = (await _store.CreateJiraCardAsync(_project, new NewBoardCard(lane.Id, "Jira work", "", null,
            "medium", null, [], false, null, BoardId: source.Id),
            new BoardJiraLinkRecord("", "jira_old", "100", "PROJ-1", null, DateTime.UtcNow, DateTime.UtcNow), Ct))!;
        await _store.AddCommentAsync(_project, imported.Id, BoardAuthor.User(), "Keep this discussion", Ct);
        await _store.SaveJiraConnectionAsync(new BoardJiraConnectionRecord("jira_old", _project, source.Id,
            "https://acme.atlassian.net", "ada@example.com", true, "saved", null, "project = PROJ", true,
            null, lane.Id, null, null, null,
            ColumnMap: JiraColumnMap.Serialize([new JiraColumnChoice("To Do", lane.Id)])), Ct);

        var destination = await _store.EnsureDedicatedJiraBoardAsync(_project, "jira_old", Ct);
        Assert.NotEqual(source.Id, destination.BoardId);
        Assert.True(destination.DedicatedBoard);
        Assert.Null(await _store.GetJiraConnectionAsync(_project, source.Id, Ct));
        Assert.Equal(native.Id, Assert.Single(await _store.GetCardsAsync(_project, Ct, source.Id)).Id);
        var moved = Assert.Single(await _store.GetCardsAsync(_project, Ct, destination.BoardId));
        Assert.Equal(imported.Id, moved.Id);
        Assert.Equal(imported.Key, moved.Key);
        Assert.Equal(imported.DisplayId, moved.DisplayId);
        Assert.Equal("PROJ-1", moved.JiraIssueKey);
        Assert.Equal(moved.ColumnId, destination.OverflowColumnId);
        Assert.Equal(moved.ColumnId, Assert.Single(JiraColumnMap.Parse(destination.ColumnMap)).LaneId);
        Assert.Contains((await _store.GetCardDetailAsync(_project, moved.Id, Ct))!.Comments,
            comment => comment.Body == "Keep this discussion");
        Assert.Equal(destination.BoardId, (await _store.EnsureDedicatedJiraBoardAsync(_project, "jira_old", Ct)).BoardId);
        Assert.Equal(2, (await _store.GetBoardsAsync(_project, Ct)).Count);
        Assert.Empty(await _store.GetPendingLaneAutomationsAsync(_project, moved.Id, Ct));
    }

    [Fact]
    public async Task SaveNeverReturnsTheToken_AndABlankTokenKeepsTheSavedOne()
    {
        var service = Service();
        var board = await _store.CreateBoardAsync(_project, "Main", Ct);
        var saved = await service.SaveAsync(_project, board.Id, Save("token-one"), "token-one", Ct);
        board = board with { Id = saved.BoardId };

        Assert.True(saved.HasToken);
        Assert.Equal("token-one", _secrets.ReadToken(saved.Id));
        Assert.DoesNotContain("token-one", saved.ToString());

        var again = await service.SaveAsync(_project, board.Id, Save(null), "   ", Ct);
        Assert.Equal(saved.Id, again.Id);
        Assert.Equal("token-one", _secrets.ReadToken(saved.Id));
    }

    [Fact]
    public async Task UnlinkKeepsImportedWorkAndIssueLinks_ButRemovesOnlyItsConnectionAndToken()
    {
        var service = Service();
        var source = await BoardWithLanes();
        var saved = await service.SaveAsync(_project, source.Id, Save("secret-token"), "secret-token", Ct);
        _jira.Pages.Enqueue(Page(Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Keep this story", "Story", "Medium", "Backlog")));
        await service.PullAsync(_project, saved.BoardId, false, Ct);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, saved.BoardId));
        await _store.AddCommentAsync(_project, card.Id, BoardAuthor.User(), "Keep this note", Ct);
        var other = await service.SaveAsync(_project, source.Id, Save("other-token"), "other-token", Ct);

        Assert.False(await service.UnlinkAsync(_project + "-foreign", saved.BoardId, Ct));
        Assert.False(await _store.DeleteJiraConnectionAsync(_project + "-foreign", saved.BoardId, saved.Id, Ct));
        Assert.False(await _store.DeleteJiraConnectionAsync(_project, saved.BoardId, other.Id, Ct));
        Assert.Equal("secret-token", _secrets.ReadToken(saved.Id));

        Assert.True(await service.UnlinkAsync(_project, saved.BoardId, Ct));
        Assert.True(await service.UnlinkAsync(_project, saved.BoardId, Ct));
        Assert.Null(await service.GetAsync(_project, saved.BoardId, Ct));
        Assert.Null(_secrets.ReadToken(saved.Id));
        Assert.NotNull(await _store.GetBoardAsync(_project, saved.BoardId, Ct));
        var kept = Assert.Single(await _store.GetCardsAsync(_project, Ct, saved.BoardId));
        Assert.Equal(card.Id, kept.Id);
        Assert.Equal(card.Title, kept.Title);
        Assert.Equal(card.Description, kept.Description);
        Assert.Equal(card.ColumnId, kept.ColumnId);
        Assert.Contains((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments, comment => comment.Body == "Keep this note");
        Assert.Equal(card.Id, (await _store.FindJiraLinkAsync(saved.Id, "100", Ct))!.CardId);
        Assert.Equal(other.Id, (await service.GetAsync(_project, other.BoardId, Ct))!.Id);
        Assert.Equal("other-token", _secrets.ReadToken(other.Id));
        await Assert.ThrowsAsync<JiraConfigException>(() => service.PullAsync(_project, saved.BoardId, false, Ct));
        Assert.Equal(1, _jira.Searches);
    }

    [Fact]
    public async Task UnlinkAndConnectionTestsRespectTheCrossProcessOperationLock()
    {
        var service = Service();
        var source = await BoardWithLanes();
        var saved = await service.SaveAsync(_project, source.Id, Save("secret-token"), "secret-token", Ct);
        using (var held = CrossProcessFileLock.TryAcquire(LockPath))
        {
            Assert.NotNull(held);
            await Assert.ThrowsAsync<JiraConfigException>(() => service.UnlinkAsync(_project, saved.BoardId, Ct));
            await Assert.ThrowsAsync<JiraConfigException>(() => service.TestAsync(_project, saved.BoardId, Ct));
            Assert.Equal("secret-token", _secrets.ReadToken(saved.Id));
            Assert.NotNull(await service.GetAsync(_project, saved.BoardId, Ct));
        }
        Assert.True(await service.UnlinkAsync(_project, saved.BoardId, Ct));
    }

    [Fact]
    public async Task DryRunCountsWithoutWriting_AndARealPullCreatesThenSkips()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct)).BoardId };
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
        Assert.Equal("0 created, 0 updated, 1 skipped, 0 failed. Skipped: 1 unchanged.", second.Message);
    }

    [Fact]
    public async Task NewerJiraUpdatePatchesMappedFields_MovesWithAutomationsSkipped_AndComments()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct)).BoardId };
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
        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct)).BoardId };
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
        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct)).BoardId };
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
        board = board with { Id = saved.BoardId };

        _jira.Outcome = JiraCallOutcome.BadJql;
        _jira.Detail = "Bounded query required.";
        var bad = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal("bad-jql", bad.Outcome);
        var disabled = (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!;
        Assert.False(disabled.Enabled);
        Assert.Equal("Bounded query required.", disabled.DisabledReason);

        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), null, Ct)).BoardId };
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
        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct)).BoardId };
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
        board = board with { Id = first.BoardId };
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
        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct)).BoardId };
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
    public async Task ASoftDeletedCardIsSkipped_WithoutRecreatingItOrCreatingTheOverflowLane()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct)).BoardId };
        _jira.Pages.Enqueue(Page(Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Gone", "Task", "Medium", "Backlog")));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id));
        Assert.True(await _store.DeleteCardAsync(_project, card.Id, Ct));
        var lanesBefore = (await _store.GetColumnsAsync(_project, Ct, board.Id)).Count;

        // A newer update with a status no lane matches: the create path would have made the overflow lane first.
        var newer = Page(Issue("100", "PROJ-1", "2026-09-02T00:00:00.000Z", "Gone", "Task", "Medium", "In QA"));
        _jira.Pages.Enqueue(newer);
        var dry = await service.PullAsync(_project, board.Id, dryRun: true, Ct);
        Assert.Equal(0, dry.Created);
        Assert.Equal(1, dry.Skipped);
        Assert.Equal("Dry run. 0 created, 0 updated, 1 skipped, 0 failed. Skipped: 1 previously deleted in VibeRails.", dry.Message);

        _jira.Pages.Enqueue(newer);
        var report = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal(0, report.Created);
        Assert.Equal(0, report.Updated);
        Assert.Equal(1, report.Skipped);
        Assert.Equal("0 created, 0 updated, 1 skipped, 0 failed. Skipped: 1 previously deleted in VibeRails.", report.Message);
        Assert.Equal("ok: " + report.Message, (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!.LastReport);
        Assert.Empty(await _store.GetCardsAsync(_project, Ct, board.Id));
        var lanes = await _store.GetColumnsAsync(_project, Ct, board.Id);
        Assert.Equal(lanesBefore, lanes.Count);
        Assert.DoesNotContain(lanes, lane => lane.Name == "Jira");
    }

    [Fact]
    public async Task PullReportDistinguishesUnchangedDeletedAndFailedIssues()
    {
        var service = Service();
        var source = await BoardWithLanes();
        var connection = await service.SaveAsync(_project, source.Id, Save("secret-token"), "secret-token", Ct);
        var unchanged = Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Keep", "Task", "Medium", "Backlog");
        var deleted = Issue("101", "PROJ-2", "2026-09-01T00:00:00.000Z", "Gone", "Task", "Medium", "Backlog");
        _jira.Pages.Enqueue(Page(unchanged, deleted));
        await service.PullAsync(_project, connection.BoardId, dryRun: false, Ct);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, connection.BoardId), card => card.Title == "Gone");
        Assert.True(await _store.DeleteCardAsync(_project, card.Id, Ct));

        _jira.Pages.Enqueue(Page(unchanged, deleted,
            Issue("102", "PROJ-3", "2026-09-01T00:00:00.000Z", "New", "Task", "Medium", "Backlog"),
            Issue("103", "PROJ-4", "2026-09-01T00:00:00.000Z", "", "Task", "Medium", "Backlog")));
        var report = await service.PullAsync(_project, connection.BoardId, dryRun: false, Ct);

        Assert.Equal(1, report.Created);
        Assert.Equal(0, report.Updated);
        Assert.Equal(2, report.Skipped);
        Assert.Equal(1, report.Failed);
        Assert.Equal("1 created, 0 updated, 2 skipped, 1 failed. Skipped: 1 unchanged, 1 previously deleted in VibeRails.", report.Message);
        Assert.Equal(2, (await _store.GetCardsAsync(_project, Ct, connection.BoardId)).Count);
        Assert.Null(await _store.FindCardAsync(_project, card.Id, Ct));
        Assert.Equal(card.Id, (await _store.FindJiraLinkAsync(connection.Id, deleted.Id, Ct))!.CardId);
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
        Assert.NotNull(await _store.DeleteBoardAsync(_project, saved.BoardId, Ct));
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
        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct)).BoardId };

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

    // ---------------------------------------------------------------- VIBE-102: board link

    private const string RobsLink =
        "https://robstokes857.atlassian.net/jira/software/projects/SCRUM/boards/1?filter=&groupBy=none&atlOrigin=eyJpIjoiOWI3NmJkMjE1Yjc1NDVhZjhlNDk5NDhkMmYxZjcyNDkiLCJwIjoiaiJ9";

    [Fact]
    public async Task ABoardLinkSavesTheSiteAndBoard_IgnoresTheBoardViewQuery_AndKeepsAProjectJqlForOlderVersions()
    {
        var service = Service();
        var board = await BoardWithLanes();
        var saved = await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct);
        board = board with { Id = saved.BoardId };

        Assert.Equal("https://robstokes857.atlassian.net", saved.SiteUrl);
        Assert.Equal("https://robstokes857.atlassian.net/jira/software/projects/SCRUM/boards/1", saved.BoardLink);
        Assert.Equal("1", saved.JiraBoardId);
        Assert.Equal("project = \"SCRUM\"", saved.Jql);
        Assert.True(saved.SkipOldDone);
        Assert.True(saved.Enabled);

        var site = await Assert.ThrowsAsync<JiraConfigException>(() =>
            service.SaveAsync(_project, board.Id, LinkSave("https://robstokes857.atlassian.net/"), null, Ct));
        Assert.Contains("not just the site", site.Message);
    }

    [Fact]
    public async Task ConnectReadsTheBoard_WritesItsFilterJql_AndMapsJiraColumnsOntoLanes()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct)).BoardId };
        _jira.Configuration = ScrumConfiguration();
        _jira.FilterJql = "project = SCRUM ORDER BY Rank ASC";

        var report = await service.TestAsync(_project, board.Id, Ct);

        Assert.True(report.Ok, report.Error);
        Assert.Equal("Ada", report.Account);
        var summary = Assert.IsType<JiraBoardSummary>(report.Board);
        Assert.Equal("SCRUM board", summary.Name);
        Assert.Equal(12, summary.IssueCount);
        Assert.Equal("customfield_10016", summary.StoryPointsFieldId);
        Assert.Equal("Story point estimate", summary.StoryPointsFieldName);
        Assert.Empty(summary.Warnings);
        Assert.Equal(["To Do→Backlog", "In Progress→", "Done→Done"], summary.Columns.Select(c => c.Name + "→" + c.LaneName));
        Assert.All(summary.Columns, column => Assert.True(column.Automatic));
        Assert.Equal(JiraJql.SkipOldDoneClause, Assert.Single(_jira.CountJql));

        var stored = (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!;
        Assert.Equal("project = SCRUM ORDER BY Rank ASC", stored.Jql);
        Assert.Equal("SCRUM board", stored.JiraBoardName);
        Assert.Equal(["To Do", "In Progress", "Done"], JiraColumnMap.Parse(stored.ColumnMap).Select(c => c.Name));
        Assert.NotNull(stored.LastTestedUtc);
    }

    [Fact]
    public async Task APullReadsTheBoardsIssues_PlacesThemByColumn_AndReadsTheBoardsStoryPointsField()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct)).BoardId };
        _jira.Configuration = ScrumConfiguration();
        await service.TestAsync(_project, board.Id, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct, board.Id);

        _jira.Pages.Enqueue(Page(
            Issue("100", "SCRUM-1", "2026-09-01T00:00:00.000Z", "Plan it", "Story", "Medium", "To Do") with { StatusId = "10000", StoryPoints = 5 },
            Issue("101", "SCRUM-2", "2026-09-01T00:00:00.000Z", "Build it", "Task", "Medium", "In Progress") with { StatusId = "3" },
            Issue("102", "SCRUM-3", "2026-09-01T00:00:00.000Z", "Ship it", "Task", "Medium", "Done") with { StatusId = "10001" }));
        var report = await service.PullAsync(_project, board.Id, dryRun: false, Ct);

        Assert.Equal("ok", report.Outcome);
        Assert.Equal(3, report.Created);
        Assert.Equal(JiraJql.SkipOldDoneClause, Assert.Single(_jira.BoardSearchJql));
        Assert.Equal("customfield_10016", Assert.Single(_jira.BoardSearchFields));
        var cards = await _store.GetCardsAsync(_project, Ct, board.Id);
        var planned = Assert.Single(cards, card => card.Title == "Plan it");
        Assert.Equal(lanes.Single(lane => lane.Name == "Backlog").Id, planned.ColumnId);
        Assert.Equal(5, planned.Points);
        Assert.Equal(lanes.Single(lane => lane.Name == "Done").Id, Assert.Single(cards, card => card.Title == "Ship it").ColumnId);
        var parked = Assert.Single(cards, card => card.Title == "Build it");
        var overflow = Assert.Single(await _store.GetColumnsAsync(_project, Ct, board.Id), lane => lane.Name == "Jira");
        Assert.Equal(overflow.Id, parked.ColumnId);
        var comment = Assert.Single((await _store.GetCardDetailAsync(_project, parked.Id, Ct))!.Comments);
        Assert.Contains("Jira column \"In Progress\"", comment.Body);

        // A lane picked for the column under Advanced wins over the automatic match.
        var build = lanes.Single(lane => lane.Name == "Build");
        await service.SaveAsync(_project, board.Id, LinkSave() with
        {
            ColumnMap = new Dictionary<string, string?> { ["In Progress"] = build.Id }
        }, null, Ct);
        _jira.Pages.Enqueue(Page(
            Issue("101", "SCRUM-2", "2026-09-02T00:00:00.000Z", "Build it", "Task", "Medium", "In Progress") with { StatusId = "3" }));
        Assert.Equal(1, (await service.PullAsync(_project, board.Id, dryRun: false, Ct)).Updated);
        Assert.Equal(build.Id, Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id), card => card.Title == "Build it").ColumnId);
    }

    [Fact]
    public async Task WithoutTheBoardConfiguration_ConnectWarns_AndThePullMatchesStatusNamesWithoutPoints()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct)).BoardId };
        _jira.ConfigurationOutcome = JiraCallOutcome.Forbidden;

        var report = await service.TestAsync(_project, board.Id, Ct);
        Assert.True(report.Ok);
        Assert.Contains("status names", Assert.Single(report.Board!.Warnings));
        Assert.Empty(report.Board.Columns);
        var stored = (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!;
        Assert.Equal("project = \"SCRUM\"", stored.Jql);
        Assert.Null(stored.ColumnMap);

        _jira.Pages.Enqueue(Page(
            Issue("100", "SCRUM-1", "2026-09-01T00:00:00.000Z", "Plan it", "Story", "Medium", "Review") with { StatusId = "10000", StoryPoints = 5 }));
        Assert.Equal(1, (await service.PullAsync(_project, board.Id, dryRun: false, Ct)).Created);
        Assert.Null(Assert.Single(_jira.BoardSearchFields));
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id));
        Assert.Equal((await _store.GetColumnsAsync(_project, Ct, board.Id)).Single(lane => lane.Name == "Review").Id, card.ColumnId);
        Assert.Null(card.Points);
    }

    [Fact]
    public async Task AnExpiredTokenOnTheBoardConfigurationEndsThePullAsExpired()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct)).BoardId };
        _jira.ConfigurationOutcome = JiraCallOutcome.Unauthorized;

        var report = await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal("expired", report.Outcome);
        Assert.Equal(0, _jira.Searches);
        Assert.Equal(BoardJiraAuthStatus.Expired, (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!.AuthStatus);
    }

    [Fact]
    public async Task ALanePickOrPointsFieldChangeReappliesUnchangedIssues_ButALocalMoveStaysWhileNothingChanges()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct)).BoardId };
        _jira.Configuration = ScrumConfiguration();
        await service.TestAsync(_project, board.Id, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct, board.Id);
        var issue = Issue("100", "SCRUM-1", "2026-09-01T00:00:00.000Z", "Plan it", "Story", "Medium", "To Do") with { StatusId = "10000", StoryPoints = 5 };
        _jira.Pages.Enqueue(Page(issue));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id));
        Assert.Equal(lanes.Single(lane => lane.Name == "Backlog").Id, card.ColumnId);

        // Dragged here and nothing changed in Jira or in the map: the pull leaves it alone.
        var ready = lanes.Single(lane => lane.Name == "Ready");
        await _board.MoveCardAsync(_project, card.Id, new BoardCardMoveRequest(ready.Id, SkipLaneAutomations: true), Ct);
        _jira.Pages.Enqueue(Page(issue));
        Assert.Equal(1, (await service.PullAsync(_project, board.Id, dryRun: false, Ct)).Skipped);
        Assert.Equal(ready.Id, Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id)).ColumnId);

        // A lane picked for the column applies to the same, unedited issue.
        var review = lanes.Single(lane => lane.Name == "Review");
        await service.SaveAsync(_project, board.Id, LinkSave() with
        {
            ColumnMap = new Dictionary<string, string?> { ["To Do"] = review.Id }
        }, null, Ct);
        _jira.Pages.Enqueue(Page(issue));
        Assert.Equal(1, (await service.PullAsync(_project, board.Id, dryRun: false, Ct)).Updated);
        card = Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id));
        Assert.Equal(review.Id, card.ColumnId);
        Assert.Contains((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments,
            comment => comment.Body.Contains("lane mapping changed", StringComparison.Ordinal));

        // So does a new estimation field on the board.
        _jira.Configuration = ScrumConfiguration() with { EstimationFieldId = "customfield_10020" };
        _jira.Pages.Enqueue(Page(issue with { StoryPoints = 8 }));
        Assert.Equal(1, (await service.PullAsync(_project, board.Id, dryRun: false, Ct)).Updated);
        Assert.Equal(8, Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id)).Points);
        _jira.Pages.Enqueue(Page(issue with { StoryPoints = 8 }));
        Assert.Equal(1, (await service.PullAsync(_project, board.Id, dryRun: false, Ct)).Skipped);
    }

    [Fact]
    public async Task AJqlConnectionSwitchedToABoardLink_PlacesItsExistingCardsByColumn()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, Save("secret-token"), "secret-token", Ct)).BoardId };
        var issue = Issue("100", "PROJ-1", "2026-09-01T00:00:00.000Z", "Plan it", "Story", "Medium", "To Do");
        _jira.Pages.Enqueue(Page(issue));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        var overflow = Assert.Single(await _store.GetColumnsAsync(_project, Ct, board.Id), lane => lane.Name == "Jira");
        Assert.Equal(overflow.Id, Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id)).ColumnId);

        // Same site, so the saved token is kept; the issue itself is untouched in Jira.
        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave("https://acme.atlassian.net/jira/software/projects/PROJ/boards/1"), null, Ct)).BoardId };
        _jira.Configuration = ScrumConfiguration();
        await service.TestAsync(_project, board.Id, Ct);
        _jira.Pages.Enqueue(Page(issue with { StatusId = "10000" }));
        Assert.Equal(1, (await service.PullAsync(_project, board.Id, dryRun: false, Ct)).Updated);

        var backlog = (await _store.GetColumnsAsync(_project, Ct, board.Id)).Single(lane => lane.Name == "Backlog");
        Assert.Equal(backlog.Id, Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id)).ColumnId);
    }

    [Fact]
    public async Task ATransientConfigurationFailureStopsThePull_InsteadOfMatchingStatusNames()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct)).BoardId };
        _jira.Configuration = ScrumConfiguration();
        await service.TestAsync(_project, board.Id, Ct);
        _jira.Pages.Enqueue(Page(Issue("100", "SCRUM-1", "2026-09-01T00:00:00.000Z", "Plan it", "Story", "Medium", "To Do") with { StatusId = "10000" }));
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        var backlog = (await _store.GetColumnsAsync(_project, Ct, board.Id)).Single(lane => lane.Name == "Backlog");

        _jira.ConfigurationOutcome = JiraCallOutcome.Failed;
        _jira.Pages.Enqueue(Page(Issue("100", "SCRUM-1", "2026-09-02T00:00:00.000Z", "Plan it", "Story", "Medium", "To Do") with { StatusId = "10000" }));
        var report = await service.PullAsync(_project, board.Id, dryRun: false, Ct);

        Assert.Equal("failed", report.Outcome);
        Assert.Contains("next interval", report.Message);
        Assert.Single(_jira.BoardSearchJql);
        Assert.Equal(backlog.Id, Assert.Single(await _store.GetCardsAsync(_project, Ct, board.Id)).ColumnId);
        var connection = (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!;
        Assert.True(connection.Enabled);
        Assert.Equal(BoardJiraAuthStatus.Saved, connection.AuthStatus);
    }

    [Fact]
    public async Task NarrowingJqlIsAndedOntoTheBoard_AndOrderByIsRefused()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave() with { NarrowJql = "  assignee = currentUser()  ", SkipOldDone = false },
            "secret-token", Ct)).BoardId };
        _jira.Configuration = ScrumConfiguration() with { SubQuery = "resolution = EMPTY" };
        _jira.Pages.Enqueue(Page());
        await service.PullAsync(_project, board.Id, dryRun: false, Ct);
        Assert.Equal("(resolution = EMPTY) AND (assignee = currentUser())", Assert.Single(_jira.BoardSearchJql));

        var ordered = await Assert.ThrowsAsync<JiraConfigException>(() => service.SaveAsync(_project, board.Id,
            LinkSave() with { NarrowJql = "assignee = currentUser() ORDER BY rank" }, null, Ct));
        Assert.Contains("ORDER BY", ordered.Message);
    }

    [Fact]
    public async Task SavingWithoutALinkKeepsTheBoard_AndADifferentBoardStartsItsColumnMapAgain()
    {
        var service = Service();
        var board = await BoardWithLanes();
        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct)).BoardId };
        _jira.Configuration = ScrumConfiguration();
        _jira.FilterJql = "project = SCRUM ORDER BY Rank ASC";
        await service.TestAsync(_project, board.Id, Ct);

        // Only the switch changes: no link, no site, no JQL in the body.
        var toggled = await service.SaveAsync(_project, board.Id,
            new BoardJiraConnectionSave("", "ada@example.com", null, "", false), null, Ct);
        Assert.Equal("1", toggled.JiraBoardId);
        Assert.Equal("project = SCRUM ORDER BY Rank ASC", toggled.Jql);
        Assert.NotNull(toggled.ColumnMap);
        Assert.False(toggled.Enabled);

        // The same board pasted again keeps what Connect read; another board starts over.
        var again = await service.SaveAsync(_project, board.Id, LinkSave(), null, Ct);
        Assert.Equal("SCRUM board", again.JiraBoardName);
        Assert.NotNull(again.ColumnMap);
        // Picks sent with another board's link were made for the previous board's columns.
        var other = await service.SaveAsync(_project, board.Id,
            LinkSave("https://robstokes857.atlassian.net/jira/software/c/projects/OPS/boards/7/backlog") with
            {
                ColumnMap = new Dictionary<string, string?> { ["To Do"] = "col_from_board_1" }
            }, null, Ct);
        Assert.Equal("7", other.JiraBoardId);
        Assert.Null(other.JiraBoardName);
        Assert.Null(other.ColumnMap);
        Assert.Equal("project = \"OPS\"", other.Jql);
        Assert.Equal(again.Id, other.Id);
    }

    [Fact]
    public async Task DetailsResolveTheSavedColumnMap_AndASavedEmailNeedsNoSuggestion()
    {
        var service = Service();
        var board = await BoardWithLanes();
        Assert.Null((await service.GetDetailsAsync(_project, board.Id, Ct)).Connection);

        board = board with { Id = (await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct)).BoardId };
        _jira.Configuration = ScrumConfiguration();
        await service.TestAsync(_project, board.Id, Ct);

        var details = await service.GetDetailsAsync(_project, board.Id, Ct);
        Assert.Equal(["Backlog", null, "Done"], details.Columns.Select(column => column.LaneName));
        Assert.Equal(5, details.Lanes.Count);
        Assert.Null(details.SuggestedEmail);
        Assert.True(details.Connection!.HasToken);
    }

    [Fact]
    public async Task AnOlderVersionsUpsertLeavesTheBoardLinkColumnsAsTheyWere()
    {
        var service = Service();
        var board = await BoardWithLanes();
        var saved = await service.SaveAsync(_project, board.Id, LinkSave(), "secret-token", Ct);
        board = board with { Id = saved.BoardId };

        // The board/12 statement an older binary runs: it names none of the board/28 columns.
        await using (var db = new SqliteConnection(_connectionString))
        {
            await db.OpenAsync(Ct);
            await using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO BoardJiraConnections
                    (Id, ProjectPath, BoardId, SiteUrl, Email, HasToken, AuthStatus, StoryPointsFieldId,
                     Jql, Enabled, DisabledReason, OverflowColumnId, LastTestedUTC, LastPullUTC, LastReport)
                SELECT $id, ProjectPath, BoardId, SiteUrl, Email, HasToken, AuthStatus, NULL,
                       'project = SCRUM AND updated >= -7d', 0, NULL, NULL, NULL, NULL, 'ok: from 1.11'
                FROM BoardJiraConnections WHERE Id = $id
                ON CONFLICT(ProjectPath, BoardId) DO UPDATE SET
                    Id = excluded.Id, SiteUrl = excluded.SiteUrl, Email = excluded.Email, HasToken = excluded.HasToken,
                    AuthStatus = excluded.AuthStatus, StoryPointsFieldId = excluded.StoryPointsFieldId,
                    Jql = excluded.Jql, Enabled = excluded.Enabled, DisabledReason = excluded.DisabledReason,
                    OverflowColumnId = excluded.OverflowColumnId, LastTestedUTC = excluded.LastTestedUTC,
                    LastPullUTC = excluded.LastPullUTC, LastReport = excluded.LastReport;
                """;
            command.Parameters.AddWithValue("$id", saved.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
        }

        var after = (await _store.GetJiraConnectionAsync(_project, board.Id, Ct))!;
        Assert.Equal("project = SCRUM AND updated >= -7d", after.Jql);
        Assert.Equal(saved.BoardLink, after.BoardLink);
        Assert.Equal("1", after.JiraBoardId);
        Assert.True(after.SkipOldDone);
        Assert.True(after.DedicatedBoard);
    }

    private static BoardJiraConnectionSave LinkSave(string link = RobsLink) =>
        new("", "ada@example.com", null, "", true, link);

    private static JiraBoardConfiguration ScrumConfiguration() => new(
        "10010", null,
        [
            new JiraBoardColumn("To Do", ["10000"]),
            new JiraBoardColumn("In Progress", ["3"]),
            new JiraBoardColumn("Done", ["10001"]),
            new JiraBoardColumn("Unmapped", [])
        ],
        "customfield_10016", "Story point estimate");

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

    internal sealed class ScriptedJira : IJiraCloudClient
    {
        public Queue<JiraSearchPage> Pages { get; } = new();
        public JiraCallOutcome Outcome { get; set; } = JiraCallOutcome.Ok;
        public string? Detail { get; set; }
        public int Searches { get; private set; }

        // Board calls (VIBE-102).
        public JiraBoard Board { get; set; } = new("1", "SCRUM board", "scrum", "SCRUM");
        public JiraCallOutcome BoardOutcome { get; set; } = JiraCallOutcome.Ok;
        public JiraBoardConfiguration? Configuration { get; set; }
        public JiraCallOutcome ConfigurationOutcome { get; set; } = JiraCallOutcome.Ok;
        public string? FilterJql { get; set; }
        public int Count { get; set; } = 12;
        public List<string?> BoardSearchJql { get; } = [];
        public List<string?> BoardSearchFields { get; } = [];
        public List<string?> CountJql { get; } = [];
        public int BoardCalls { get; private set; }

        public Task<JiraCallResult<JiraBoard>> GetBoardAsync(
            string siteUrl, string email, string apiToken, string boardId, CancellationToken cancellationToken)
        {
            BoardCalls++;
            return Task.FromResult(BoardOutcome == JiraCallOutcome.Ok
                ? new JiraCallResult<JiraBoard>(JiraCallOutcome.Ok, Board with { Id = boardId }, null, null)
                : new JiraCallResult<JiraBoard>(BoardOutcome, null, $"Jira has no board {boardId} that this account can see.", null));
        }

        public Task<JiraCallResult<JiraBoardConfiguration>> GetBoardConfigurationAsync(
            string siteUrl, string email, string apiToken, string boardId, CancellationToken cancellationToken) =>
            Task.FromResult(ConfigurationOutcome == JiraCallOutcome.Ok && Configuration is not null
                ? new JiraCallResult<JiraBoardConfiguration>(JiraCallOutcome.Ok, Configuration, null, null)
                : new JiraCallResult<JiraBoardConfiguration>(ConfigurationOutcome == JiraCallOutcome.Ok ? JiraCallOutcome.Forbidden : ConfigurationOutcome,
                    null, $"Jira did not let this account read the configuration of board {boardId}.", null));

        public Task<JiraCallResult<JiraSearchPage>> SearchBoardAsync(
            string siteUrl, string email, string apiToken, string boardId, string? jql, string? nextPageToken,
            string? storyPointsFieldId, CancellationToken cancellationToken)
        {
            Searches++;
            Assert.Equal("secret-token", apiToken);
            BoardSearchJql.Add(jql);
            BoardSearchFields.Add(storyPointsFieldId);
            if (Outcome != JiraCallOutcome.Ok)
                return Task.FromResult(new JiraCallResult<JiraSearchPage>(Outcome, null, Detail, TimeSpan.FromSeconds(1)));
            return Task.FromResult(new JiraCallResult<JiraSearchPage>(JiraCallOutcome.Ok, Pages.Dequeue(), null, null));
        }

        public Task<JiraCallResult<JiraIssueCount>> CountBoardIssuesAsync(
            string siteUrl, string email, string apiToken, string boardId, string? jql, CancellationToken cancellationToken)
        {
            CountJql.Add(jql);
            return Task.FromResult(new JiraCallResult<JiraIssueCount>(JiraCallOutcome.Ok, new JiraIssueCount(Count), null, null));
        }

        public Task<JiraCallResult<string>> GetFilterJqlAsync(
            string siteUrl, string email, string apiToken, string filterId, CancellationToken cancellationToken) =>
            Task.FromResult(FilterJql is null
                ? new JiraCallResult<string>(JiraCallOutcome.NotFound, null, "No filter.", null)
                : new JiraCallResult<string>(JiraCallOutcome.Ok, FilterJql, null, null));

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
    public async Task BoardCallsUseTheAgileAndEnhancedSoftwareEndpointsOnTheSavedOrigin()
    {
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"id":1,"name":"SCRUM board","type":"scrum","location":{"projectKey":"SCRUM"}}""");
        handler.Enqueue(HttpStatusCode.OK, """
            {"filter":{"id":"10010"},"subQuery":{"query":"resolution = EMPTY"},
             "columnConfig":{"columns":[
               {"name":"To Do","statuses":[{"id":"10000"},{"id":1}]},
               {"name":"Done","statuses":[{"id":"10001"}]}]},
             "estimation":{"type":"field","field":{"fieldId":"customfield_10016","displayName":"Story point estimate"}}}
            """);
        handler.Enqueue(HttpStatusCode.OK, """
            {"isLast":true,"nextPageToken":"ignored","issues":[{"id":"100","key":"SCRUM-1","fields":{
              "summary":"Plan it","status":{"id":"10000","name":"To Do"},"issuetype":{"name":"Story"},
              "updated":"2026-09-01T00:00:00.000+0000","customfield_10016":3}}]}
            """);
        handler.Enqueue(HttpStatusCode.OK, """{"count":153}""");
        handler.Enqueue(HttpStatusCode.OK, """{"id":"10010","jql":"project = SCRUM ORDER BY Rank ASC"}""");
        using var http = new HttpClient(handler);
        var client = new JiraCloudClient(http);
        var ct = TestContext.Current.CancellationToken;
        const string site = "https://acme.atlassian.net";

        var board = await client.GetBoardAsync(site, "ada@example.com", "secret", "1", ct);
        Assert.Equal(new JiraBoard("1", "SCRUM board", "scrum", "SCRUM"), board.Value);
        Assert.Equal("/rest/agile/1.0/board/1", handler.Paths[^1]);

        var config = (await client.GetBoardConfigurationAsync(site, "ada@example.com", "secret", "1", ct)).Value!;
        Assert.Equal("/rest/agile/1.0/board/1/configuration", handler.Paths[^1]);
        Assert.Equal("10010", config.FilterId);
        Assert.Equal("resolution = EMPTY", config.SubQuery);
        Assert.Equal(["10000", "1"], config.Columns[0].StatusIds);
        Assert.Equal("customfield_10016", config.EstimationFieldId);
        Assert.Equal("Story point estimate", config.EstimationFieldName);

        var page = (await client.SearchBoardAsync(site, "ada@example.com", "secret", "1", "labels = ui", null, "customfield_10016", ct)).Value!;
        Assert.StartsWith("/rest/software/1.0/board/1/issue?", handler.Paths[^1], StringComparison.Ordinal);
        Assert.Contains("jql=labels%20%3D%20ui", handler.Paths[^1], StringComparison.Ordinal);
        Assert.Contains("customfield_10016", handler.Paths[^1], StringComparison.Ordinal);
        Assert.Null(page.NextPageToken);
        var issue = Assert.Single(page.Issues);
        Assert.Equal("10000", issue.StatusId);
        Assert.Equal(3, issue.StoryPoints);

        Assert.Equal(153, (await client.CountBoardIssuesAsync(site, "ada@example.com", "secret", "1", null, ct)).Value!.Count);
        Assert.Equal("/rest/software/1.0/board/1/issue/approximate-count", handler.Paths[^1]);

        Assert.Equal("project = SCRUM ORDER BY Rank ASC", (await client.GetFilterJqlAsync(site, "ada@example.com", "secret", "10010", ct)).Value);
        Assert.Equal("/rest/api/3/filter/10010", handler.Paths[^1]);
        Assert.All(handler.Hosts, host => Assert.Equal("acme.atlassian.net", host));
        Assert.All(handler.Authorizations, value => Assert.StartsWith("Basic ", value, StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/rest/agile/1.0/board/1/issue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BoardRefusalsAreTyped_AndARedirectIsAFailureThatIsNotFollowed()
    {
        var handler = new QueueHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, "");
        handler.Enqueue(HttpStatusCode.NotFound, "");
        handler.Enqueue(HttpStatusCode.Unauthorized, "");
        handler.Enqueue(HttpStatusCode.Redirect, "");
        handler.Enqueue(HttpStatusCode.OK, """{"estimation":{"type":"field","field":{"fieldId":"timeoriginalestimate"}}}""");
        using var http = new HttpClient(handler);
        var client = new JiraCloudClient(http);
        var ct = TestContext.Current.CancellationToken;
        const string site = "https://acme.atlassian.net";

        Assert.Equal(JiraCallOutcome.Forbidden, (await client.GetBoardConfigurationAsync(site, "a@b.c", "secret", "1", ct)).Outcome);
        // The pull treats a board it can't see as a failed pull, not an expired token.
        Assert.Equal(JiraCallOutcome.Failed, (await client.SearchBoardAsync(site, "a@b.c", "secret", "1", null, null, null, ct)).Outcome);
        Assert.Equal(JiraCallOutcome.Unauthorized, (await client.GetBoardAsync(site, "a@b.c", "secret", "1", ct)).Outcome);
        Assert.Equal(JiraCallOutcome.Failed, (await client.GetBoardAsync(site, "a@b.c", "secret", "1", ct)).Outcome);
        Assert.Equal(4, handler.Paths.Count);
        // Time tracking is not a story points field.
        Assert.Null((await client.GetBoardConfigurationAsync(site, "a@b.c", "secret", "1", ct)).Value!.EstimationFieldId);

        await Assert.ThrowsAsync<JiraConfigException>(() => client.GetBoardAsync(site, "a@b.c", "secret", "1/../2", ct));
        await Assert.ThrowsAsync<JiraConfigException>(() => client.GetFilterJqlAsync(site, "a@b.c", "secret", "-1", ct));
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
        public List<string> Paths { get; } = [];
        public List<string> Hosts { get; } = [];
        public List<string> Authorizations { get; } = [];

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
            Paths.Add(LastPath);
            Hosts.Add(request.RequestUri.Host);
            Authorizations.Add(LastAuthorization ?? string.Empty);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
