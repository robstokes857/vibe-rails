using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// The board manager's storage (VIBE-52 follow-up, VIBE-204): the AI board manager switch,
/// revisioned settings with standing orders and a worker limit, the board-aware rule for Agent cards
/// without their own manager, manager launches, the event stream (scoped, without the caller's own
/// writes, with completion reports), the Agent-card snapshot and the managed workers, over a real
/// temp board.db.
/// </summary>
public sealed class BoardManagerStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-manager-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly BoardStore _store;
    private readonly string _project;

    public BoardManagerStoreTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project");
        _connectionString = $"Data Source={Path.Combine(_root, "board.db")};Mode=ReadWriteCreate;Cache=Shared;Pooling=False";
        _store = new BoardStore(_connectionString, _connectionString);
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NewBoardCard NewCard(string title, string? workflow = null, string? manager = null, string? boardId = null, bool flagged = false) =>
        new(null, title, "Scope", "base:claude", "medium", null, [], false, BoardId: boardId, Flagged: flagged,
            WorkflowMode: workflow ?? BoardWorkflowModes.Manual, ManagerSelection: manager);

    private async Task<string> DefaultBoardAsync(bool enabled = true)
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        if (enabled) await _store.SetBoardManagerEnabledAsync(true, Ct);
        return (await _store.GetBoardsAsync(_project, Ct)).Single().Id;
    }

    [Fact]
    public async Task Switch_IsOffUntilSaved_AndPersists()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        Assert.False(await _store.GetBoardManagerEnabledAsync(Ct));
        Assert.True(await _store.SetBoardManagerEnabledAsync(true, Ct));
        Assert.True(await new BoardStore(_connectionString, _connectionString).GetBoardManagerEnabledAsync(Ct));
        Assert.False(await _store.SetBoardManagerEnabledAsync(false, Ct));
        Assert.False(await _store.GetBoardManagerEnabledAsync(Ct));
        Assert.True(await _store.SetBoardManagerEnabledAsync(true, Ct));
    }

    [Fact]
    public async Task Settings_AreRevisioned_KeepStandingOrders_ClearRemovesTheRow_AndForeignBoardsAreNotFound()
    {
        var boardId = await DefaultBoardAsync();
        var none = (await _store.GetBoardManagerAsync(_project, boardId, Ct))!;
        Assert.False(none.Settings.IsConfigured); Assert.Equal(0, none.Revision); Assert.Equal(1, none.Settings.MaxWorkers);

        var options = new BaseLlmOptions(Model: "gpt-6-astra", Effort: "high");
        var saved = (await _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", options, "  Bugs before features.  ", 2), 0, Ct))!;
        Assert.Equal(1, saved.Revision); Assert.Equal("base:codex", saved.Settings.Selection); Assert.Equal(options, saved.Settings.BaseLlmOptions);
        Assert.Equal("Bugs before features.", saved.Settings.Instructions); Assert.Equal(2, saved.Settings.MaxWorkers);
        var reopened = (await new BoardStore(_connectionString, _connectionString).GetBoardManagerAsync(_project, boardId, Ct))!;
        Assert.Equal(saved, reopened);

        await Assert.ThrowsAsync<BoardConflictException>(() => _store.SaveBoardManagerAsync(_project, boardId, new("base:claude", null), 0, Ct));
        Assert.Equal("base:codex", (await _store.GetBoardManagerAsync(_project, boardId, Ct))!.Settings.Selection);
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", null, null, 0), 1, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.SaveBoardManagerAsync(_project, boardId,
            new("base:codex", null, null, BoardManagerSettings.MaxWorkersLimit + 1), 1, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.SaveBoardManagerAsync(_project, boardId,
            new("base:codex", null, new string('x', BoardManagerSettings.MaxInstructionsLength + 1)), 1, Ct));
        // Blank standing orders are stored as none.
        Assert.Null((await _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", null, "   "), 1, Ct))!.Settings.Instructions);

        var cleared = (await _store.SaveBoardManagerAsync(_project, boardId, BoardManagerSettings.None, 2, Ct))!;
        Assert.False(cleared.Settings.IsConfigured); Assert.Equal(0, cleared.Revision);
        Assert.Equal(0, (await _store.GetBoardManagerAsync(_project, boardId, Ct))!.Revision);

        Assert.Null(await _store.GetBoardManagerAsync(_project + "-other", boardId, Ct));
        Assert.Null(await _store.SaveBoardManagerAsync(_project + "-other", boardId, new("base:codex", null), 0, Ct));
        Assert.Null(await _store.RecordBoardManagerSessionAsync(_project + "-other", boardId, Guid.NewGuid().ToString(), "tab", "base:codex", "codex", Ct));
    }

    [Fact]
    public async Task ARowTheFirstBuildWrote_ReadsWithNoStandingOrdersAndOneWorker()
    {
        var boardId = await DefaultBoardAsync();
        // board-agent-workflow/2 binaries write only the original columns.
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO BoardManagers (BoardId, Selection, OptionsJson, Revision, UpdatedUTC) VALUES ($id, 'base:claude', NULL, 3, '2026-10-23T14:24:03.4492483Z');";
            command.Parameters.AddWithValue("$id", boardId);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var record = (await _store.GetBoardManagerAsync(_project, boardId, Ct))!;
        Assert.Equal(3, record.Revision);
        Assert.Equal(new BoardManagerSettings("base:claude", null, null, 1), record.Settings);
    }

    [Fact]
    public async Task AgentCardWithoutItsOwnManager_NeedsTheBoardManager_AndOrdinaryEditsSurviveItsRemoval()
    {
        var boardId = await DefaultBoardAsync();
        var missing = await Assert.ThrowsAsync<BoardValidationException>(() => _store.CreateCardAsync(_project, NewCard("Orphan", BoardWorkflowModes.Agent), Ct));
        Assert.Contains("Board settings > Manager", missing.Message);
        var own = await _store.CreateCardAsync(_project, NewCard("Own manager", BoardWorkflowModes.Agent, "base:codex"), Ct);

        await _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", null), 0, Ct);
        var managed = await _store.CreateCardAsync(_project, NewCard("Board managed", BoardWorkflowModes.Agent), Ct);
        Assert.Null(managed.ManagerSelection); Assert.Equal(BoardWorkflowModes.Agent, managed.WorkflowMode);
        // A card may hand itself to the board manager by clearing its own.
        var handed = (await _store.UpdateCardAsync(_project, own.Id, new BoardCardPatch(ClearManagerSelection: true), Ct))!;
        Assert.Null(handed.ManagerSelection);

        // Removing the board manager leaves the cards as they are; a title edit must still save,
        // while switching a Manual card to Agent without any manager is refused again.
        await _store.SaveBoardManagerAsync(_project, boardId, BoardManagerSettings.None, 1, Ct);
        Assert.Equal("Renamed", (await _store.UpdateCardAsync(_project, managed.Id, new BoardCardPatch(Title: "Renamed"), Ct))!.Title);
        var manual = await _store.CreateCardAsync(_project, NewCard("Manual"), Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.UpdateCardAsync(_project, manual.Id, new BoardCardPatch(WorkflowMode: BoardWorkflowModes.Agent), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.UpdateCardAsync(_project, managed.Id, new BoardCardPatch(ClearManagerSelection: true), Ct));
    }

    [Fact]
    public async Task WithTheSwitchOff_AnAgentCardNeedsItsOwnManager_AsBeforeBoardManagers()
    {
        var boardId = await DefaultBoardAsync();
        await _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", null), 0, Ct);
        var managed = await _store.CreateCardAsync(_project, NewCard("Board managed", BoardWorkflowModes.Agent), Ct);

        await _store.SetBoardManagerEnabledAsync(false, Ct);
        var refused = await Assert.ThrowsAsync<BoardValidationException>(() => _store.CreateCardAsync(_project, NewCard("New agent card", BoardWorkflowModes.Agent), Ct));
        Assert.Equal("Select a manager agent for the Agent workflow.", refused.Message);
        var manual = await _store.CreateCardAsync(_project, NewCard("Manual"), Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.UpdateCardAsync(_project, manual.Id, new BoardCardPatch(WorkflowMode: BoardWorkflowModes.Agent), Ct));
        // A card saved while it was on keeps saving ordinary edits; giving it a manager is always allowed.
        Assert.Equal("Still editable", (await _store.UpdateCardAsync(_project, managed.Id, new BoardCardPatch(Title: "Still editable"), Ct))!.Title);
        Assert.Equal("base:claude", (await _store.UpdateCardAsync(_project, managed.Id, new BoardCardPatch(ManagerSelection: "base:claude"), Ct))!.ManagerSelection);
        Assert.NotNull(await _store.CreateCardAsync(_project, NewCard("Own manager", BoardWorkflowModes.Agent, "base:codex"), Ct));
    }

    [Fact]
    public async Task Sessions_AreNewestFirst_AndScopedToTheBoard()
    {
        var boardId = await DefaultBoardAsync();
        var other = await _store.CreateBoardAsync(_project, "Other", Ct);
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        Assert.NotNull(await _store.RecordBoardManagerSessionAsync(_project, boardId, first, "tab-1", "base:codex", "codex", Ct));
        await Task.Delay(5, Ct);
        Assert.NotNull(await _store.RecordBoardManagerSessionAsync(_project, boardId, second, "tab-2", "base:codex", "codex", Ct));
        Assert.NotNull(await _store.RecordBoardManagerSessionAsync(_project, other.Id, Guid.NewGuid().ToString(), "tab-3", "base:claude", "claude", Ct));

        var sessions = await _store.GetBoardManagerSessionsAsync(_project, boardId, 10, Ct);
        Assert.Equal([second, first], sessions.Select(session => session.SessionId));
        Assert.Equal("tab-2", sessions[0].TabId); Assert.Equal("codex", sessions[0].Cli);
        Assert.Single(await _store.GetBoardManagerSessionsAsync(_project, boardId, 1, Ct));
        Assert.Single(await _store.GetBoardManagerSessionsAsync(_project, other.Id, 10, Ct));
    }

    [Fact]
    public async Task Events_FollowTheCardLogAfterTheCursor_ForOneBoardOnly()
    {
        var boardId = await DefaultBoardAsync();
        var other = await _store.CreateBoardAsync(_project, "Other", Ct);
        var before = DateTime.UtcNow.AddSeconds(-1);
        var card = await _store.CreateCardAsync(_project, NewCard("Watched"), Ct);
        var foreign = await _store.CreateCardAsync(_project, NewCard("Elsewhere", boardId: other.Id), Ct);
        await _store.AddCommentAsync(_project, foreign.Id, BoardAuthor.User(), "Not on this board", Ct);
        var comment = (await _store.AddCommentAsync(_project, card.Id, BoardAuthor.Agent("Worker", "codex", null), "Implemented the thing", Ct))!;
        var review = (await _store.GetColumnsAsync(_project, Ct, boardId)).First(column => column.Name == "Review");
        await _store.MoveCardAsync(_project, card.Id, review.Id, null, Ct);

        var events = await _store.GetBoardEventsAsync(_project, boardId, before, 100, cancellationToken: Ct);
        Assert.All(events, entry => Assert.Equal(card.Key, entry.CardKey));
        Assert.DoesNotContain(events, entry => entry.Summary.Contains("Not on this board"));
        Assert.Equal(["created", "comment", "change"], events.Select(entry => entry.Kind));
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Utc <= pair.Second.Utc));
        var moved = events[^1];
        Assert.Equal("Review", moved.LaneName);
        Assert.Contains("Review", moved.Summary);
        Assert.Equal("agent", events[1].AuthorKind); Assert.Equal("Worker", events[1].AuthorLabel);

        // The cursor is exclusive: everything at or before the comment stays behind it.
        var after = await _store.GetBoardEventsAsync(_project, boardId, comment.CreatedUtc, 100, cancellationToken: Ct);
        Assert.Equal(["change"], after.Select(entry => entry.Kind));
        Assert.Single(await _store.GetBoardEventsAsync(_project, boardId, before, 1, cancellationToken: Ct));
        Assert.Empty(await _store.GetBoardEventsAsync(_project, boardId, DateTime.UtcNow.AddMinutes(1), 100, cancellationToken: Ct));
        Assert.Empty(await _store.GetBoardEventsAsync(_project + "-other", boardId, before, 100, cancellationToken: Ct));
    }

    [Fact]
    public async Task ManagedScope_KeepsTheManagersCards_AndWorkflowChangesOnAnyCard_WithoutTheCallersOwnWrites()
    {
        var boardId = await DefaultBoardAsync();
        await _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", null), 0, Ct);
        var manual = await _store.CreateCardAsync(_project, NewCard("Manual"), Ct);
        var own = await _store.CreateCardAsync(_project, NewCard("Own manager", BoardWorkflowModes.Agent, "base:claude"), Ct);
        var managed = await _store.CreateCardAsync(_project, NewCard("Managed", BoardWorkflowModes.Agent), Ct);
        var leaving = await _store.CreateCardAsync(_project, NewCard("Leaving", BoardWorkflowModes.Agent), Ct);
        var cursor = DateTime.UtcNow;
        await Task.Delay(5, Ct);
        var manager = Guid.NewGuid().ToString();
        await _store.AddCommentAsync(_project, manual.Id, BoardAuthor.User(), "Human note on a manual card", Ct);
        await _store.AddCommentAsync(_project, own.Id, BoardAuthor.User(), "Someone else's card", Ct);
        await _store.AddCommentAsync(_project, managed.Id, BoardAuthor.Agent("Board manager", "codex", manager), "My own checkpoint", Ct);
        await _store.AddCommentAsync(_project, managed.Id, BoardAuthor.Agent("Worker", "claude", Guid.NewGuid().ToString()), "Worker handoff", Ct);
        await _store.UpdateCardAsync(_project, leaving.Id, new BoardCardPatch(WorkflowMode: BoardWorkflowModes.Manual, ClearManagerSelection: true), Ct);

        var all = await _store.GetBoardEventsAsync(_project, boardId, cursor, 100, cancellationToken: Ct);
        Assert.Equal(5, all.Count);
        var scoped = await _store.GetBoardEventsAsync(_project, boardId, cursor, 100, managedOnly: true, excludeSessionId: manager, cancellationToken: Ct);
        Assert.Equal([managed.Key, leaving.Key], scoped.Select(entry => entry.CardKey));
        Assert.Equal("Worker handoff", scoped[0].Summary);
        Assert.Equal("change", scoped[1].Kind);
        Assert.Contains(scoped, entry => entry.CardKey == leaving.Key && entry.Summary.Contains("Manual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Events_CarryCompletionReports_Attention_AndACardsDeletion()
    {
        var boardId = await DefaultBoardAsync();
        await _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", null), 0, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Managed", BoardWorkflowModes.Agent), Ct);
        var doomed = await _store.CreateCardAsync(_project, NewCard("Doomed", BoardWorkflowModes.Agent), Ct);
        var worker = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_project, card.Id, worker, "tab-w", "base:claude", "claude", "Worker", BoardSessionRecord.WorkerOrigin, Ct);
        var cursor = DateTime.UtcNow;
        await Task.Delay(5, Ct);
        await _store.UpdateCardAsync(_project, card.Id, new BoardCardPatch(Flagged: true, FlagReason: "Need the API key"), Ct,
            BoardAuthor.Agent("Worker", "claude", worker));
        await _store.CompleteAgentAsync(_project, card.Id, worker, "succeeded", "Tests green", Ct);
        await _store.DeleteCardAsync(_project, doomed.Id, Ct);

        var events = await _store.GetBoardEventsAsync(_project, boardId, cursor, 100, managedOnly: true, cancellationToken: Ct);
        Assert.Contains(events, entry => entry.Kind == "comment" && entry.IsAttention && entry.Summary.Contains("Need the API key"));
        var completion = Assert.Single(events, entry => entry.Kind == "completion");
        Assert.Equal(card.Key, completion.CardKey);
        Assert.Equal("succeeded: Tests green", completion.Summary);
        Assert.Contains(events, entry => entry.Kind == "deleted" && entry.CardKey == doomed.Key);
        // The worker's own call excludes its own report and comment.
        Assert.DoesNotContain(await _store.GetBoardEventsAsync(_project, boardId, cursor, 100, managedOnly: true, excludeSessionId: worker, cancellationToken: Ct),
            entry => entry.Kind == "completion");
    }

    [Fact]
    public async Task AFullBatch_NeverSplitsAGroupOfEntriesThatShareAStamp()
    {
        var boardId = await DefaultBoardAsync();
        var card = await _store.CreateCardAsync(_project, NewCard("Busy"), Ct);
        var cursor = DateTime.UtcNow;
        await Task.Delay(5, Ct);
        var first = (await _store.AddCommentAsync(_project, card.Id, BoardAuthor.User(), "first", Ct))!;
        var shared = first.CreatedUtc.AddMilliseconds(10);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            foreach (var body in new[] { "same-a", "same-b" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, Body, CreatedUTC, Kind) VALUES ($id, $card, 'user', 'You', $body, $at, 'comment');";
                command.Parameters.AddWithValue("$id", "cm_" + Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$card", card.Id);
                command.Parameters.AddWithValue("$body", body);
                command.Parameters.AddWithValue("$at", shared.ToString("O"));
                await command.ExecuteNonQueryAsync(Ct);
            }
        }
        // Two fit, but the second shares its stamp with the third: the batch stops after "first".
        var batch = await _store.GetBoardEventsAsync(_project, boardId, cursor, 2, cancellationToken: Ct);
        Assert.Equal(["first"], batch.Select(entry => entry.Summary));
        var rest = await _store.GetBoardEventsAsync(_project, boardId, batch[^1].Utc, 2, cancellationToken: Ct);
        Assert.Equal(["same-a", "same-b"], rest.Select(entry => entry.Summary));
        // One stamp throughout still advances instead of returning nothing forever.
        Assert.Equal(2, (await _store.GetBoardEventsAsync(_project, boardId, first.CreatedUtc, 1, cancellationToken: Ct)).Count);
    }

    [Fact]
    public async Task ManagedCards_ListAgentCardsInLaneOrder_FlaggedFirst_WithDiscussionAsActivity()
    {
        var boardId = await DefaultBoardAsync();
        await _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", null), 0, Ct);
        var lanes = (await _store.GetColumnsAsync(_project, Ct, boardId)).OrderBy(lane => lane.Position).ToList();
        await _store.CreateCardAsync(_project, NewCard("Manual card"), Ct);
        var own = await _store.CreateCardAsync(_project, NewCard("Own manager", BoardWorkflowModes.Agent, "base:claude"), Ct);
        var quiet = await _store.CreateCardAsync(_project, NewCard("Quiet", BoardWorkflowModes.Agent), Ct);
        var flagged = await _store.CreateCardAsync(_project, NewCard("Urgent", BoardWorkflowModes.Agent, flagged: true), Ct);
        var later = await _store.CreateCardAsync(_project, NewCard("Later lane", BoardWorkflowModes.Agent), Ct);
        await _store.MoveCardAsync(_project, later.Id, lanes[2].Id, null, Ct);
        await Task.Delay(5, Ct);
        var comment = (await _store.AddCommentAsync(_project, quiet.Id, BoardAuthor.User(), "Still thinking", Ct))!;

        var cards = await _store.GetManagedCardsAsync(_project, boardId, Ct);
        Assert.Equal(new HashSet<string> { flagged.Id, own.Id, quiet.Id, later.Id }, cards.Select(entry => entry.Card.Id).ToHashSet());
        Assert.Equal(flagged.Id, cards[0].Card.Id);
        Assert.Equal(later.Id, cards[^1].Card.Id); Assert.Equal(lanes[2].Name, cards[^1].LaneName);
        Assert.All(cards.Take(3), entry => Assert.Equal(lanes[0].Name, entry.LaneName));
        Assert.True(cards.Single(entry => entry.Card.Id == own.Id).OwnManager);
        Assert.False(cards.Single(entry => entry.Card.Id == quiet.Id).OwnManager);
        // A comment is activity: it also touches the card a moment later, and the later stamp wins.
        var quietActivity = cards.Single(entry => entry.Card.Id == quiet.Id).LastActivityUtc;
        Assert.True(quietActivity >= comment.CreatedUtc, $"{quietActivity:O} should not precede the comment at {comment.CreatedUtc:O}");
        Assert.True(quietActivity > quiet.CreatedUtc);
        Assert.Equal(quiet.Id, cards.Where(entry => entry.LaneName == lanes[0].Name).MaxBy(entry => entry.LastActivityUtc)!.Card.Id);
        Assert.DoesNotContain(cards, entry => entry.Card.Title == "Manual card");
        Assert.Empty(await _store.GetManagedCardsAsync(_project + "-other", boardId, Ct));
    }

    [Fact]
    public async Task ManagedWorkerSessions_AreTheWorkersOnBoardManagedCards()
    {
        var boardId = await DefaultBoardAsync();
        await _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", null), 0, Ct);
        var managed = await _store.CreateCardAsync(_project, NewCard("Managed", BoardWorkflowModes.Agent), Ct);
        var own = await _store.CreateCardAsync(_project, NewCard("Own manager", BoardWorkflowModes.Agent, "base:claude"), Ct);
        var worker = Guid.NewGuid().ToString();
        var planner = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_project, managed.Id, worker, "tab-1", "base:claude", "claude", "Worker", BoardSessionRecord.WorkerOrigin, Ct);
        await _store.LinkSessionAsync(_project, managed.Id, planner, "tab-2", "base:claude", "claude", "Planner", BoardSessionRecord.PlanningWorkerOrigin, Ct);
        await _store.LinkSessionAsync(_project, managed.Id, Guid.NewGuid().ToString(), "tab-3", "base:codex", "codex", "Manager", BoardSessionRecord.ManagerOrigin, Ct);
        await _store.LinkSessionAsync(_project, own.Id, Guid.NewGuid().ToString(), "tab-4", "base:claude", "claude", "Other worker", BoardSessionRecord.WorkerOrigin, Ct);

        var sessions = await _store.GetManagedWorkerSessionsAsync(_project, boardId, Ct);
        Assert.Equal(new HashSet<string> { worker, planner }, sessions.Select(session => session.SessionId).ToHashSet());
        Assert.All(sessions, session => Assert.Equal(managed.Key, session.CardKey));
        Assert.Equal(BoardSessionRecord.PlanningWorkerOrigin, sessions.Single(session => session.SessionId == planner).Origin);
        Assert.All(sessions, session => Assert.True(session.StartedUtc > DateTime.UtcNow.AddMinutes(-5)));
        Assert.Empty(await _store.GetManagedWorkerSessionsAsync(_project + "-other", boardId, Ct));
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
