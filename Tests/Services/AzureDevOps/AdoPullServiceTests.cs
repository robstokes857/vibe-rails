using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Services.AzureDevOps;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Tracker;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.AzureDevOps;

public sealed class AdoPullServiceTests : IDisposable
{
    private const string BoardLink = "https://dev.azure.com/contoso/Web/_boards/board/t/Core/Stories";
    private const string QueryId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string QueryLink = "https://dev.azure.com/contoso/Web/_queries/query/" + QueryId + "/";
    private const string Pat = "secret-pat";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-ado-{Guid.NewGuid():N}");
    private readonly string _project;
    private readonly BoardStore _store;
    private readonly BoardService _board;
    private readonly MemoryAdoSecrets _secrets = new();
    private readonly ScriptedAdo _ado = new();

    public AdoPullServiceTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project");
        var connectionString = $"Data Source={Path.Combine(_root, "board.db")};Mode=ReadWriteCreate;Cache=Shared";
        _store = new BoardStore(connectionString, connectionString);
        _board = new BoardService(_store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DryRunCountsWithoutWriting_ARealPullCreatesOnTheDedicatedBoard_AndAnUnchangedRevisionIsSkipped()
    {
        var service = Service();
        var source = await BoardWithLanes();
        var saved = await service.SaveAsync(_project, source.Id, Save(), Pat, Ct);
        Assert.NotEqual(source.Id, saved.BoardId);
        Assert.True(saved.DedicatedBoard);
        Assert.Equal("Azure DevOps · Core", (await _store.GetBoardAsync(_project, saved.BoardId, Ct))!.Name);
        _ado.Serve(
            Item(1, "Fix login", "Bug", "New", priority: 1, points: 3),
            Item(2, "Tell the story", "User Story", "New", priority: 4));

        var dry = await service.PullAsync(_project, saved.BoardId, dryRun: true, Ct);
        Assert.Equal(("ok", 2), (dry.Outcome, dry.Created));
        Assert.StartsWith("Dry run. ", dry.Message, StringComparison.Ordinal);
        Assert.Empty(await _store.GetCardsAsync(_project, Ct, saved.BoardId));

        var wrote = await service.PullAsync(_project, saved.BoardId, dryRun: false, Ct);
        Assert.Equal(("ok", 2, saved.BoardId), (wrote.Outcome, wrote.Created, wrote.BoardId));
        var cards = await _store.GetCardsAsync(_project, Ct, saved.BoardId);
        var bug = Assert.Single(cards, card => card.Title == "Fix login");
        Assert.Equal((BoardCardTypes.Bug, "critical", 3), (bug.Type, bug.Priority, bug.Points));
        Assert.Null(bug.Assignee);
        Assert.Equal(1, bug.AdoWorkItemId);
        var story = Assert.Single(cards, card => card.Title == "Tell the story");
        Assert.Equal((BoardCardTypes.Feature, "low"), (story.Type, story.Priority));
        var link = (await _store.FindAdoLinkAsync(saved.Id, 1, Ct))!;
        Assert.Equal((bug.Id, 1, "Ada"), (link.CardId, link.Rev, link.AssigneeDisplay));
        Assert.Equal("points:" + AdoFields.StoryPoints + ";status:New", link.Mapping);

        var again = await service.PullAsync(_project, saved.BoardId, dryRun: false, Ct);
        Assert.Equal("0 created, 0 updated, 2 skipped, 0 failed. Skipped: 2 unchanged.", again.Message);
        Assert.StartsWith("ok: 0 created", (await service.GetAsync(_project, saved.BoardId, Ct))!.LastReport, StringComparison.Ordinal);
    }

    /// <summary>
    /// A new card goes to the lane of its board column (System.BoardColumn when the linked board
    /// agrees, else the state mapping), else the lane named like its state, else the on-demand
    /// "Azure DevOps" lane with a comment that never goes back to Azure DevOps.
    /// </summary>
    [Fact]
    public async Task NewCardsArePlacedOnce_ByBoardColumnThenStateMappingThenStateNameThenTheOverflowLane()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        _ado.Serve(
            Item(1, "Mapped by state", "User Story", "New"),
            Item(2, "Named column", "User Story", "Resolved", boardColumn: "Review"),
            Item(3, "Unmapped column", "Bug", "Active"),
            Item(4, "Named lane", "Task", "Build"),
            Item(5, "Nowhere", "Task", "Weird"),
            Item(6, "Disagreeing column", "User Story", "Active", boardColumn: "Review"));

        var report = await service.PullAsync(_project, saved.BoardId, false, Ct);

        Assert.Equal(6, report.Created);
        var lanes = (await _store.GetColumnsAsync(_project, Ct, saved.BoardId)).ToDictionary(lane => lane.Id, lane => lane.Name);
        var cards = (await _store.GetCardsAsync(_project, Ct, saved.BoardId)).ToDictionary(card => card.Title);
        Assert.Equal("Backlog", lanes[cards["Mapped by state"].ColumnId]);
        Assert.Equal("Review", lanes[cards["Named column"].ColumnId]);
        Assert.Equal("Azure DevOps", lanes[cards["Unmapped column"].ColumnId]);
        Assert.Equal("Build", lanes[cards["Named lane"].ColumnId]);
        Assert.Equal("Azure DevOps", lanes[cards["Nowhere"].ColumnId]);
        Assert.Equal("Azure DevOps", lanes[cards["Disagreeing column"].ColumnId]);
        Assert.Single(lanes.Values, name => name == "Azure DevOps");
        Assert.Equal(cards["Nowhere"].ColumnId, (await service.GetAsync(_project, saved.BoardId, Ct))!.OverflowColumnId);
        // The overflow lane is made first, before the lanes the board already had.
        var ordered = await _store.GetColumnsAsync(_project, Ct, saved.BoardId);
        Assert.Equal("Azure DevOps", ordered[0].Name);
        Assert.Equal(Enumerable.Range(0, ordered.Count), ordered.Select(lane => lane.Position));

        Assert.Empty((await _store.GetCardDetailAsync(_project, cards["Mapped by state"].Id, Ct))!.Comments);
        Assert.Empty((await _store.GetCardDetailAsync(_project, cards["Named lane"].Id, Ct))!.Comments);
        var column = Assert.Single((await _store.GetCardDetailAsync(_project, cards["Unmapped column"].Id, Ct))!.Comments);
        Assert.Equal("#3 is in the Azure DevOps column \"Active\", which has no lane on this board. Parked in Azure DevOps. Pick a lane for that column under Board settings, Azure DevOps, Advanced.", column.Body);
        Assert.Equal("Azure DevOps", column.Author.Label);
        var state = Assert.Single((await _store.GetCardDetailAsync(_project, cards["Nowhere"].Id, Ct))!.Comments);
        Assert.Contains("state \"Weird\", which matches no lane", state.Body, StringComparison.Ordinal);
        Assert.Empty(await _store.GetAdoDeliveriesAsync(_project, cards["Unmapped column"].Id, Ct));
    }

