using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardSyncStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-sync-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string cs;
    private readonly BoardStore store;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardSyncStoreTests()
    {
        Directory.CreateDirectory(root);
        cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new BoardStore(cs, cs);
    }

    [Fact]
    public async Task OutboxReadsCreationBeforeComments_AndAcknowledgesOnlySentEntries()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "First", "", null, "medium", null, [], false), Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Comment", Ct);
        var entries = await store.GetUnsentLogEntriesAsync(card.BoardId, 200, Ct);
        Assert.Equal(new[] { "created", "comment" }, entries.Select(e => e.Entry.Kind));
        Assert.All(entries, e => Assert.Equal(card.Key, e.CardKey));
        await store.MarkLogEntriesSentAsync([new(entries[0].Entry.Id, 1)], Ct);
        Assert.Equal("comment", Assert.Single(await store.GetUnsentLogEntriesAsync(card.BoardId, 200, Ct)).Entry.Kind);
        Assert.Equal(1, await store.CountUnsentLogEntriesAsync(card.BoardId, Ct));
    }

    [Fact]
    public async Task ActivityMigrationPreservesOldPublicationScopeAndLegacyWrites()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Before activity", "", null, "medium", null, [], false), Ct);
        await store.SaveSyncLinkAsync(new(card.BoardId, "remote", 7, true, null, null, null, default, default, DestinationKey: "server"), Ct);
        await ExecuteAsync("ALTER TABLE BoardSyncLinks DROP COLUMN ActivityAfter; ALTER TABLE BoardSyncLinks DROP COLUMN ActivitySchema; DELETE FROM SchemaMigrations WHERE Component='board' AND Version=23;");
        var upgraded = new BoardStore(cs, cs);
        var existing = (await upgraded.GetSyncLinkAsync(root, card.BoardId, Ct))!;
        Assert.Equal(0, existing.ActivitySchema);
        Assert.Null(existing.ActivityAfter);
        Assert.True(existing.Enabled);
        Assert.Equal(7, existing.Cursor);
        await upgraded.SaveSyncLinkAsync(existing with { ActivitySchema = 1, ActivityAfter = card.Id }, Ct);
        await ExecuteAsync("UPDATE BoardSyncLinks SET Cursor=8, Enabled=0;"); // old-column-only writer
        var preserved = (await new BoardStore(cs, cs).GetSyncLinkAsync(root, card.BoardId, Ct))!;
        Assert.Equal(1, preserved.ActivitySchema);
        Assert.Equal(card.Id, preserved.ActivityAfter);
        Assert.Equal(8, preserved.Cursor);
        Assert.False(preserved.Enabled);
        Assert.Equal("Before activity", (await upgraded.FindCardAsync(root, card.Id, Ct))!.Title);
    }

    [Fact]
    public async Task RejectionsAreBoardScoped_SurviveReopeningTheStore_AndResetOnlyWithTheSentMarks()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Keep", "", null, "medium", null, [], false), Ct);
        var entry = Assert.Single(await store.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct));
        var other = await store.CreateBoardAsync(root, "Other", Ct);
        Assert.False(await store.RejectLogEntryAsync(other.Id, entry.Entry.Id, Ct));
        Assert.True(await store.RejectLogEntryAsync(card.BoardId, entry.Entry.Id, Ct));
        Assert.False(await store.RejectLogEntryAsync(card.BoardId, entry.Entry.Id, Ct));
        var reopened = new BoardStore(cs, cs);
        Assert.Equal(1, await reopened.CountRejectedLogEntriesAsync(card.BoardId, Ct));
        Assert.Equal(card.Key, Assert.Single(await reopened.GetRejectedLogEntriesAsync(card.BoardId, 50, Ct)).CardKey);
        Assert.Contains("title", await reopened.GetFieldsChangedAfterAsync(card.Id, 100, Ct));
        Assert.True(await reopened.HasLogEntryAsync(entry.Entry.Id, Ct));
        await reopened.ResetSentMarksAsync(card.BoardId, Ct);
        Assert.Equal(0, await reopened.CountRejectedLogEntriesAsync(card.BoardId, Ct));
        Assert.Single(await reopened.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct));
    }

    [Fact]
    public async Task SequenceCollisionRollsBackTheWholeBatch_AndSequencesAreScopedToTheBoard()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "First", "", null, "medium", null, [], false), Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "One", Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Two", Ct);
        var entries = await store.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct);
        await store.MarkLogEntriesSentAsync([new(entries[0].Entry.Id, 1)], Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => store.MarkLogEntriesSentAsync(
            [new(entries[1].Entry.Id, 2), new(entries[2].Entry.Id, 1)], Ct));
        Assert.Equal(2, await store.CountUnsentLogEntriesAsync(card.BoardId, Ct));
        Assert.Equal(1, await store.GetMaxAcknowledgedSequenceAsync(card.BoardId, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => store.MarkLogEntriesSentAsync([new(entries[0].Entry.Id, 3)], Ct));
        var board = await store.CreateBoardAsync(root, "Separate sequence", Ct);
        var lane = (await store.GetColumnsAsync(root, Ct, board.Id))[0];
        await store.CreateCardAsync(root, new(lane.Id, "Other", "", null, "medium", null, [], false, BoardId: board.Id), Ct);
        var other = Assert.Single(await store.GetUnsentLogEntriesAsync(board.Id, 20, Ct));
        await store.MarkLogEntriesSentAsync([new(other.Entry.Id, 1)], Ct);
        Assert.Equal(0, await store.CountUnsentLogEntriesAsync(board.Id, Ct));
    }

    [Fact]
    public async Task AdditiveRejectedFieldMigrationRetainsExistingRowsAndLegacyWrites()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Before migration", "", null, "medium", null, [], false), Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Existing comment", Ct);
        await ExecuteAsync("DROP TABLE BoardSyncRejectedFields; DROP INDEX IX_BoardComments_RemoteSeq; DELETE FROM SchemaMigrations WHERE Component='board' AND Version=17;");
        var upgraded = new BoardStore(cs, cs);
        Assert.Equal("Before migration", (await upgraded.FindCardAsync(root, card.Id, Ct))!.Title);
        Assert.Equal("Existing comment", Assert.Single((await upgraded.GetCardDetailAsync(root, card.Id, Ct))!.Comments).Body);
        await ExecuteAsync("UPDATE BoardCards SET Title='Legacy update';");
        Assert.Equal("Legacy update", (await upgraded.FindCardAsync(root, card.Id, Ct))!.Title);
        var rejected = (await upgraded.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct))[0];
        Assert.True(await upgraded.RejectLogEntryAsync(card.BoardId, rejected.Entry.Id, Ct));
        Assert.Contains("title", await upgraded.GetFieldsChangedAfterAsync(card.Id, 100, Ct));
    }

    [Fact]
    public async Task LegacyBaselinePrecedesExistingCommentsWithTheSameTimestamp()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Legacy", "", null, "medium", null, [], false), Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Older comment", Ct);
        await ExecuteAsync("DELETE FROM BoardComments WHERE Kind = 'created'; UPDATE BoardComments SET CreatedUTC = (SELECT CreatedUTC FROM BoardCards LIMIT 1);");
        Assert.Equal(1, await store.WriteSyncBaselineAsync(root, card.BoardId, Ct));
        Assert.Equal(0, await store.WriteSyncBaselineAsync(root, card.BoardId, Ct));
        Assert.Equal("created", (await store.GetUnsentLogEntriesAsync(card.BoardId, 200, Ct))[0].Entry.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteBaselinePreservesAnExistingLegacyCard(bool deleted)
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Legacy", "", null, "medium", null, [], false), Ct);
        await ExecuteAsync("UPDATE BoardCards SET CardKey = NULL;");
        card = (await store.FindCardAsync(root, card.Id, Ct))!;
        Assert.False(BoardKeys.TryParseStored(card.Key, out _));
        if (deleted) await store.DeleteCardAsync(root, card.Id, Ct);
        var stamp = new BoardSyncStamp("log_legacy_baseline", 1, DateTime.UtcNow, BoardId: card.BoardId);
        var retained = await store.CreateSyncedCardAsync(root, card.Id, card.Key,
            new(card.ColumnId, "Remote", "", null, "medium", null, [], false, BoardId: card.BoardId), BoardAuthor.User(), stamp, Ct);
        Assert.Equal(card.Key, retained!.Key);
        Assert.Equal("Legacy", retained.Title);
        Assert.Equal(deleted, await store.FindCardAsync(root, card.Id, Ct) is null);
        Assert.True(await store.HasLogEntryAsync(stamp.EntryId, Ct));
    }

    [Fact]
    public async Task PulledNoOpRetainsTheOriginalLogEntryWithoutEchoingIt()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Same", "", null, "medium", null, [], false), Ct);
        var stamp = new BoardSyncStamp("log_web", 10, DateTime.UtcNow);
        await store.UpdateSyncedCardAsync(root, card.Id, new(Title: "Same"), BoardAuthor.User(), stamp, Ct);
        Assert.True(await store.HasLogEntryAsync(stamp.EntryId, Ct));
        Assert.DoesNotContain(await store.GetUnsentLogEntriesAsync(card.BoardId, 200, Ct), e => e.Entry.Id == stamp.EntryId);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task HistoryIsExplicitScopedAndSeparateFromConversation()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var board = Assert.Single(await store.GetBoardsAsync(root, Ct));
        var card = await store.CreateCardAsync(root, new(null, "History card", "", null, "medium", null, [], false), Ct);
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Conversation only", Ct);
        await store.AddNoteAsync(root, card.Id, BoardAuthor.User(), "Scratchpad only", Ct);
        await store.RenameBoardAsync(root, board.Id, "Renamed", null, Ct);
        var detail = await store.GetCardDetailAsync(root, card.Id, Ct);
        Assert.Single(detail!.Comments);
        Assert.Single(detail.Notes);
        var history = await store.GetHistoryAsync(root, board.Id, null, 0, Ct);
        Assert.Contains(history!, e => e.Body.Contains("Renamed"));
        Assert.Contains(history!, e => e.CardKey == card.Key && e.Kind == "created");
        Assert.DoesNotContain(history!, e => e.Kind is "comment" or "note");
        Assert.Single((await store.GetHistoryAsync(root, board.Id, card.Id, 0, Ct))!);
        Assert.Null(await store.GetHistoryAsync(root + "foreign", board.Id, null, 0, Ct));
    }

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task RemoteKeysKeepTheirSuffixAndAmbiguousShortFormsAreRejected()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var local = await store.CreateCardAsync(root, new(null, "Local", "", null, "medium", null, [], false), Ct);
        var remoteKey = local.KeyPrefix + "-ZZZZZ-1";
        var remote = await store.CreateSyncedCardAsync(root, "card_000000000099", remoteKey,
            new(local.ColumnId, "Remote", "", null, "medium", null, [], false, BoardId: local.BoardId),
            BoardAuthor.User(), new("log_remote_key", 1, DateTime.UtcNow, BoardId: local.BoardId), Ct);
        Assert.NotEqual(local.Number, remote!.Number);
        Assert.Equal(remote.Id, (await store.FindCardAsync(root, remoteKey.ToLowerInvariant(), Ct))!.Id);
        await Assert.ThrowsAsync<BoardValidationException>(() => store.FindCardAsync(root, local.KeyPrefix + "-1", Ct));
        Assert.Null(await store.FindCardAsync(root, local.KeyPrefix + "-" + remote.Number, Ct));
        Assert.Equal(local.Id, (await store.FindCardAsync(root, local.Key, Ct))!.Id);
    }

    [Fact]
    public async Task PulledNumbersNearTheMarkAreAdopted_WhileFarOrMaximalOnesNeverMoveLocalNumbering()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var local = await store.CreateCardAsync(root, new(null, "Local", "", null, "medium", null, [], false), Ct);
        async Task<BoardCardRecord> Pull(string id, string key) => (await store.CreateSyncedCardAsync(root, id, key,
            new(local.ColumnId, "Remote " + key, "", null, "medium", null, [], false, BoardId: local.BoardId),
            BoardAuthor.User(), new("log_" + id, 1, DateTime.UtcNow, BoardId: local.BoardId), Ct))!;

        Assert.Equal(5, (await Pull("card_000000000005", "XX-ABCDE-5")).Number);
        var maximal = await Pull("card_0000000000ff", "XX-ABCDF-2147483647");
        Assert.Equal(6, maximal.Number);
        Assert.Equal("XX-ABCDF-2147483647", maximal.Key);
        Assert.Equal(7, (await Pull("card_0000000000fe", "XX-ABCDG-900000000")).Number);
        Assert.Equal(8, (await store.CreateCardAsync(root, new(null, "Next local", "", null, "medium", null, [], false), Ct)).Number);
        Assert.Equal(maximal.Id, (await store.FindCardAsync(root, "XX-2147483647", Ct))!.Id);
    }

    [Fact]
    public async Task AnExhaustedCardSequenceIsAValidationErrorThatLeavesTheMarkAlone()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        await store.CreateCardAsync(root, new(null, "Local", "", null, "medium", null, [], false), Ct);
        await ExecuteAsync("UPDATE BoardCardSequences SET LastNumber = 2147483647;");
        var error = await Assert.ThrowsAsync<BoardValidationException>(() =>
            store.CreateCardAsync(root, new(null, "One too many", "", null, "medium", null, [], false), Ct));
        Assert.Contains("every card number", error.Message);
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT LastNumber FROM BoardCardSequences;";
        Assert.Equal(2147483647L, (long)(await command.ExecuteScalarAsync(Ct))!);
        Assert.Single(await store.GetCardsAsync(root, Ct));
    }

    [Fact]
    public async Task SkippedRemoteEntriesAreBoardScoped_KeepTheLatestAttempt_AndResetWithTheSentMarks()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Card", "", null, "medium", null, [], false), Ct);
        var other = await store.CreateBoardAsync(root, "Other", Ct);
        Assert.True(await store.RecordSkippedSyncEntryAsync(card.BoardId, new("web_1", 4, card.Key, "restored", "First reason"), "10.11.2", Ct));
        // A later attempt at the same entry records that attempt's version and reason.
        Assert.True(await store.RecordSkippedSyncEntryAsync(card.BoardId, new("web_1", 4, card.Key, "restored", "Second reason"), "10.12.0", Ct));
        Assert.True(await store.RecordSkippedSyncEntryAsync(card.BoardId, new("web_2", 9, card.Key, "change", "Later"), "10.12.0", Ct));
        Assert.False(await store.RecordSkippedSyncEntryAsync("brd_missing", new("web_3", 1, card.Key, "change", "No board"), "10.12.0", Ct));
        Assert.Equal(2, await store.CountSkippedSyncEntriesAsync(card.BoardId, Ct));
        Assert.Equal(0, await store.CountSkippedSyncEntriesAsync(other.Id, Ct));
        var listed = await store.GetSkippedSyncEntriesAsync(card.BoardId, 50, Ct);
        Assert.Equal(["web_2", "web_1"], listed.Select(e => e.EntryId));
        Assert.Equal("Second reason", listed[1].Reason);
        Assert.Equal(["10.12.0", "10.12.0"], await SkippedVersionsAsync());
        await store.ResetSentMarksAsync(card.BoardId, Ct);
        Assert.Equal(0, await store.CountSkippedSyncEntriesAsync(card.BoardId, Ct));
    }

    [Fact]
    public async Task ARetryMovesTheCursorBackOncePerNewerVersion_AndNeverForward()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Card", "", null, "medium", null, [], false), Ct);
        await store.SaveSyncLinkAsync(new(card.BoardId, "remote", 20, true, null, null, null, default, default, DestinationKey: "server"), Ct);
        await store.RecordSkippedSyncEntryAsync(card.BoardId, new("web_newer", 4, card.Key, "future", "Unknown"), "10.12.0", Ct);
        await store.RecordSkippedSyncEntryAsync(card.BoardId, new("web_older", 6, card.Key, "future", "Unknown"), "10.11.2", Ct);

        // The version that last tried an entry, and every earlier one, leaves it alone; so does a build
        // whose version does not parse, since it cannot tell that it is newer.
        Assert.Null(await store.RetrySkippedSyncEntriesAsync(card.BoardId, "10.11.2", Ct));
        Assert.Null(await store.RetrySkippedSyncEntriesAsync(card.BoardId, "dev", Ct));
        Assert.Equal(20, (await store.GetSyncLinkAsync(root, card.BoardId, Ct))!.Cursor);

        // 10.12.0 retries only what 10.11.2 tried, once.
        Assert.Equal(5, await store.RetrySkippedSyncEntriesAsync(card.BoardId, "10.12.0", Ct));
        Assert.Equal(["10.12.0", "10.12.0"], await SkippedVersionsAsync());
        Assert.Null(await store.RetrySkippedSyncEntriesAsync(card.BoardId, "10.12.0", Ct));
        Assert.Equal(5, (await store.GetSyncLinkAsync(root, card.BoardId, Ct))!.Cursor);

        // A newer release retries both, going back to the earlier entry. Only release numbers order
        // versions: 10.13.0 does not retry what 10.13.0-rc.1 tried.
        Assert.Equal(3, await store.RetrySkippedSyncEntriesAsync(card.BoardId, "10.13.0-rc.1", Ct));
        Assert.Null(await store.RetrySkippedSyncEntriesAsync(card.BoardId, "10.13.0", Ct));

        // A record from before board/22 names no version and counts as earlier. The cursor never moves forward.
        await store.RecordSkippedSyncEntryAsync(card.BoardId, new("web_legacy", 2, card.Key, "future", "Unknown"), "10.13.0", Ct);
        await ExecuteAsync("UPDATE BoardSyncSkippedEntries SET Version = NULL WHERE EntryId = 'web_legacy';");
        await store.SaveSyncLinkAsync((await store.GetSyncLinkAsync(root, card.BoardId, Ct))! with { Cursor = 0 }, Ct);
        Assert.Equal(0, await store.RetrySkippedSyncEntriesAsync(card.BoardId, "10.13.0", Ct));
        Assert.Equal(["10.13.0", "10.13.0-rc.1", "10.13.0-rc.1"], await SkippedVersionsAsync());

        // Without a link there is no cursor to move, and nothing is marked as tried.
        var other = await store.CreateBoardAsync(root, "Other", Ct);
        await store.RecordSkippedSyncEntryAsync(other.Id, new("web_other", 3, card.Key, "future", "Unknown"), "10.11.2", Ct);
        Assert.Null(await store.RetrySkippedSyncEntriesAsync(other.Id, "10.13.0", Ct));
        Assert.Contains("10.11.2", await SkippedVersionsAsync());
    }

    [Fact]
    public async Task ApplyingASkippedEntryRemovesItsRecordInTheSameWrite()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Card", "", null, "medium", null, [], false), Ct);
        await store.RecordSkippedSyncEntryAsync(card.BoardId, new("web_change", 7, card.Key, "change", "Unknown"), "1.0.0", Ct);
        await store.RecordSkippedSyncEntryAsync(card.BoardId, new("web_comment", 8, card.Key, "comment", "Unknown"), "1.0.0", Ct);
        await store.RecordSkippedSyncEntryAsync(card.BoardId, new("web_other", 9, card.Key, "comment", "Unknown"), "1.0.0", Ct);

        await store.UpdateSyncedCardAsync(root, card.Id, new(Title: "From the web"), BoardAuthor.User(),
            new("web_change", 7, DateTime.UtcNow, "Changed: title", """{"title":{"to":"From the web"}}""", card.BoardId), Ct);
        await store.AddSyncedCommentAsync(root, card.Id, BoardAuthor.User(), "Hello", "comment",
            new("web_comment", 8, DateTime.UtcNow, BoardId: card.BoardId), Ct);

        Assert.Equal("web_other", Assert.Single(await store.GetSkippedSyncEntriesAsync(card.BoardId, 50, Ct)).EntryId);
    }

    private async Task<List<string?>> SkippedVersionsAsync()
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM BoardSyncSkippedEntries ORDER BY Seq;";
        var versions = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) versions.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return versions;
    }

    [Fact]
    public async Task CardsStayOnTheirBoard_MoveAndUpdateBothRejectAnotherBoardsLane()
    {
        // Rejected for every board, published or not: a card's key names it on viberails.ai, so it
        // cannot change boards underneath that identity. Another board needs a new card.
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Card", "", null, "medium", null, [], false), Ct);
        var other = await store.CreateBoardAsync(root, "Other", Ct);
        var lane = (await store.GetColumnsAsync(root, Ct, other.Id))[0];
        await Assert.ThrowsAsync<BoardValidationException>(() => store.MoveCardAsync(root, card.Id, lane.Id, null, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => store.UpdateCardAsync(root, card.Id, new(ColumnId: lane.Id), Ct));
        Assert.Equal(card.BoardId, (await store.FindCardAsync(root, card.Id, Ct))!.BoardId);
    }

    [Fact]
    public async Task StoredKeysAreUniqueAcrossTheDatabase_WhileLegacyNullKeysAreNot()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new(null, "Keyed", "", null, "medium", null, [], false), Ct);
        // Clone the row with a new id and number so only CardKey can collide.
        string Clone(string id, int numberOffset, string cardKeySql) =>
            "INSERT INTO BoardCards (Id, ProjectPath, Number, ColumnId, Position, Title, CreatedUTC, UpdatedUTC, CardKey) " +
            $"SELECT '{id}', ProjectPath, Number + {numberOffset}, ColumnId, Position + 1, 'Clone', CreatedUTC, UpdatedUTC, {cardKeySql} " +
            $"FROM BoardCards WHERE Id = '{card.Id}';";

        // The index is partial: cards an older binary created (NULL key) coexist freely.
        await ExecuteAsync(Clone("card_legacy_a", 1000, "NULL"));
        await ExecuteAsync(Clone("card_legacy_b", 1001, "NULL"));
        var error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(Clone("card_duplicate", 1002, "CardKey")));
        Assert.Equal(19, error.SqliteErrorCode); // SQLITE_CONSTRAINT
        Assert.Contains("BoardCards.CardKey", error.Message);
        Assert.Equal(card.Id, (await store.FindCardAsync(root, card.Key, Ct))!.Id);
    }

    [Fact]
    public async Task MissingRemoteLaneQueuesACorrectionWithoutDroppingTheOriginalEvent()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var lane = (await store.GetColumnsAsync(root, Ct))[0];
        var stamp = new BoardSyncStamp("log_remote_lane", 1, DateTime.UtcNow, "Created remotely",
            """{"title":{"to":"Remote"},"lane":{"to":"col_deleted"}}""", lane.BoardId);
        var card = await store.CreateSyncedCardAsync(root, "card_000000000088", "VB-ZZZZZ-8",
            new(lane.Id, "Remote", "", null, "medium", null, [], false, BoardId: lane.BoardId), BoardAuthor.User(), stamp, Ct);
        Assert.True(await store.HasLogEntryAsync(stamp.EntryId, Ct));
        var correction = Assert.Single(await store.GetUnsentLogEntriesAsync(lane.BoardId, 20, Ct));
        Assert.Equal("change", correction.Entry.Kind);
        Assert.Contains(lane.Id, correction.Entry.Changes);
        Assert.Equal(lane.Id, card!.ColumnId);
    }

    [Fact]
    public async Task RemoteCreationAndMoveQueueLocalAutomationsExactlyOnce()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        await ExecuteAsync("""
            CREATE TABLE Jobs (Id INTEGER PRIMARY KEY, ProjectPath TEXT, Enabled INTEGER, DeletedUTC TEXT);
            INSERT INTO Jobs SELECT 10, ProjectPath, 1, NULL FROM Boards LIMIT 1;
            INSERT INTO Jobs SELECT 20, ProjectPath, 1, NULL FROM Boards LIMIT 1;
            """);
        var lanes = await store.GetColumnsAsync(root, Ct);
        await store.SaveLaneAutomationAsync(root, lanes[0].Id, [10], 0, Ct);
        await store.SaveLaneAutomationAsync(root, lanes[1].Id, [20], 0, Ct);
        var stamp = new BoardSyncStamp("log_remote_create", 1, DateTime.UtcNow, BoardId: lanes[0].BoardId);
        var draft = new NewBoardCard(lanes[0].Id, "Remote", "", null, "medium", null, [], false, BoardId: lanes[0].BoardId);
        var card = await store.CreateSyncedCardAsync(root, "card_000000000077", "VB-ZZZZZ-7", draft, BoardAuthor.User(), stamp, Ct);
        Assert.Equal(10, Assert.Single(await store.GetPendingLaneAutomationsAsync(root, card!.Id, Ct)).JobId);
        await store.CreateSyncedCardAsync(root, card.Id, card.Key, draft, BoardAuthor.User(), stamp, Ct);
        Assert.Single(await store.GetPendingLaneAutomationsAsync(root, card.Id, Ct));
        var move = new BoardSyncStamp("log_remote_move", 2, DateTime.UtcNow, BoardId: card.BoardId);
        await store.UpdateSyncedCardAsync(root, card.Id, new(ColumnId: lanes[1].Id), BoardAuthor.User(), move, Ct);
        var pending = Assert.Single(await store.GetDueLaneAutomationsAsync(DateTime.UtcNow.AddMinutes(2), Ct));
        Assert.Equal(20, pending.JobId);
        await store.UpdateSyncedCardAsync(root, card.Id, new(ColumnId: lanes[1].Id), BoardAuthor.User(), move, Ct);
        Assert.Equal(pending.EventKey, Assert.Single(await store.GetDueLaneAutomationsAsync(DateTime.UtcNow.AddMinutes(2), Ct)).EventKey);
    }
}
