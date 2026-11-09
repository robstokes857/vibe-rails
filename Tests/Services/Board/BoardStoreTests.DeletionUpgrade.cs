using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletionKeyUpgrade_RepairsAppliedSchema_AndPreservesReceiptsAndOldWriters(bool originalSchema)
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        await _store.EnsureDefaultColumnsAsync(_otherProject, Ct);
        var kept = await _store.CreateCardAsync(_project, NewCard("Keep this card"), Ct);
        if (originalSchema)
        {
            await BoardDeletionUpgradeFixture.RestoreOriginalSchemaAsync(_connectionString, Ct);
        }
        await ExecuteDeletionSql("""
            DELETE FROM SchemaMigrations WHERE Component = 'board-card-deletion' AND Version = 3;
            INSERT INTO BoardDeletedCards
                (CardId, CardKey, ProjectPath, Number, BoardId, DeletedUTC, EntryId, AuthorKind, AuthorLabel, RemoteSeq, Managed)
            VALUES ('old-deleted', 'VB-ABCDE-900', $project, 900, 'old-board', '2026-11-01T00:00:00Z',
                'old-receipt', 'user', 'Original author', 42, 0);
            """, kept);
        var before = await ReadOldDeletionReceiptAsync();

        var upgraded = new BoardStore(_connectionString, _connectionString);
        var created = await upgraded.CreateCardAsync(_project, NewCard("Create after upgrade"), Ct);
        Assert.Equal(before, await ReadOldDeletionReceiptAsync());
        Assert.Equal(kept.Title, (await upgraded.FindCardAsync(_project, kept.Id, Ct))!.Title);
        Assert.Equal(3, await DeletionCount("SchemaMigrations", "Component", "board-card-deletion"));
        await using (var db = SqliteConnectionFactory.Open(_connectionString))
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT StoredCardKey FROM BoardDeletedCards WHERE CardId = 'old-deleted';";
            Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(Ct));
        }

        Assert.True(await upgraded.DeleteCardAsync(_project, created.Id, Ct));
        Assert.Equal(1, await DeletionCount("BoardDeletedCards", "StoredCardKey", created.Key));
        Assert.Equal(1, await DeletionCount("BoardSyncEntryOrder", "EntryId", "old-receipt"));
        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteDeletionSql("""
            UPDATE BoardDeletedCards
            SET StoredCardKey = (SELECT StoredCardKey FROM BoardDeletedCards WHERE CardId = $id)
            WHERE CardId = 'old-deleted';
            """, created));
        Assert.Equal(19, duplicate.SqliteErrorCode);

        // A concurrently running older build still writes its original column list.
        await ExecuteDeletionSql("""
            INSERT INTO BoardDeletedCards
                (CardId, CardKey, ProjectPath, Number, BoardId, DeletedUTC, EntryId, AuthorKind, AuthorLabel, Managed)
            VALUES ('older-writer', 'VB-FGHIJ-901', $project, 901, 'old-board', $now,
                'late-receipt', 'user', 'Older writer', 0);
            """, kept);
        var reopened = new BoardStore(_connectionString, _connectionString);
        Assert.Equal(before, await ReadOldDeletionReceiptAsync());
        Assert.Equal(3, await DeletionCount("SchemaMigrations", "Component", "board-card-deletion"));
        foreach (var key in new[] { "VB-ABCDE-900", "VB-FGHIJ-901", created.Key })
        {
            var error = await Assert.ThrowsAsync<BoardValidationException>(() => reopened.CreateSyncedCardAsync(
                _otherProject, "reused-identity", key, NewCard("Do not resurrect"), BoardAuthor.User(),
                new BoardSyncStamp("remote-entry", 43, DateTime.UtcNow), Ct));
            Assert.Contains("permanently deleted", error.Message);
        }
        Assert.NotNull(await reopened.CreateSyncedCardAsync(_project, "remote-new", "VB-KLMNO-902",
            NewCard("New remote card"), BoardAuthor.User(), new BoardSyncStamp("new-remote-entry", 44, DateTime.UtcNow), Ct));
    }

    private async Task<object[]> ReadOldDeletionReceiptAsync()
    {
        await using var db = SqliteConnectionFactory.Open(_connectionString);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT CardId, CardKey, ProjectPath, Number, BoardId, DeletedUTC, EntryId,
                AuthorKind, AuthorLabel, AuthorCli, RemoteSeq, Managed,
                (SELECT Sequence FROM BoardSyncEntryOrder WHERE EntryId = 'old-receipt')
            FROM BoardDeletedCards WHERE CardId = 'old-deleted';
            """;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var values = new object[reader.FieldCount];
        reader.GetValues(values);
        return values;
    }
}