    [Fact]
    public async Task AnUpdatedWorkItemWinsOnItsFields_ButNeverMovesTheCard_AndAStateChangeIsOnlyNoted()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        _ado.Serve(Item(1, "Plan", "User Story", "New", description: "<p>Old</p>", tags: "a", points: 3, priority: 2));
        await service.PullAsync(_project, saved.BoardId, false, Ct);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, saved.BoardId));
        var build = (await _store.GetColumnsAsync(_project, Ct, saved.BoardId)).Single(lane => lane.Name == "Build");
        await _store.MoveCardAsync(_project, card.Id, build.Id, null, Ct);
        await _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Assignee: "claude"), Ct);

        _ado.Serve(Item(1, "Plan it", "User Story", "Active", rev: 2, description: "<p>New</p>", tags: "b; c", points: 5,
            priority: 1, boardColumn: "Active", assignee: "Grace"));
        var report = await service.PullAsync(_project, saved.BoardId, false, Ct);

        Assert.Equal((0, 1), (report.Created, report.Updated));
        var updated = (await _store.GetCardDetailAsync(_project, card.Id, Ct))!;
        Assert.Equal(("Plan it", "New", "critical", 5), (updated.Card.Title, updated.Card.Description, updated.Card.Priority, updated.Card.Points));
        Assert.Equal(["b", "c"], updated.Card.Tags);
        Assert.Equal(build.Id, updated.Card.ColumnId);
        Assert.Equal("claude", updated.Card.Assignee);
        var note = Assert.Single(updated.Comments);
        Assert.Equal("#1 is now \"Active\" in Azure DevOps (was \"New\"). The card was not moved: its lane and board are local.", note.Body);
        var link = (await _store.FindAdoLinkAsync(saved.Id, 1, Ct))!;
        Assert.Equal((2, "Grace"), (link.Rev, link.AssigneeDisplay));
        Assert.EndsWith(";status:Active", link.Mapping, StringComparison.Ordinal);
        Assert.Empty(await _store.GetAdoDeliveriesAsync(_project, card.Id, Ct));

        // A user comment on the same card does go to Azure DevOps.
        await _board.AddCommentAsync(_project, card.Id, BoardAuthor.User(), "Looks good", Ct);
        Assert.Single(await _store.GetAdoDeliveriesAsync(_project, card.Id, Ct));

        Assert.Equal(1, (await service.PullAsync(_project, saved.BoardId, false, Ct)).Skipped);
        Assert.Equal(2, (await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments.Count);
    }

    [Fact]
    public async Task ASoftDeletedCardIsSkipped_AndNeverRecreated()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        _ado.Serve(Item(1, "Gone soon", "Bug", "Weird"));
        await service.PullAsync(_project, saved.BoardId, false, Ct);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, saved.BoardId));
        Assert.True(await _store.DeleteCardAsync(_project, card.Id, Ct));

        _ado.Serve(Item(1, "Gone soon", "Bug", "Active", rev: 5));
        var report = await service.PullAsync(_project, saved.BoardId, false, Ct);

        Assert.Equal("0 created, 0 updated, 1 skipped, 0 failed. Skipped: 1 previously deleted in VibeRails.", report.Message);
        Assert.Empty(await _store.GetCardsAsync(_project, Ct, saved.BoardId));
        Assert.Equal(card.Id, (await _store.FindAdoLinkAsync(saved.Id, 1, Ct))!.CardId);
    }

    [Fact]
    public async Task TheBoardQuery_UsesTheBoardsTypesTeamScopeSkipOldDoneAndNarrowing_AndAsksOnlyForFieldsThatExist()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id,
            Save() with { NarrowWiql = " [System.Tags] CONTAINS 'ui' " }, Pat, Ct);
        Assert.Equal("[System.Tags] CONTAINS 'ui'", saved.NarrowWiql);
        Assert.True(saved.SkipOldDone);
        _ado.Serve(Item(1, "One", "User Story", "New"));

        await service.PullAsync(_project, saved.BoardId, false, Ct);

        var wiql = Assert.Single(_ado.Wiql);
        Assert.Contains("[System.WorkItemType] IN ('User Story', 'Bug')", wiql, StringComparison.Ordinal);
        Assert.Contains("[System.AreaPath] UNDER 'Web\\Core'", wiql, StringComparison.Ordinal);
        Assert.Contains("([System.WorkItemType] <> 'Bug' OR [System.State] <> 'Closed')", wiql, StringComparison.Ordinal);
        Assert.Contains("@today - 30", wiql, StringComparison.Ordinal);
        Assert.Contains(" AND ([System.Tags] CONTAINS 'ui') ORDER BY", wiql, StringComparison.Ordinal);
        Assert.Equal(("contoso", "Web", "Core"), _ado.LastScope);
        var fields = Assert.Single(_ado.RequestedFields);
        Assert.Contains(AdoFields.StoryPoints, fields);
        Assert.Contains(AdoFields.AcceptanceCriteria, fields);
        Assert.DoesNotContain(AdoFields.Effort, fields);
        Assert.DoesNotContain(AdoFields.Size, fields);
        Assert.All(_ado.Tokens, token => Assert.Equal(Pat, token));

        // A field this organization lacks is never requested: it would fail the whole batch.
        _ado.Fields = Fields(AdoFields.Priority, AdoFields.StoryPoints);
        await service.SaveAsync(_project, saved.BoardId, Save() with { SkipOldDone = false, NarrowWiql = "" }, null, Ct);
        await service.PullAsync(_project, saved.BoardId, false, Ct);
        Assert.DoesNotContain(AdoFields.AcceptanceCriteria, _ado.RequestedFields[^1]);
        Assert.DoesNotContain(AdoFields.BoardColumn, _ado.RequestedFields[^1]);
        Assert.DoesNotContain("@today", _ado.Wiql[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("CONTAINS 'ui'", _ado.Wiql[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedBoardConfiguration_FallsBackToStateNamesAndTheBacklogCategory()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        _ado.BoardOutcome = AdoCallOutcome.Forbidden;
        _ado.Serve(
            Item(1, "In review", "User Story", "Review", points: 8),
            Item(2, "Active", "User Story", "Active", boardColumn: "Review"));

        var report = await service.PullAsync(_project, saved.BoardId, false, Ct);

        Assert.Equal(("ok", 2), (report.Outcome, report.Created));
        var wiql = Assert.Single(_ado.Wiql);
        Assert.Contains("[System.WorkItemType] IN GROUP 'Microsoft.RequirementCategory'", wiql, StringComparison.Ordinal);
        Assert.DoesNotContain("@today", wiql, StringComparison.Ordinal);
        // The types weren't known ahead of the pull, so every estimate field is read and the
        // items' types pick one.
        Assert.Contains(AdoFields.Effort, _ado.RequestedFields[^1]);
        var lanes = (await _store.GetColumnsAsync(_project, Ct, saved.BoardId)).ToDictionary(lane => lane.Id, lane => lane.Name);
        var cards = (await _store.GetCardsAsync(_project, Ct, saved.BoardId)).ToDictionary(card => card.Title);
        Assert.Equal("Review", lanes[cards["In review"].ColumnId]);
        Assert.Equal(8, cards["In review"].Points);
        Assert.Equal("Azure DevOps", lanes[cards["Active"].ColumnId]);
    }

    [Fact]
    public async Task ATransientBoardConfigurationFailure_StopsThePullWithoutReadingWorkItems()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        _ado.BoardOutcome = AdoCallOutcome.Failed;
        _ado.Serve(Item(1, "One", "User Story", "New"));

        var report = await service.PullAsync(_project, saved.BoardId, false, Ct);

        Assert.Equal("failed", report.Outcome);
        Assert.EndsWith("The next interval tries again.", report.Message, StringComparison.Ordinal);
        Assert.Empty(_ado.Wiql);
        Assert.Empty(await _store.GetCardsAsync(_project, Ct, saved.BoardId));
    }

    [Fact]
    public async Task ASavedQueryIsPulledAsWritten_DetectingPointsFromItsTypes_AndRefusesNarrowing()
    {
        var service = Service();
        var source = await BoardWithLanes();
        await Assert.ThrowsAsync<AdoConfigException>(() =>
            service.SaveAsync(_project, source.Id, new AdoConnectionSave(QueryLink, true, NarrowWiql: "[System.Id] > 1"), Pat, Ct));
        var saved = await service.SaveAsync(_project, source.Id, new AdoConnectionSave(QueryLink, true, SkipOldDone: true), Pat, Ct);
        Assert.Equal((QueryId, null, null), (saved.QueryId, saved.Team, saved.SkipOldDone));
        Assert.Equal("Azure DevOps · Web", (await _store.GetBoardAsync(_project, saved.BoardId, Ct))!.Name);
        _ado.Serve(
            Item(1, "Scrum item", "Product Backlog Item", "Done", effort: 2),
            Item(2, "Committed", "Product Backlog Item", "Committed", effort: 13));

        var report = await service.PullAsync(_project, saved.BoardId, false, Ct);

        Assert.Equal(2, report.Created);
        Assert.Equal(1, _ado.SavedQueryRuns);
        Assert.Empty(_ado.Wiql);
        Assert.Equal(0, _ado.BoardCalls);
        Assert.Contains(AdoFields.StoryPoints, _ado.RequestedFields[^1]);
        Assert.Contains(AdoFields.Effort, _ado.RequestedFields[^1]);
        var lanes = (await _store.GetColumnsAsync(_project, Ct, saved.BoardId)).ToDictionary(lane => lane.Id, lane => lane.Name);
        var cards = (await _store.GetCardsAsync(_project, Ct, saved.BoardId)).ToDictionary(card => card.Title);
        Assert.Equal(("Done", 2), (lanes[cards["Scrum item"].ColumnId], cards["Scrum item"].Points));
        Assert.Equal(("Azure DevOps", 13), (lanes[cards["Committed"].ColumnId], cards["Committed"].Points));
        Assert.Equal("points:" + AdoFields.Effort + ";status:Done", (await _store.FindAdoLinkAsync(saved.Id, 1, Ct))!.Mapping);
    }

    /// <summary>
    /// Review R2: a saved query spanning an Agile and a Scrum project reads each item's own
    /// estimate, a bug-only result still gets points, and an item with no estimate keeps its card's.
    /// </summary>
    [Fact]
    public async Task ASavedQueryAcrossProcesses_ReadsEachItemsOwnEstimate()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, new AdoConnectionSave(QueryLink, true), Pat, Ct);
        var unestimated = Item(4, "Unestimated bug", "Bug", "New", points: 3);
        _ado.Serve(
            Item(1, "Agile story", "User Story", "New", points: 3),
            Item(2, "Scrum item", "Product Backlog Item", "New", effort: 8),
            Item(3, "Scrum bug", "Bug", "New", effort: 5),
            unestimated);
        await service.PullAsync(_project, saved.BoardId, false, Ct);
        var cards = (await _store.GetCardsAsync(_project, Ct, saved.BoardId)).ToDictionary(card => card.Title);
        Assert.Equal((3, 8, 5, 3), (cards["Agile story"].Points, cards["Scrum item"].Points, cards["Scrum bug"].Points, cards["Unestimated bug"].Points));

        _ado.Serve(unestimated with { Rev = 2, Estimates = new Dictionary<string, double>() });
        await service.PullAsync(_project, saved.BoardId, false, Ct);
        Assert.Equal(3, (await _store.FindCardAsync(_project, cards["Unestimated bug"].Id, Ct))!.Points);
    }

    [Fact]
    public async Task BatchesHoldAtMost200Ids_AndAPullReadsAtMost2000WorkItems()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        _ado.Serve(Enumerable.Range(1, 450).Select(id => Item(id, $"Item {id}", "User Story", "New")).ToArray());

        var some = await service.PullAsync(_project, saved.BoardId, dryRun: true, Ct);
        Assert.Equal(450, some.Created);
        Assert.Equal([200, 200, 50], _ado.BatchSizes);

        _ado.BatchSizes.Clear();
        _ado.Serve(Enumerable.Range(1, 2500).Select(id => Item(id, $"Item {id}", "User Story", "New")).ToArray());
        var capped = await service.PullAsync(_project, saved.BoardId, dryRun: true, Ct);
        Assert.Equal(2000, capped.Created);
        Assert.Equal(10, _ado.BatchSizes.Count);
        Assert.All(_ado.BatchSizes, size => Assert.Equal(200, size));
        Assert.EndsWith("Only the 2000 most recently changed work items were read.", capped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARateLimitOnlyStopsThePull_ABadQueryDisablesTheConnection_AndARefusedTokenExpiresIt()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        _ado.Serve(Item(1, "One", "User Story", "New"));

        _ado.IdsOutcome = AdoCallOutcome.RateLimited;
        var limited = await service.PullAsync(_project, saved.BoardId, false, Ct);
        Assert.Equal(("rate-limited", "Azure DevOps rate limited the pull. The next interval tries again."), (limited.Outcome, limited.Message));
        var after = (await service.GetAsync(_project, saved.BoardId, Ct))!;
        Assert.True(after.Enabled);
        Assert.Equal(BoardAdoAuthStatus.Saved, after.AuthStatus);

        _ado.IdsOutcome = AdoCallOutcome.BadQuery;
        _ado.IdsDetail = "TF51005: The query references a field that does not exist.";
        var bad = await service.PullAsync(_project, saved.BoardId, false, Ct);
        Assert.Equal("bad-query", bad.Outcome);
        after = (await service.GetAsync(_project, saved.BoardId, Ct))!;
        Assert.False(after.Enabled);
        Assert.Equal("TF51005: The query references a field that does not exist.", after.DisabledReason);

        // Saving again re-enables it.
        after = await service.SaveAsync(_project, saved.BoardId, Save(), null, Ct);
        Assert.True(after.Enabled);
        Assert.Null(after.DisabledReason);

        _ado.IdsOutcome = AdoCallOutcome.Unauthorized;
        _ado.IdsDetail = "Azure DevOps refused the personal access token.";
        Assert.Equal("expired", (await service.PullAsync(_project, saved.BoardId, false, Ct)).Outcome);
        Assert.Equal(BoardAdoAuthStatus.Expired, (await service.GetAsync(_project, saved.BoardId, Ct))!.AuthStatus);
        await Assert.ThrowsAsync<AdoConfigException>(() => service.PullAsync(_project, saved.BoardId, false, Ct));
        Assert.Empty(await _store.GetCardsAsync(_project, Ct, saved.BoardId));
    }

    [Fact]
    public async Task PullDue_OneFailingConnectionNeverStopsTheOthers_AndABusyLockPullsNothing()
    {
        var service = Service();
        var first = await BoardWithLanes();
        var second = await _store.CreateBoardAsync(_project, "Second", Ct);
        var failing = await service.SaveAsync(_project, first.Id, Save(), Pat, Ct);
        var working = await service.SaveAsync(_project, second.Id,
            Save("https://dev.azure.com/contoso/Ops/_boards/board/t/Core/Stories"), Pat, Ct);
        var expiredSource = await _store.CreateBoardAsync(_project, "Third", Ct);
        var expired = await service.SaveAsync(_project, expiredSource.Id,
            Save("https://dev.azure.com/contoso/Legacy/_boards/board/t/Core/Stories"), Pat, Ct);
        await _store.SaveAdoConnectionAsync((await _store.GetAdoConnectionAsync(_project, expired.BoardId, Ct))! with
        {
            AuthStatus = BoardAdoAuthStatus.Expired
        }, Ct);
        _secrets.SaveToken("ado_deadbeef0000", "orphan");
        _ado.FailingProjects.Add("Web");
        _ado.Serve(Item(1, "Ops work", "User Story", "New"));

        using (var held = CrossProcessFileLock.TryAcquire(LockPath))
        {
            Assert.NotNull(held);
            Assert.False(await service.PullDueAsync(Ct));
            Assert.Empty(_ado.Wiql);
        }

        Assert.True(await service.PullDueAsync(Ct));
        Assert.Single(await _store.GetCardsAsync(_project, Ct, working.BoardId));
        Assert.Empty(await _store.GetCardsAsync(_project, Ct, failing.BoardId));
        Assert.StartsWith("failed:", (await service.GetAsync(_project, failing.BoardId, Ct))!.LastReport, StringComparison.Ordinal);
        Assert.DoesNotContain(_ado.Scopes, scope => scope.Project == "Legacy");
        Assert.Null(_secrets.ReadToken("ado_deadbeef0000"));
        Assert.Equal(Pat, _secrets.ReadToken(working.Id));
    }

    [Fact]
    public async Task SaveNeverReturnsTheToken_AnotherOrganizationNeedsItAgain_AndAnotherProjectCarriesIt()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        Assert.True(saved.HasToken);
        Assert.Equal(BoardAdoAuthStatus.Saved, saved.AuthStatus);
        Assert.Matches("^ado_[0-9a-f]{12}$", saved.Id);
        Assert.DoesNotContain(Pat, saved.ToString(), StringComparison.Ordinal);

        var again = await service.SaveAsync(_project, saved.BoardId, new AdoConnectionSave(null, true), "  ", Ct);
        Assert.Equal((saved.Id, BoardLink), (again.Id, again.BoardLink));
        Assert.Equal(Pat, _secrets.ReadToken(saved.Id));

        await Assert.ThrowsAsync<AdoConfigException>(() => service.SaveAsync(_project, saved.BoardId,
            Save("https://dev.azure.com/fabrikam/Web/_boards/board/t/Core/Stories"), null, Ct));
        Assert.Equal(Pat, _secrets.ReadToken(saved.Id));

        var moved = await service.SaveAsync(_project, saved.BoardId, Save("https://dev.azure.com/contoso/Ops/_boards/board/t/Core/Stories"), null, Ct);
        Assert.NotEqual(saved.Id, moved.Id);
        Assert.Equal(saved.BoardId, moved.BoardId);
        Assert.Equal(Pat, _secrets.ReadToken(moved.Id));
        Assert.Null(_secrets.ReadToken(saved.Id));
        Assert.Contains("contoso/Web", moved.LastReport, StringComparison.Ordinal);

        var otherOrg = await service.SaveAsync(_project, saved.BoardId,
            Save("https://dev.azure.com/fabrikam/Ops/_boards/board/t/Core/Stories"), "fabrikam-pat", Ct);
        Assert.NotEqual(moved.Id, otherOrg.Id);
        Assert.Equal("fabrikam-pat", _secrets.ReadToken(otherOrg.Id));
        Assert.Null(_secrets.ReadToken(moved.Id));

        await Assert.ThrowsAsync<AdoConfigException>(() => service.SaveAsync(_project, saved.BoardId,
            Save() with { PointsField = "Story Points" }, Pat, Ct));
        await Assert.ThrowsAsync<AdoConfigException>(() => service.SaveAsync(_project, saved.BoardId,
            Save("https://contoso.visualstudio.com/Web/_boards/board/t/Core/Stories"), Pat, Ct));
    }

    [Fact]
    public async Task LanePicksBelongToOneBoard_AndAPointsOverrideReplacesDetection()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        await service.TestAsync(_project, saved.BoardId, Ct);
        var build = (await _store.GetColumnsAsync(_project, Ct, saved.BoardId)).Single(lane => lane.Name == "Build");

        var picked = await service.SaveAsync(_project, saved.BoardId,
            Save() with { ColumnMap = new Dictionary<string, string?> { ["Active"] = build.Id }, PointsField = "Custom.Points" }, null, Ct);
        Assert.Equal(build.Id, TrackerColumnMap.Parse(picked.ColumnMap).Single(column => column.Name == "Active").LaneId);
        _ado.Fields = Fields(AdoFields.StoryPoints, "Custom.Points");
        _ado.Serve(Item(1, "Doing", "Bug", "Active") with { Estimates = new Dictionary<string, double> { ["Custom.Points"] = 2 } });
        await service.PullAsync(_project, saved.BoardId, false, Ct);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, saved.BoardId));
        Assert.Equal((build.Id, 2), (card.ColumnId, card.Points));
        Assert.Contains("Custom.Points", _ado.RequestedFields[^1]);
        Assert.DoesNotContain(AdoFields.StoryPoints, _ado.RequestedFields[^1]);

        var other = await service.SaveAsync(_project, saved.BoardId, Save("https://dev.azure.com/contoso/Web/_boards/board/t/Core/Epics"), null, Ct);
        Assert.Equal(saved.Id, other.Id);
        Assert.Null(other.ColumnMap);
    }

    [Fact]
    public async Task UnlinkKeepsImportedWorkAndLinks_AndSaveTestAndUnlinkRespectTheLock()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        _ado.Serve(Item(1, "Keep me", "User Story", "New"));
        await service.PullAsync(_project, saved.BoardId, false, Ct);
        var card = Assert.Single(await _store.GetCardsAsync(_project, Ct, saved.BoardId));

        using (var held = CrossProcessFileLock.TryAcquire(LockPath))
        {
            Assert.NotNull(held);
            await Assert.ThrowsAsync<AdoConfigException>(() => service.UnlinkAsync(_project, saved.BoardId, Ct));
            await Assert.ThrowsAsync<AdoConfigException>(() => service.TestAsync(_project, saved.BoardId, Ct));
            await Assert.ThrowsAsync<AdoConfigException>(() => service.SaveAsync(_project, saved.BoardId, Save(), null, Ct));
            Assert.Equal("busy", (await service.PullAsync(_project, saved.BoardId, false, Ct)).Outcome);
        }

        Assert.False(await service.UnlinkAsync(_project + "-foreign", saved.BoardId, Ct));
        Assert.True(await service.UnlinkAsync(_project, saved.BoardId, Ct));
        Assert.True(await service.UnlinkAsync(_project, saved.BoardId, Ct));
        Assert.Null(await service.GetAsync(_project, saved.BoardId, Ct));
        Assert.Null(_secrets.ReadToken(saved.Id));
        Assert.Equal(card.Id, Assert.Single(await _store.GetCardsAsync(_project, Ct, saved.BoardId)).Id);
        Assert.Equal(card.Id, (await _store.FindAdoLinkAsync(saved.Id, 1, Ct))!.CardId);
        await Assert.ThrowsAsync<AdoConfigException>(() => service.PullAsync(_project, saved.BoardId, false, Ct));
    }

    [Fact]
    public async Task TestReadsTheBoard_SavingItsNameAndColumns_AndCountsItsWorkItems()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, Save(), Pat, Ct);
        _ado.Serve(Item(1, "One", "User Story", "New"), Item(2, "Two", "Bug", "New"));

        var report = await service.TestAsync(_project, saved.BoardId, Ct);

        Assert.True(report.Ok, report.Error);
        Assert.Equal("Web", report.Project);
        var summary = report.Board!;
        Assert.Equal(("Stories", "Core", "Stories", 2, false), (summary.Name, summary.Team, summary.BacklogLevel, summary.WorkItemCount, summary.CountCapped));
        Assert.Equal(AdoFields.StoryPoints, summary.PointsField);
        Assert.Equal(["New", "Active", "Review", "Closed"], summary.Columns.Select(column => column.Name));
        Assert.Equal(["Backlog", null, "Review", "Done"], summary.Columns.Select(column => column.LaneName));
        Assert.Empty(summary.Warnings);
        Assert.Empty(_ado.BatchSizes);
        var connection = (await service.GetAsync(_project, saved.BoardId, Ct))!;
        Assert.Equal("Stories", connection.AdoBoardName);
        Assert.NotNull(connection.LastTestedUtc);
        Assert.Equal(["New", "Active", "Review", "Closed"], TrackerColumnMap.Parse(connection.ColumnMap).Select(column => column.Name));
        var details = await service.GetDetailsAsync(_project, saved.BoardId, Ct);
        Assert.Equal(["Backlog", null, "Review", "Done"], details.Columns.Select(column => column.LaneName));

        _ado.BoardOutcome = AdoCallOutcome.NotFound;
        var degraded = await service.TestAsync(_project, saved.BoardId, Ct);
        Assert.True(degraded.Ok);
        Assert.Contains("Lanes match state names", Assert.Single(degraded.Board!.Warnings), StringComparison.Ordinal);
        Assert.Equal(4, TrackerColumnMap.Parse((await service.GetAsync(_project, saved.BoardId, Ct))!.ColumnMap).Count);

        _ado.TestOutcome = AdoCallOutcome.Unauthorized;
        var refused = await service.TestAsync(_project, saved.BoardId, Ct);
        Assert.False(refused.Ok);
        Assert.Equal(BoardAdoAuthStatus.Expired, (await service.GetAsync(_project, saved.BoardId, Ct))!.AuthStatus);
    }

    [Fact]
    public async Task TestOfASavedQuery_ReadsItsName_AndAFolderIsAnError()
    {
        var service = Service();
        var saved = await service.SaveAsync(_project, (await BoardWithLanes()).Id, new AdoConnectionSave(QueryLink, true), Pat, Ct);
        _ado.Serve(Item(1, "One", "Bug", "New"));

        var report = await service.TestAsync(_project, saved.BoardId, Ct);
        Assert.True(report.Ok, report.Error);
        Assert.Equal(("My bugs", "flat", 1), (report.Board!.Name, report.Board.QueryType, report.Board.WorkItemCount));
        Assert.Empty(report.Board.Columns);
        Assert.Equal("My bugs", (await service.GetAsync(_project, saved.BoardId, Ct))!.AdoBoardName);
        Assert.Equal("Azure DevOps · My bugs", (await _store.GetBoardAsync(_project, saved.BoardId, Ct))!.Name);

        _ado.Query = _ado.Query with { IsFolder = true };
        var folder = await service.TestAsync(_project, saved.BoardId, Ct);
        Assert.False(folder.Ok);
        Assert.Contains("query folder", folder.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectingSavedQueriesUsesDistinctNames_AndRetestingPreservesACustomBoardName()
    {
        var service = Service();
        var source = await BoardWithLanes();
        var first = await service.SaveAsync(_project, source.Id, new AdoConnectionSave(QueryLink, true), Pat, Ct);
        await service.TestAsync(_project, first.BoardId, Ct);

        const string otherQueryId = "0b7a8a3e-5d5b-4f0a-9a43-5a8a2c1e6f10";
        _ado.Query = _ado.Query with { Id = otherQueryId, Name = "Open features" };
        var otherLink = QueryLink.Replace(QueryId, otherQueryId, StringComparison.Ordinal);
        var second = await service.SaveAsync(_project, source.Id, new AdoConnectionSave(otherLink, true), Pat, Ct);
        await service.TestAsync(_project, second.BoardId, Ct);

        Assert.Equal("Azure DevOps · My bugs", (await _store.GetBoardAsync(_project, first.BoardId, Ct))!.Name);
        Assert.Equal("Azure DevOps · Open features", (await _store.GetBoardAsync(_project, second.BoardId, Ct))!.Name);
        await _store.RenameBoardAsync(_project, first.BoardId, "My triage", null, Ct);
        _ado.Query = _ado.Query with { Id = QueryId, Name = "Renamed remote query" };
        await service.TestAsync(_project, first.BoardId, Ct);
        Assert.Equal("My triage", (await _store.GetBoardAsync(_project, first.BoardId, Ct))!.Name);
    }

    private string LockPath => Path.Combine(_root, AdoPullLock.FileName);

    private AdoPullService Service() => new(_store, _board, _ado, _secrets, new AdoPullLock(LockPath));

    private async Task<BoardRecord> BoardWithLanes()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        return (await _store.GetBoardsAsync(_project, Ct))[0];
    }

    private static AdoConnectionSave Save(string link = BoardLink) => new(link, true);

    private static AdoFieldSet Fields(params string[] names) =>
        new(new HashSet<string>(names.Concat(AdoFields.Always), StringComparer.OrdinalIgnoreCase));

    private static AdoWorkItem Item(
        int id, string title, string type, string state, int rev = 1, string? description = "<p>body</p>", string? tags = "one",
        double? points = null, double? effort = null, int? priority = 2, string? boardColumn = null, string assignee = "Ada")
    {
        var estimates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (points is double storyPoints) estimates[AdoFields.StoryPoints] = storyPoints;
        if (effort is double effortPoints) estimates[AdoFields.Effort] = effortPoints;
        return new AdoWorkItem(id, rev, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(rev), title, type, state,
            description, null, null, priority, tags, assignee, boardColumn, estimates);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class MemoryAdoSecrets : IAdoSecretStore
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
        public bool HasToken(string connectionId) => _tokens.ContainsKey(connectionId);
        public string? ReadToken(string connectionId) => _tokens.TryGetValue(connectionId, out var token) ? token : null;
        public void SaveToken(string connectionId, string token) => _tokens[connectionId] = token;
        public void DeleteToken(string connectionId) => _tokens.Remove(connectionId);

        public async Task PruneAsync(Func<CancellationToken, Task<IReadOnlyCollection<string>>> liveConnectionIds, CancellationToken cancellationToken)
        {
            var live = (await liveConnectionIds(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _tokens.Keys.Where(id => !live.Contains(id)).ToList())
                _tokens.Remove(id);
        }
    }

    /// <summary>
    /// A Stories board: New (incoming), Active and Review (in progress), Closed (outgoing), for
    /// User Story and Bug, on the Web\Core area. Serves whatever work items a test puts in.
    /// </summary>
    internal sealed class ScriptedAdo : IAzureDevOpsClient
    {
        public AdoCallOutcome TestOutcome { get; set; } = AdoCallOutcome.Ok;
        public AdoCallOutcome BoardOutcome { get; set; } = AdoCallOutcome.Ok;
        public AdoBoardConfiguration Board { get; set; } = new("Stories",
        [
            Column("New", "incoming", ("User Story", "New"), ("Bug", "New")),
            Column("Active", "inProgress", ("User Story", "Active"), ("Bug", "Active")),
            Column("Review", "inProgress", ("User Story", "Resolved"), ("Bug", "Resolved")),
            Column("Closed", "outgoing", ("User Story", "Closed"), ("Bug", "Closed"))
        ]);
        public AdoTeamFieldValues Team { get; set; } = new("System.AreaPath", [new AdoTeamFieldValue("Web\\Core", true)]);
        public AdoFieldSet Fields { get; set; } = AdoPullServiceTests.Fields(
            AdoFields.BoardColumn, AdoFields.Priority, AdoFields.ReproSteps, AdoFields.AcceptanceCriteria,
            AdoFields.StoryPoints, AdoFields.Effort, AdoFields.Size);
        public AdoQuery Query { get; set; } = new(QueryId, "My bugs", "flat", false);
        public AdoCallOutcome IdsOutcome { get; set; } = AdoCallOutcome.Ok;
        public string? IdsDetail { get; set; }
        public HashSet<string> FailingProjects { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Wiql { get; } = [];
        public List<IReadOnlyList<string>> RequestedFields { get; } = [];
        public List<int> BatchSizes { get; } = [];
        public List<string> Tokens { get; } = [];
        public List<(string Organization, string Project, string Team)> Scopes { get; } = [];
        public (string, string, string) LastScope => Scopes[^1];
        public int SavedQueryRuns { get; private set; }
        public int BoardCalls { get; private set; }

        private List<int> _ids = [];
        private Dictionary<int, AdoWorkItem> _items = [];

        public void Serve(params AdoWorkItem[] items)
        {
            _ids = items.Select(item => item.Id).ToList();
            _items = items.ToDictionary(item => item.Id);
        }

        public Task<AdoCallResult<AdoProject>> TestAsync(string organization, string project, string token, CancellationToken cancellationToken)
        {
            Tokens.Add(token);
            return Task.FromResult(TestOutcome == AdoCallOutcome.Ok
                ? new AdoCallResult<AdoProject>(AdoCallOutcome.Ok, new AdoProject("p1", project), null, null)
                : new AdoCallResult<AdoProject>(TestOutcome, null, "Azure DevOps refused the personal access token.", null));
        }

        public Task<AdoCallResult<AdoBoardConfiguration>> GetBoardAsync(
            string organization, string project, string team, string backlogLevel, string token, CancellationToken cancellationToken)
        {
            BoardCalls++;
            Tokens.Add(token);
            return Task.FromResult(BoardOutcome == AdoCallOutcome.Ok
                ? new AdoCallResult<AdoBoardConfiguration>(AdoCallOutcome.Ok, Board, null, null)
                : new AdoCallResult<AdoBoardConfiguration>(BoardOutcome, null, $"Azure DevOps did not let this token read the {backlogLevel} board.", null));
        }

        public Task<AdoCallResult<AdoTeamFieldValues>> GetTeamFieldValuesAsync(
            string organization, string project, string team, string token, CancellationToken cancellationToken)
        {
            Tokens.Add(token);
            return Task.FromResult(new AdoCallResult<AdoTeamFieldValues>(AdoCallOutcome.Ok, Team, null, null));
        }

        public Task<AdoCallResult<AdoFieldSet>> GetFieldsAsync(string organization, string token, CancellationToken cancellationToken)
        {
            Tokens.Add(token);
            return Task.FromResult(new AdoCallResult<AdoFieldSet>(AdoCallOutcome.Ok, Fields, null, null));
        }

        public Task<AdoCallResult<AdoQuery>> GetQueryAsync(
            string organization, string project, string queryId, string token, CancellationToken cancellationToken) =>
            Task.FromResult(new AdoCallResult<AdoQuery>(AdoCallOutcome.Ok, Query, null, null));

        public Task<AdoCallResult<AdoWorkItemIds>> QueryIdsAsync(
            string organization, string project, string team, string wiql, string token, CancellationToken cancellationToken)
        {
            Wiql.Add(wiql);
            Scopes.Add((organization, project, team));
            Tokens.Add(token);
            return Task.FromResult(Ids(project));
        }

        public Task<AdoCallResult<AdoWorkItemIds>> RunSavedQueryAsync(
            string organization, string project, string queryId, string token, CancellationToken cancellationToken)
        {
            SavedQueryRuns++;
            Assert.Equal(Query.Id, queryId);
            Tokens.Add(token);
            return Task.FromResult(Ids(project));
        }

        public Task<AdoCallResult<IReadOnlyList<AdoWorkItem>>> GetWorkItemsAsync(
            string organization, string project, IReadOnlyList<int> ids, IReadOnlyList<string> fields, string token,
            CancellationToken cancellationToken)
        {
            Assert.InRange(ids.Count, 1, AzureDevOpsClient.BatchSize);
            BatchSizes.Add(ids.Count);
            RequestedFields.Add(fields);
            Tokens.Add(token);
            IReadOnlyList<AdoWorkItem> items = ids.Where(_items.ContainsKey).Select(id => _items[id]).ToList();
            return Task.FromResult(new AdoCallResult<IReadOnlyList<AdoWorkItem>>(AdoCallOutcome.Ok, items, null, null));
        }

        private AdoCallResult<AdoWorkItemIds> Ids(string project)
        {
            if (FailingProjects.Contains(project))
                return new(AdoCallOutcome.Failed, null, "Azure DevOps returned 503 for the board query.", null);
            return IdsOutcome == AdoCallOutcome.Ok
                ? new(AdoCallOutcome.Ok, new AdoWorkItemIds(_ids), null, null)
                : new(IdsOutcome, null, IdsDetail, TimeSpan.FromSeconds(30));
        }

        private static AdoBoardColumn Column(string name, string type, params (string Type, string State)[] mappings) =>
            new(name, type, mappings.ToDictionary(pair => pair.Type, pair => pair.State, StringComparer.OrdinalIgnoreCase), false);
    }
}
