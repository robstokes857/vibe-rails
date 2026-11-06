using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardStoreTests
{
    [Fact]
    public async Task PermanentDelete_LegacyKeysAreProjectScoped_AndStoredKeysStayGloballyReserved()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        await _store.EnsureDefaultColumnsAsync(_otherProject, Ct);
        var first = await _store.CreateCardAsync(_project, NewCard("First legacy"), Ct);
        var second = await _store.CreateCardAsync(_otherProject, NewCard("Second legacy"), Ct);
        foreach (var card in new[] { first, second })
            await ExecuteDeletionSql("""
                UPDATE BoardProjectKeys SET Prefix = 'VB' WHERE ProjectPath = $project;
                UPDATE BoardCards SET CardKey = NULL WHERE Id = $id;
                """, card);
        first = (await _store.FindCardAsync(_project, first.Id, Ct))!;
        second = (await _store.FindCardAsync(_otherProject, second.Id, Ct))!;
        Assert.Equal("VB-1", first.Key);
        Assert.Equal(first.Key, second.Key);

        Assert.True(await _store.DeleteCardAsync(_project, first.Id, Ct));
        Assert.NotNull(await _store.FindCardAsync(_otherProject, second.Id, Ct));
        Assert.True(await _store.DeleteCardAsync(_otherProject, second.Id, Ct));
        Assert.Equal(2, await DeletionCount("BoardDeletedCards", "CardKey", "VB-1"));
        var reusedLegacy = await Assert.ThrowsAsync<BoardValidationException>(() => _store.CreateSyncedCardAsync(_project, "reused-legacy", "VB-1",
            NewCard("Recreated") with { BoardId = first.BoardId }, BoardAuthor.User(), new("legacy-entry", 42, DateTime.UtcNow, BoardId: first.BoardId), Ct));
        Assert.Contains("permanently deleted", reusedLegacy.Message);

        var thirdProject = Path.Combine(_root, "third-project");
        await _store.EnsureDefaultColumnsAsync(thirdProject, Ct);
        Assert.NotNull(await _store.CreateCardAsync(thirdProject, NewCard("Different project") with { DisplayId = "VB-1" }, Ct));

        var permanent = await _store.CreateCardAsync(_project, NewCard("Permanent key"), Ct);
        Assert.True(await _store.DeleteCardAsync(_project, permanent.Id, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.CreateSyncedCardAsync(_otherProject, "reused-permanent", permanent.Key,
            NewCard("Recreated") with { BoardId = second.BoardId }, BoardAuthor.User(), new("permanent-entry", 43, DateTime.UtcNow, BoardId: second.BoardId), Ct));
    }

    [Fact]
    public async Task PermanentDelete_RemovesOwnedGraph_AndPreservesOtherBoardsAndSharedSessions()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var otherBoard = await _store.CreateBoardAsync(_project, "Other", Ct);
        var gone = await _store.CreateCardAsync(_project, NewCard("Gone"), Ct);
        var kept = await _store.CreateCardAsync(_project, NewCard("Kept") with { BoardId = otherBoard.Id }, Ct);
        await SeedDeletionGraph(gone);
        await SeedDeletionGraph(kept);
        await ExecuteDeletionSql($"INSERT INTO BoardCardLinks VALUES ('{(string.CompareOrdinal(gone.Id, kept.Id) < 0 ? gone.Id : kept.Id)}', '{(string.CompareOrdinal(gone.Id, kept.Id) < 0 ? kept.Id : gone.Id)}');", gone);
        await _store.LinkSessionAsync(_project, gone.Id, "shared-session", null, "base:codex", "codex", "Shared", "launch", Ct);
        await _store.LinkSessionAsync(_project, kept.Id, "shared-session", null, "base:codex", "codex", "Shared", "launch", Ct);
        await ExecuteDeletionSql("INSERT INTO BoardAgentCompletions VALUES ($project, 'shared-session', 'succeeded', 'Shared report', $now);", gone);

        Assert.False(await _store.DeleteCardAsync(_otherProject, gone.Id, Ct));
        Assert.NotNull(await _store.GetCardDetailAsync(_project, gone.Id, Ct));
        Assert.True(await _store.DeleteCardAsync(_project, gone.Id, Ct));
        Assert.False(await _store.DeleteCardAsync(_project, gone.Id, Ct));
        foreach (var table in new[] { "BoardComments", "BoardAttachments", "BoardCommits", "BoardCommitSnapshots", "BoardChecks", "BoardReviews",
            "BoardReviewSettings", "BoardContextSamples", "BoardHandoffs", "BoardRecallEmbeddings", "BoardSearchEmbeddings", "BoardCardOptions",
            "BoardCodeLinks", "BoardCodeQualityExemptions", "BoardDesktopActivity", "BoardLaneWorkflows", "BoardLaneAutomationDispatch" })
        {
            Assert.Equal(0, await DeletionCount(table, "CardId", gone.Id));
            Assert.True(await DeletionCount(table, "CardId", kept.Id) > 0, table);
        }
        Assert.Equal(0, await DeletionCount("BoardAttachmentContents", "AttachmentId", "att-" + gone.Id));
        Assert.Equal(1, await DeletionCount("BoardAttachmentContents", "AttachmentId", "att-" + kept.Id));
        Assert.Equal(0, await DeletionCount("BoardLaneWorkflowSteps", "WorkflowId", gone.Id));
        Assert.Equal(0, await DeletionCount("BoardLaneStepReports", "EventKey", gone.Id));
        Assert.Equal(1, await DeletionCount("BoardLaneStepReports", "EventKey", kept.Id));
        Assert.Equal(1, await DeletionCount("BoardAgentCompletions", "SessionId", "shared-session"));
        Assert.Equal(0, await DeletionCount("BoardCardLinks", "CardId", gone.Id));
        Assert.Equal(0, await DeletionCount("BoardCardLinks", "LinkedCardId", gone.Id));
        Assert.Equal(kept.Id, (await _store.FindSessionLinkAsync("shared-session", Ct))!.CardId);
        Assert.NotNull(await _store.FindCardAsync(_project, kept.Id, Ct));
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(Ct);
        await using var check = db.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await check.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task PermanentDelete_RollsBackAllDataAndReceipts_WhenAnyDeleteFails()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Atomic"), Ct);
        await SeedDeletionGraph(card);
        await ExecuteDeletionSql("CREATE TRIGGER FailDelete BEFORE DELETE ON BoardCards BEGIN SELECT RAISE(ABORT, 'fixture'); END;", card);
        await Assert.ThrowsAsync<SqliteException>(() => _store.DeleteCardAsync(_project, card.Id, Ct));
        Assert.NotNull(await _store.FindCardAsync(_project, card.Id, Ct));
        Assert.Equal(1, await DeletionCount("BoardLaneStepReports", "EventKey", card.Id));
        Assert.Equal(1, await DeletionCount("BoardCodeQualityExemptions", "CardId", card.Id));
        Assert.Equal(0, await DeletionCount("BoardDeletedCards", "CardId", card.Id));
    }

    [Fact]
    public async Task PermanentDelete_DoesNotSweepLegacySoftDeletedCards_AndReservesIdentityAfterReopen()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var old = await _store.CreateCardAsync(_project, NewCard("Legacy"), Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Delete"), Ct);
        await ExecuteDeletionSql("UPDATE BoardCards SET DeletedUTC = $now WHERE Id = $id;", old);
        // Recreate the pre-feature schema in this disposable fixture, including its retained data.
        await ExecuteDeletionSql("""
            DROP TRIGGER BoardComments_RecordSyncOrder;
            DROP TRIGGER BoardComments_RemoveSyncOrder;
            DROP TABLE BoardSyncEntryOrder;
            DROP TABLE BoardDeletedCards;
            DROP TABLE BoardDeletedTrackerItems;
            DELETE FROM SchemaMigrations WHERE Component = 'board-card-deletion';
            """, old);
        var upgraded = new BoardStore(_connectionString, _connectionString);
        Assert.Equal(1, await DeletionCount("BoardCards", "Id", old.Id));
        Assert.True(await upgraded.DeleteCardAsync(_project, card.Id, Ct));
        await ExecuteDeletionSql("DELETE FROM BoardCardSequences;", card);
        var reopened = new BoardStore(_connectionString, _connectionString);
        Assert.False(await reopened.DeleteCardAsync(_project, old.Id, Ct));
        Assert.Equal(1, await DeletionCount("BoardCards", "Id", old.Id));
        var next = await reopened.CreateCardAsync(_project, NewCard("Next"), Ct);
        Assert.True(next.Number > card.Number);
        foreach (var identity in new[] { (card.Id, card.Key), ("new-remote-id", card.Key), (card.Id, "VB-ABCDE-900") })
            await Assert.ThrowsAsync<BoardValidationException>(() => reopened.CreateSyncedCardAsync(_project, identity.Item1, identity.Item2,
                NewCard("Recreated") with { BoardId = card.BoardId }, BoardAuthor.User(), new("remote-entry", 42, DateTime.UtcNow, BoardId: card.BoardId), Ct));
        Assert.Null(await reopened.FindCardAsync(_project, card.Key, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentDelete_ReservesKeysEvenAsDisplayLabels(bool legacy)
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Gone"), Ct);
        if (legacy)
        {
            await ExecuteDeletionSql("UPDATE BoardCards SET CardKey = NULL WHERE Id = $id;", card);
            card = (await _store.FindCardAsync(_project, card.Id, Ct))!;
        }
        Assert.True(await _store.DeleteCardAsync(_project, card.Id, Ct));
        foreach (var label in new[] { card.Key, card.Key.ToLowerInvariant(), BoardKeys.Format(card.KeyPrefix, card.Number), $"VB-{card.Number}" })
            await Assert.ThrowsAsync<BoardConflictException>(() => _store.CreateCardAsync(_project, NewCard("Impostor") with { DisplayId = label }, Ct));
        var remote = await _store.CreateSyncedCardAsync(_project, "new-remote", "VB-ABCDE-900",
            NewCard("Incoming") with { BoardId = card.BoardId, DisplayId = card.Key }, BoardAuthor.User(),
            new("remote-label", 42, DateTime.UtcNow, BoardId: card.BoardId), Ct);
        Assert.NotEqual(card.Key, remote!.DisplayId);
        Assert.Null(await _store.FindCardAsync(_project, card.Key, Ct));
    }

    [Fact]
    public async Task PermanentDelete_PreservesUnderlyingRecording_InSeparateStateDatabase()
    {
        var statePath = Path.Combine(_root, "recordings.db");
        var services = new ServiceCollection();
        services.AddSqliteStateStorage(_ => new SqliteStoragePaths(statePath));
        using var provider = services.BuildServiceProvider();
        var sessions = provider.GetRequiredService<ISessionStore>();
        var boards = provider.GetRequiredService<IBoardStore>();
        await sessions.CreateSessionAsync("recording", "Codex", null, _project, Environment.ProcessId);
        byte[] recording = [65, 66, 67, 13, 10];
        await sessions.InsertTerminalSessionLogAsync("recording", 0, recording, false, 80, 24);
        await boards.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await boards.CreateCardAsync(_project, NewCard("Recorded"), Ct);
        await boards.LinkSessionAsync(_project, card.Id, "recording", null, "base:codex", "codex", "Recording", "launch", Ct);
        Assert.True(await boards.DeleteCardAsync(_project, card.Id, Ct));
        Assert.NotNull(await sessions.GetSessionByIdAsync("recording", Ct));
        Assert.Equal(recording, Assert.Single(await sessions.GetTerminalSessionLogsAsync("recording", Ct)).Data);
        Assert.Null(await boards.FindSessionLinkAsync("recording", Ct));
    }

    [Fact]
    public async Task PermanentDelete_TrackerReceiptsPreventReimport_WithoutRetainingLinkContent()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Imported"), Ct);
        await ExecuteDeletionSql("""
            INSERT INTO BoardJiraLinks VALUES ($id, 'site', '123', 'PRIVATE-123', 'Person', $now, $now, 'private mapping');
            INSERT INTO BoardAdoLinks VALUES ($id, 'connection', 456, 1, $now, 'Person', 'private mapping', $now);
            """, card);
        Assert.True(await _store.DeleteCardAsync(_project, card.Id, Ct));
        Assert.Equal(0, await DeletionCount("BoardJiraLinks", "CardId", card.Id));
        Assert.Equal(0, await DeletionCount("BoardAdoLinks", "CardId", card.Id));
        Assert.False((await _store.GetJiraImportStatesAsync(_project, "site", ["123"], Ct))["123"]);
        Assert.False((await _store.GetAdoImportStatesAsync(_project, "connection", [456], Ct))[456]);
        Assert.Empty(await _store.GetJiraImportStatesAsync(_otherProject, "site", ["123"], Ct));
        Assert.Empty(await _store.GetAdoImportStatesAsync(_project, "another-connection", [456], Ct));
        var jira = (await _store.FindJiraLinkAsync("site", "123", Ct))!;
        var ado = (await _store.FindAdoLinkAsync("connection", 456, Ct))!;
        Assert.Null(jira.AssigneeDisplay);
        Assert.Null(ado.AssigneeDisplay);
        Assert.Null(await _store.CreateJiraCardAsync(_project, NewCard("Reimport"), jira, Ct));
        Assert.Null(await _store.CreateAdoCardAsync(_project, NewCard("Reimport"), ado, Ct));
    }

    private async Task SeedDeletionGraph(BoardCardRecord card)
    {
        await _store.AddCommentAsync(_project, card.Id, BoardAuthor.User(), "Discussion", Ct);
        await _store.AddCommitAsync(_project, card.Id, "0123456789abcdef", "Author", "Commit", DateTime.UtcNow, Snapshot(), Ct);
        await ExecuteDeletionSql("""
            INSERT INTO BoardAttachments VALUES ('att-' || $id, $id, 'a.txt', 'text/plain', 3, '', $now);
            INSERT INTO BoardAttachmentContents VALUES ('att-' || $id, X'414243');
            INSERT INTO BoardCardOptions VALUES ($id, '{}');
            INSERT INTO BoardChecks (Id, CardId, RunId, ActionId, StartedUTC, SummaryJson) VALUES ($id, $id, $id, $id, $now, '{}');
            INSERT INTO BoardReviews (Id, CardId, CreatedUTC, RecordJson) VALUES ($id, $id, $now, '{}');
            INSERT INTO BoardReviewSettings VALUES ($id, '{}');
            INSERT INTO BoardHandoffs VALUES ($id, $id, '{}', $now);
            INSERT INTO BoardRecallEmbeddings VALUES ($id, '1', '[]');
            INSERT INTO BoardSearchEmbeddings VALUES ($id, '1', '1', '[]');
            INSERT INTO BoardContextSamples VALUES ($id, $id, NULL, $now, 'work', NULL, NULL, 1, 1, 1, 1, '{}');
            INSERT INTO BoardCodeLinks VALUES ($id, $id, 'https://github.com/example/repo/pull/1', 'pr', 'PR', $now);
            INSERT INTO BoardCodeQualityExemptions VALUES ($id, $project, 'a.cs', $id, '{}', NULL, NULL);
            INSERT INTO BoardDesktopActivity VALUES ($id, 'client', 'Client', $now, NULL);
            INSERT INTO BoardLaneWorkflows VALUES ($id, $id, $lane, 0, 1);
            INSERT INTO BoardLaneWorkflowSteps VALUES ($id, 1, 0, $id);
            INSERT INTO BoardLaneStepReports (EventKey, JobId, Status, Summary, CreatedUTC) VALUES ($id, 1, 'succeeded', 'Report', $now);
            INSERT INTO BoardLaneAutomationDispatch VALUES ($id, 1, $id, $lane, 0, 'succeeded', 'Done', NULL, 0);
            """, card);
    }

    private async Task ExecuteDeletionSql(string sql, BoardCardRecord card)
    {
        await using var db = SqliteConnectionFactory.Open(_connectionString);
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", card.Id);
        command.Parameters.AddWithValue("$lane", card.ColumnId);
        command.Parameters.AddWithValue("$project", card.ProjectPath);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> DeletionCount(string table, string column, string id)
    {
        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = $id";
        command.Parameters.AddWithValue("$id", id);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
