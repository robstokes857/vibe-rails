using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// board-ado/1 and board-ado-delivery/1 (VIBE-166): Azure DevOps connections, work item links and
/// the outbound ledger live in their own tables beside Jira's, and leave Jira's untouched.
/// </summary>
public sealed class BoardAdoStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "board-ado-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;
    private readonly string _stateConnectionString;
    private readonly BoardStore _store;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardAdoStoreTests()
    {
        Directory.CreateDirectory(_root);
        _connectionString = $"Data Source={Path.Combine(_root, "board.db")};Pooling=False";
        _stateConnectionString = $"Data Source={Path.Combine(_root, "state.db")};Pooling=False";
        _store = new BoardStore(_connectionString, _stateConnectionString);
    }

    [Fact]
    public async Task ConnectionUpsertNeedsTheBoardInThisProject_AndANewOrganizationReplacesTheId()
    {
        var board = await _store.CreateBoardAsync(_root, "Main", Ct);

        Assert.Null(await _store.SaveAdoConnectionAsync(Connection("ado_missing00000", "brd_missing"), Ct));
        Assert.Null(await _store.SaveAdoConnectionAsync(Connection("ado_foreign00000", board.Id) with
        {
            ProjectPath = Path.Combine(_root, "other-project")
        }, Ct));
        Assert.Empty(await _store.GetAdoConnectionsAsync(Ct));

        var saved = await _store.SaveAdoConnectionAsync(Connection("ado_aaaaaaaaaaaa", board.Id) with
        {
            ColumnMap = "[{\"name\":\"New\",\"lane\":null}]", PointsField = "Microsoft.VSTS.Scheduling.StoryPoints",
            NarrowWiql = "[System.Tags] CONTAINS 'web'", SkipOldDone = true, LastTestedUtc = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc),
            LastReport = "Pulled 3"
        }, Ct);
        Assert.NotNull(saved);
        Assert.Equal("ado_aaaaaaaaaaaa", saved.Id);
        Assert.Equal("contoso", saved.Organization);
        Assert.Equal("Web Team", saved.Team);
        Assert.Equal("Stories", saved.BacklogLevel);
        Assert.Null(saved.QueryId);
        Assert.Equal("Microsoft.VSTS.Scheduling.StoryPoints", saved.PointsField);
        Assert.True(saved.SkipOldDone);
        Assert.False(saved.DedicatedBoard);
        Assert.Equal(BoardAdoAuthStatus.Saved, saved.AuthStatus);
        Assert.Equal(new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc), saved.LastTestedUtc);
        Assert.Null(saved.LastPullUtc);
        Assert.Equal(saved, await _store.GetAdoConnectionAsync(_root, board.Id, Ct));

        var moved = await _store.SaveAdoConnectionAsync(Connection("ado_bbbbbbbbbbbb", board.Id) with
        {
            Organization = "fabrikam", Team = null, BacklogLevel = null, QueryId = "0b7a8a3e-5d5b-4f0a-9a43-5a8a2c1e6f10",
            AdoBoardName = "Open bugs", AuthStatus = BoardAdoAuthStatus.Expired
        }, Ct);
        Assert.Equal("ado_bbbbbbbbbbbb", moved!.Id);
        Assert.Equal("fabrikam", moved.Organization);
        Assert.Null(moved.Team);
        Assert.Null(moved.SkipOldDone);
        Assert.Equal(BoardAdoAuthStatus.Expired, moved.AuthStatus);
        Assert.Equal("ado_bbbbbbbbbbbb", Assert.Single(await _store.GetAdoConnectionsAsync(Ct)).Id);
        Assert.Null(await _store.GetJiraConnectionAsync(_root, board.Id, Ct));

        Assert.False(await _store.DeleteAdoConnectionAsync(_root, board.Id, "ado_aaaaaaaaaaaa", Ct));
        Assert.True(await _store.DeleteAdoConnectionAsync(_root, board.Id, "ado_bbbbbbbbbbbb", Ct));
        Assert.Null(await _store.GetAdoConnectionAsync(_root, board.Id, Ct));
    }

    [Fact]
    public async Task OneLinkPerWorkItem_AndCreateAdoCardWritesNothingForALinkedItem()
    {
        var (board, lane) = await LinkedBoardAsync();
        var link = new BoardAdoLinkRecord("", "ado_aaaaaaaaaaaa", 42, 7, Utc("2026-10-01T09:00:00Z"), "Ada Lovelace", Utc("2026-10-02T09:00:00Z"),
            "points:Microsoft.VSTS.Scheduling.StoryPoints;status:Active");

        var card = await _store.CreateAdoCardAsync(_root, NewCard(lane, "Imported"), link, Ct);
        Assert.NotNull(card);
        var read = (await _store.GetCardDetailAsync(_root, card.Id, Ct))!.Card;
        Assert.Equal(42, read.AdoWorkItemId);
        Assert.Null(read.JiraIssueKey);

        var changedLink = link with { Rev = 9, AssigneeDisplay = null, Mapping = "status:Closed",
            ChangedDate = Utc("2026-10-03T09:00:00Z"), LastPulledUtc = Utc("2026-10-04T09:00:00Z") };
        Assert.Null(await _store.CreateAdoCardAsync(_root, NewCard(lane, "Duplicate"), changedLink, Ct));
        Assert.Single(await _store.GetCardsAsync(_root, Ct, board.Id));
        await Assert.ThrowsAsync<SqliteException>(() => _store.AddAdoLinkAsync(link with { CardId = card.Id }, Ct));

        var found = await _store.FindAdoLinkAsync("ado_aaaaaaaaaaaa", 42, Ct);
        Assert.Equal(link with { CardId = card.Id }, found);
        Assert.Null(await _store.FindAdoLinkAsync("ado_cccccccccccc", 42, Ct));

        // The same work item id under another connection (another organization) is a separate link.
        var other = await _store.CreateCardAsync(_root, NewCard(lane, "Local"), Ct);
        await _store.AddAdoLinkAsync(link with { CardId = other.Id, ConnectionId = "ado_cccccccccccc" }, Ct);
        Assert.Equal(link with { CardId = card.Id }, Assert.Single(await _store.GetAdoLinksForCardAsync(_root, card.Id, Ct)));
        Assert.Equal(7, (await _store.FindAdoLinkAsync("ado_cccccccccccc", 42, Ct))!.Rev);
        Assert.Empty(await _store.GetAdoLinksForCardAsync(Path.Combine(_root, "other-project"), card.Id, Ct));
    }

    [Fact]
    public async Task CommentsAndSessionsQueueForTheLinkedWorkItem_UnlessSuppressedCopiedOrWrittenByThePull()
    {
        var (_, lane) = await LinkedBoardAsync();
        var card = (await _store.CreateAdoCardAsync(_root, NewCard(lane, "Imported"), Link(42), Ct))!;
        var local = await _store.CreateCardAsync(_root, NewCard(lane, "Local only"), Ct);

        var sent = await _store.AddCommentAsync(_root, card.Id, BoardAuthor.User(), "Ready for review", Ct);
        await _store.AddCommentAsync(_root, card.Id, BoardAuthor.Agent("Codex", "codex", null), "Internal", Ct, syncToJira: false);
        await _store.AddCommentAsync(_root, card.Id, BoardAuthor.Agent("Azure DevOps", null, null), "State changed; card not moved", Ct);
        await _store.AddCommentAsync(_root, local.Id, BoardAuthor.User(), "Nothing to send", Ct);
        await ExecuteAsync("""
            INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, Body, CreatedUTC, Kind, Changes)
            VALUES ('cm_copied', $card, 'user', 'You', 'Copied discussion', '2026-10-19T09:00:00.0000000Z', 'comment', '{"jiraCopy":true}');
            INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, Body, CreatedUTC, Kind, RemoteSeq)
            VALUES ('cm_remote', $card, 'user', 'You', 'Pulled from viberails.ai', '2026-10-19T09:00:00.0000000Z', 'comment', 12);
            INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, Body, CreatedUTC, Kind, DiscussionHidden)
            VALUES ('cm_hidden', $card, 'user', 'You', 'Superseded copy', '2026-10-19T09:00:00.0000000Z', 'comment', 1);
            """, ("$card", card.Id));

        var pending = Assert.Single(await _store.GetPendingAdoDeliveriesAsync(Ct));
        Assert.Equal(sent!.Id, pending.SourceId);
        Assert.Equal("comment", pending.Kind);
        Assert.Equal("ado_aaaaaaaaaaaa", pending.ConnectionId);
        Assert.Equal(42, pending.WorkItemId);
        Assert.Equal("You: Ready for review", pending.Body);
        Assert.Equal("pending", pending.Status);
        Assert.Empty(await _store.GetPendingJiraDeliveriesAsync(Ct));

        var session = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_root, card.Id, session, null, "base:codex", "codex", "Working session", BoardSessionRecord.LaunchOrigin, Ct);
        await _store.LinkSessionAsync(_root, local.Id, Guid.NewGuid().ToString(), null, "base:codex", "codex", "Local session", BoardSessionRecord.LaunchOrigin, Ct);
        var deliveries = await _store.GetAdoDeliveriesAsync(_root, card.Id, Ct);
        Assert.Equal(2, deliveries.Count);
        var linked = Assert.Single(deliveries, d => d.Kind == "session");
        Assert.Equal(session, linked.SourceId);
        Assert.Equal("VibeRails session linked: Working session", linked.Body);
        Assert.Empty(await _store.GetAdoDeliveriesAsync(Path.Combine(_root, "other-project"), card.Id, Ct));
        Assert.Empty(await _store.GetAdoDeliveriesAsync(_root, local.Id, Ct));
    }

    [Fact]
    public async Task DeliveryStatusIsCompareAndSet_AndAnInterruptedSendBecomesUncertain()
    {
        var (_, lane) = await LinkedBoardAsync();
        var card = (await _store.CreateAdoCardAsync(_root, NewCard(lane, "Imported"), Link(42), Ct))!;
        await _store.AddCommentAsync(_root, card.Id, BoardAuthor.User(), "First", Ct);
        await _store.AddCommentAsync(_root, card.Id, BoardAuthor.User(), "Second", Ct);
        var pending = await _store.GetPendingAdoDeliveriesAsync(Ct);
        Assert.Equal(2, pending.Count);

        Assert.True(await _store.SetAdoDeliveryAsync(pending[0].Id, "pending", "sending", null, null, Ct));
        Assert.False(await _store.SetAdoDeliveryAsync(pending[0].Id, "pending", "sending", null, null, Ct));
        Assert.True(await _store.SetAdoDeliveryAsync(pending[1].Id, "pending", "sent", null, "https://dev.azure.com/contoso/Web/_workitems/edit/42", Ct));
        await _store.RecoverAdoDeliveriesAsync(Ct);

        Assert.Empty(await _store.GetPendingAdoDeliveriesAsync(Ct));
        var all = await _store.GetAdoDeliveriesAsync(_root, card.Id, Ct);
        var interrupted = Assert.Single(all, d => d.Id == pending[0].Id);
        Assert.Equal("uncertain", interrupted.Status);
        Assert.Contains("Check Azure DevOps", interrupted.Message);
        var done = Assert.Single(all, d => d.Id == pending[1].Id);
        Assert.Equal("sent", done.Status);
        Assert.Equal("https://dev.azure.com/contoso/Web/_workitems/edit/42", done.Url);
    }

    [Fact]
    public async Task DedicatedBoardIsMadeOnce_UnpublishedWithClonedLanesAndNoAutomations()
    {
        var jobs = new JobStore(_stateConnectionString, _store);
        await ExecuteStateAsync(SqlStrings.CreateEnvironmentsTable);
        var job = await jobs.CreateJobAsync(new("Review", _root, LLM.NotSet, null, "", null, true, [], Actions:
            [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python, ApprovedHash: "pinned")]), Ct);
        var source = await _store.CreateBoardAsync(_root, "Main", Ct);
        await _store.SetBoardSyncEnabledAsync(_root, source.Id, true, Ct);
        var lanes = await _store.GetColumnsAsync(_root, Ct, source.Id);
        await _store.SaveLaneAutomationAsync(_root, lanes[1].Id, [job.Id], 0, Ct);
        await _store.SaveAdoConnectionAsync(Connection("ado_aaaaaaaaaaaa", source.Id) with
        {
            OverflowColumnId = lanes[1].Id,
            ColumnMap = $$"""[{"name":"Active","lane":"{{lanes[1].Id}}"},{"name":"Closed","lane":null}]"""
        }, Ct);
        var native = await _store.CreateCardAsync(_root, NewCard(lanes[0].Id, "Local work"), Ct);
        var imported = (await _store.CreateAdoCardAsync(_root, NewCard(lanes[1].Id, "Azure DevOps work"), Link(42), Ct))!;
        await _store.AddCommentAsync(_root, imported.Id, BoardAuthor.User(), "Keep this discussion", Ct);
        var queued = Assert.Single(await _store.GetAdoDeliveriesAsync(_root, imported.Id, Ct));

        var destination = await _store.EnsureDedicatedAdoBoardAsync(_root, "ado_aaaaaaaaaaaa", Ct);

        Assert.NotEqual(source.Id, destination.BoardId);
        Assert.True(destination.DedicatedBoard);
        Assert.Null(await _store.GetAdoConnectionAsync(_root, source.Id, Ct));
        Assert.Equal(destination, await _store.GetAdoConnectionAsync(_root, destination.BoardId, Ct));
        var board = (await _store.GetBoardAsync(_root, destination.BoardId, Ct))!;
        Assert.Equal("Azure DevOps · Web Team", board.Name);
        Assert.False(board.SyncEnabled);
        var cloned = await _store.GetColumnsAsync(_root, Ct, destination.BoardId);
        Assert.Equal(lanes.Select(lane => lane.Name), cloned.Select(lane => lane.Name));
        Assert.All(cloned, lane => Assert.DoesNotContain(lane.Id, lanes.Select(l => l.Id)));
        foreach (var lane in cloned)
            Assert.Empty((await _store.GetLaneAutomationAsync(_root, lane.Id, Ct))!.JobIds);
        Assert.Equal(cloned[1].Id, destination.OverflowColumnId);
        Assert.Contains($"\"lane\":\"{cloned[1].Id}\"", destination.ColumnMap);

        Assert.Equal(native.Id, Assert.Single(await _store.GetCardsAsync(_root, Ct, source.Id)).Id);
        var moved = Assert.Single(await _store.GetCardsAsync(_root, Ct, destination.BoardId));
        Assert.Equal(imported.Id, moved.Id);
        Assert.Equal(imported.Key, moved.Key);
        Assert.Equal(cloned[1].Id, moved.ColumnId);
        Assert.Equal(42, moved.AdoWorkItemId);
        Assert.Empty(await _store.GetPendingLaneAutomationsAsync(_root, moved.Id, Ct));
        var comments = (await _store.GetCardDetailAsync(_root, moved.Id, Ct))!.Comments;
        var kept = Assert.Single(comments, comment => comment.Body == "Keep this discussion");
        // The move republished the discussion with new ids; the queued delivery follows it instead of requeueing.
        var rebound = Assert.Single(await _store.GetAdoDeliveriesAsync(_root, moved.Id, Ct));
        Assert.Equal(queued.Id, rebound.Id);
        Assert.Equal(kept.Id, rebound.SourceId);

        Assert.Equal(destination.BoardId, (await _store.EnsureDedicatedAdoBoardAsync(_root, "ado_aaaaaaaaaaaa", Ct)).BoardId);
        Assert.Equal(2, (await _store.GetBoardsAsync(_root, Ct)).Count);
    }

    /// <summary>
    /// Connecting a board made only for the connection must not leave it behind empty: the board
    /// itself becomes the Azure DevOps board, keeping its name, lanes and lane choices.
    /// </summary>
    [Fact]
    public async Task ABoardNothingElseUsesBecomesTheAzureDevOpsBoardItself()
    {
        await _store.CreateBoardAsync(_root, "Main", Ct);
        var board = await _store.CreateBoardAsync(_root, "Our work items", Ct);
        var lanes = await _store.GetColumnsAsync(_root, Ct, board.Id);
        var map = $$"""[{"name":"Active","lane":"{{lanes[1].Id}}"}]""";
        await _store.SaveAdoConnectionAsync(Connection("ado_aaaaaaaaaaaa", board.Id) with { OverflowColumnId = lanes[1].Id, ColumnMap = map }, Ct);
        // The connection's own imports and deleted cards leave the board unused.
        var imported = (await _store.CreateAdoCardAsync(_root, NewCard(lanes[1].Id, "Azure DevOps work"), Link(42), Ct))!;
        var deleted = await _store.CreateCardAsync(_root, NewCard(lanes[0].Id, "Deleted work"), Ct);
        Assert.True(await _store.DeleteCardAsync(_root, deleted.Id, Ct));

        var dedicated = await _store.EnsureDedicatedAdoBoardAsync(_root, "ado_aaaaaaaaaaaa", Ct);

        Assert.Equal(board.Id, dedicated.BoardId);
        Assert.True(dedicated.DedicatedBoard);
        Assert.Equal((map, lanes[1].Id), (dedicated.ColumnMap, dedicated.OverflowColumnId));
        Assert.Equal(dedicated, await _store.GetAdoConnectionAsync(_root, board.Id, Ct));
        Assert.Equal("Our work items", (await _store.GetBoardAsync(_root, board.Id, Ct))!.Name);
        Assert.Equal(lanes.Select(lane => lane.Id), (await _store.GetColumnsAsync(_root, Ct, board.Id)).Select(lane => lane.Id));
        Assert.Equal(imported.Id, Assert.Single(await _store.GetCardsAsync(_root, Ct, board.Id)).Id);
        Assert.Equal(2, (await _store.GetBoardsAsync(_root, Ct)).Count);
        Assert.Equal(board.Id, (await _store.EnsureDedicatedAdoBoardAsync(_root, "ado_aaaaaaaaaaaa", Ct)).BoardId);
    }

    /// <summary>
    /// The board is copied, as before, when the New board dialog asks for a new one, when taking
    /// it over would hand local work or lane Automations to the tracker or stop viberails.ai sync,
    /// or when it is the project's last local board.
    /// </summary>
    [Theory]
    [InlineData("newBoard")]
    [InlineData("localCard")]
    [InlineData("laneAutomation")]
    [InlineData("syncEnabled")]
    [InlineData("onlyLocalBoard")]
    [InlineData("otherBoardIsTracker")]
    public async Task ABoardInUse_OrTheLastLocalOne_IsCopiedInstead(string use)
    {
        if (use != "onlyLocalBoard")
        {
            var main = await _store.CreateBoardAsync(_root, "Main", Ct);
            if (use == "otherBoardIsTracker")
                await _store.SaveAdoConnectionAsync(Connection("ado_bbbbbbbbbbbb", main.Id) with { DedicatedBoard = true }, Ct);
        }
        var board = await _store.CreateBoardAsync(_root, "Sprint", Ct);
        var lanes = await _store.GetColumnsAsync(_root, Ct, board.Id);
        await _store.SaveAdoConnectionAsync(Connection("ado_aaaaaaaaaaaa", board.Id), Ct);
        switch (use)
        {
            case "localCard":
                await _store.CreateCardAsync(_root, NewCard(lanes[0].Id, "Local work"), Ct);
                break;
            case "laneAutomation":
                await ExecuteStateAsync(SqlStrings.CreateEnvironmentsTable);
                var job = await new JobStore(_stateConnectionString, _store).CreateJobAsync(new("Review", _root, LLM.NotSet, null, "", null, true, [], Actions:
                    [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python, ApprovedHash: "pinned")]), Ct);
                await _store.SaveLaneAutomationAsync(_root, lanes[1].Id, [job.Id], 0, Ct);
                break;
            case "syncEnabled":
                await _store.SetBoardSyncEnabledAsync(_root, board.Id, true, Ct);
                break;
        }

        var dedicated = await _store.EnsureDedicatedAdoBoardAsync(_root, "ado_aaaaaaaaaaaa", Ct, newBoard: use == "newBoard");

        Assert.NotEqual(board.Id, dedicated.BoardId);
        Assert.True(dedicated.DedicatedBoard);
        Assert.Null(await _store.GetAdoConnectionAsync(_root, board.Id, Ct));
        Assert.Equal("Sprint", (await _store.GetBoardAsync(_root, board.Id, Ct))!.Name);
        Assert.Equal(use == "onlyLocalBoard" ? 2 : 3, (await _store.GetBoardsAsync(_root, Ct)).Count);
    }

    [Fact]
    public async Task AQueryConnectionNamesItsBoardForTheQuery()
    {
        var source = await _store.CreateBoardAsync(_root, "Main", Ct);
        await _store.SaveAdoConnectionAsync(Connection("ado_aaaaaaaaaaaa", source.Id) with
        {
            Team = null, BacklogLevel = null, QueryId = "0b7a8a3e-5d5b-4f0a-9a43-5a8a2c1e6f10", AdoBoardName = "Open bugs"
        }, Ct);
        var destination = await _store.EnsureDedicatedAdoBoardAsync(_root, "ado_aaaaaaaaaaaa", Ct);
        Assert.Equal("Azure DevOps · Open bugs", (await _store.GetBoardAsync(_root, destination.BoardId, Ct))!.Name);
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.EnsureDedicatedAdoBoardAsync(_root, "ado_cccccccccccc", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveringAQueryNameReplacesOnlyItsProvisionalLabel_WithTheBoardNameLimit(bool customName)
    {
        var source = await _store.CreateBoardAsync(_root, "Main", Ct);
        await _store.SaveAdoConnectionAsync(Connection("ado_aaaaaaaaaaaa", source.Id) with
        {
            Project = new string('p', 80), Team = null, BacklogLevel = null,
            QueryId = "0b7a8a3e-5d5b-4f0a-9a43-5a8a2c1e6f10", AdoBoardName = null
        }, Ct);
        var destination = await _store.EnsureDedicatedAdoBoardAsync(_root, "ado_aaaaaaaaaaaa", Ct);
        var provisional = (await _store.GetBoardAsync(_root, destination.BoardId, Ct))!;
        Assert.Equal(60, provisional.Name.Length);
        if (customName)
            await _store.RenameBoardAsync(_root, destination.BoardId, "My triage", "TRIAGE", Ct);

        await _store.SaveAdoConnectionAsync(destination with { AdoBoardName = new string('q', 80) }, Ct);
        var resolved = await _store.EnsureDedicatedAdoBoardAsync(_root, destination.Id, Ct);
        var named = (await _store.GetBoardAsync(_root, resolved.BoardId, Ct))!;
        Assert.Equal(destination.BoardId, resolved.BoardId);
        Assert.Equal(customName ? "My triage" : ("Azure DevOps · " + new string('q', 80))[..60], named.Name);
        Assert.Equal(customName ? "TRIAGE" : provisional.DisplayPrefix, named.DisplayPrefix);
        Assert.Equal(2, (await _store.GetBoardsAsync(_root, Ct)).Count);
    }

    [Fact]
    public async Task DeletingABoardDeletesItsAzureDevOpsConnection_AndKeepsOtherBoards()
    {
        var kept = await _store.CreateBoardAsync(_root, "Main", Ct);
        var removed = await _store.CreateBoardAsync(_root, "Azure DevOps", Ct);
        await _store.SaveAdoConnectionAsync(Connection("ado_aaaaaaaaaaaa", kept.Id), Ct);
        await _store.SaveAdoConnectionAsync(Connection("ado_bbbbbbbbbbbb", removed.Id), Ct);

        Assert.NotNull(await _store.DeleteBoardAsync(_root, removed.Id, Ct));

        Assert.Equal("ado_aaaaaaaaaaaa", Assert.Single(await _store.GetAdoConnectionsAsync(Ct)).Id);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM BoardAdoConnections;";
        Assert.Equal(1L, await count.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task OriginFilterFindsAzureDevOpsCards_AndHumanLeavesOutBothTrackers()
    {
        var (board, lane) = await LinkedBoardAsync();
        var ado = (await _store.CreateAdoCardAsync(_root, NewCard(lane, "From Azure DevOps"), Link(42), Ct))!;
        var jira = (await _store.CreateJiraCardAsync(_root, NewCard(lane, "From Jira"),
            new BoardJiraLinkRecord("", "jira_aaaaaaaaaaaa", "100", "PROJ-1", null, DateTime.UtcNow, DateTime.UtcNow), Ct))!;
        var human = await _store.CreateCardAsync(_root, NewCard(lane, "Typed in"), Ct);

        async Task<IEnumerable<string>> Origin(string origin) =>
            (await _store.GetCardsPageAsync(_root, new BoardCardPageQuery(Origin: origin), Ct, board.Id)).Cards.Select(card => card.Id);

        Assert.Equal([ado.Id], await Origin("ado"));
        Assert.Equal([jira.Id], await Origin("jira"));
        Assert.Equal([human.Id], await Origin("human"));
    }

    /// <summary>
    /// A comment written the way an older binary writes it (none of the columns added since
    /// board/14) still fires the Azure DevOps trigger and succeeds; its generic syncToJira=false
    /// still suppresses Azure DevOps delivery.
    /// </summary>
    [Fact]
    public async Task AnOlderBinarysCommentInsertQueuesWithoutFailing_AndItsSuppressionHolds()
    {
        var (_, lane) = await LinkedBoardAsync();
        var card = (await _store.CreateAdoCardAsync(_root, NewCard(lane, "Imported"), Link(42), Ct))!;

        Assert.Equal(1, await ExecuteAsync("""
            INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC)
            VALUES ('cm_old_shape', $card, 'user', 'You', NULL, NULL, 'Written by 1.9', '2026-10-19T09:00:00.0000000Z');
            """, ("$card", card.Id)));
        await ExecuteAsync("""
            INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind, Changes)
            VALUES ('cm_old_quiet', $card, 'agent', 'Codex', 'codex', NULL, 'Internal', '2026-10-19T09:00:00.0000000Z', 'note', '{"syncToJira":{"to":false}}');
            """, ("$card", card.Id));

        var pending = Assert.Single(await _store.GetPendingAdoDeliveriesAsync(Ct));
        Assert.Equal("cm_old_shape", pending.SourceId);
        Assert.Equal("You: Written by 1.9", pending.Body);
    }

    /// <summary>
    /// The Azure DevOps migrations only add objects: an older binary's database (Jira tables,
    /// triggers and everything else, without board-ado) gains the Azure DevOps objects and every
    /// other schema object keeps its exact SQL. A Jira-linked card's comment still queues only for
    /// Jira.
    /// </summary>
    [Fact]
    public async Task AdoMigrationsLeaveJiraAndEveryOtherObjectUnchanged()
    {
        var before = await SchemaAsync();
        Assert.Contains(before, o => o.Key == "BoardComments_QueueJira");
        Assert.DoesNotContain(before.Where(o => !o.Key.Contains("Ado", StringComparison.Ordinal)),
            o => o.Value.Contains("BoardAdo", StringComparison.Ordinal));
        Assert.Equal(["Id", "ProjectPath", "BoardId", "SiteUrl", "Email", "HasToken", "AuthStatus", "StoryPointsFieldId", "Jql",
            "Enabled", "DisabledReason", "OverflowColumnId", "LastTestedUTC", "LastPullUTC", "LastReport", "BoardLink", "JiraBoardId",
            "JiraBoardName", "ColumnMap", "NarrowJql", "SkipOldDone", "DedicatedBoard"], await ColumnsAsync("BoardJiraConnections"));
        Assert.Equal(["CardId", "SiteId", "IssueId", "IssueKey", "AssigneeDisplay", "IssueUpdated", "LastPulledUTC", "Mapping"],
            await ColumnsAsync("BoardJiraLinks"));

        await ExecuteAsync("""
            DROP TRIGGER BoardComments_QueueAdo;
            DROP TABLE BoardAdoDeliveries;
            DROP TRIGGER Boards_DeleteAdoConnection;
            DROP TABLE BoardAdoLinks;
            DROP TABLE BoardAdoConnections;
            DELETE FROM SchemaMigrations WHERE Component IN ('board-ado', 'board-ado-delivery');
            """);
        var older = await SchemaAsync();
        Assert.DoesNotContain(older, o => o.Key.Contains("Ado", StringComparison.Ordinal));

        var upgraded = new BoardStore(_connectionString, _stateConnectionString);
        var after = await SchemaAsync();
        Assert.Equal(before, after);
        Assert.All(older, o => Assert.Equal(o.Value, after[o.Key]));

        var board = await upgraded.CreateBoardAsync(_root, "Jira", Ct);
        var lane = (await upgraded.GetColumnsAsync(_root, Ct, board.Id))[0].Id;
        await upgraded.SaveJiraConnectionAsync(new BoardJiraConnectionRecord("jira_aaaaaaaaaaaa", _root, board.Id,
            "https://acme.atlassian.net", "ada@example.com", true, "saved", null, "project = PROJ", true,
            null, null, null, null, null), Ct);
        var card = (await upgraded.CreateJiraCardAsync(_root, NewCard(lane, "From Jira"),
            new BoardJiraLinkRecord("", "jira_aaaaaaaaaaaa", "100", "PROJ-1", null, DateTime.UtcNow, DateTime.UtcNow), Ct))!;
        await upgraded.AddCommentAsync(_root, card.Id, BoardAuthor.User(), "Jira only", Ct);
        Assert.Single(await upgraded.GetPendingJiraDeliveriesAsync(Ct));
        Assert.Empty(await upgraded.GetPendingAdoDeliveriesAsync(Ct));
    }

    private async Task<(BoardRecord Board, string Lane)> LinkedBoardAsync()
    {
        var board = await _store.CreateBoardAsync(_root, "Main", Ct);
        await _store.SaveAdoConnectionAsync(Connection("ado_aaaaaaaaaaaa", board.Id), Ct);
        return (board, (await _store.GetColumnsAsync(_root, Ct, board.Id))[0].Id);
    }

    private BoardAdoConnectionRecord Connection(string id, string boardId) => new(
        id, _root, boardId, "contoso", "Web", "Web Team", "Stories", null,
        "https://dev.azure.com/contoso/Web/_boards/board/t/Web%20Team/Stories", null,
        null, null, null, null, true, BoardAdoAuthStatus.Saved, true, null, null, false, null, null, null);

    private static BoardAdoLinkRecord Link(int workItemId) =>
        new("", "ado_aaaaaaaaaaaa", workItemId, 1, Utc("2026-10-01T09:00:00Z"), null, Utc("2026-10-01T09:00:00Z"));

    private static NewBoardCard NewCard(string lane, string title) =>
        new(lane, title, "", null, "medium", null, [], false);

    private static DateTime Utc(string text) => DateTime.Parse(text, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    private async Task<Dictionary<string, string>> SchemaAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name <> 'SchemaMigrations';";
        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            objects[reader.GetString(0)] = reader.GetString(1);
        return objects;
    }

    private async Task<IReadOnlyList<string>> ColumnsAsync(string table)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task ExecuteStateAsync(string sql)
    {
        await using var connection = new SqliteConnection(_stateConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
