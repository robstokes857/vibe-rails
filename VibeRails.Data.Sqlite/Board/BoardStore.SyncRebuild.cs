using System.Data;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string SyncRebuildSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardSyncRebuilds (
            Id TEXT PRIMARY KEY,
            BoardId TEXT NOT NULL REFERENCES Boards(Id) ON DELETE CASCADE,
            RemoteBoardId TEXT NOT NULL,
            DestinationKey TEXT NOT NULL,
            Phase TEXT NOT NULL,
            ReplacementRemoteBoardId TEXT,
            CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL,
            CompletedUTC TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_BoardSyncRebuilds_Pending
            ON BoardSyncRebuilds(BoardId) WHERE CompletedUTC IS NULL;
        """;

    /// <inheritdoc />
    public async Task<BoardSyncRebuildRecord?> GetPendingSyncRebuildAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        return await ReadSyncRebuildAsync(db, null, boardId, cancellationToken);
    }

    private static async Task<BoardSyncRebuildRecord?> ReadSyncRebuildAsync(SqliteConnection db, SqliteTransaction? tx,
        string boardId, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT Id, BoardId, RemoteBoardId, DestinationKey, Phase, ReplacementRemoteBoardId
            FROM BoardSyncRebuilds WHERE BoardId=$board AND CompletedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)) : null;
    }

    /// <inheritdoc />
    public async Task<BoardSyncRebuildRecord?> BeginSyncRebuildAsync(BoardSyncLinkRecord expected, CancellationToken cancellationToken = default)
    {
        if (expected.Imported || expected.DestinationKey is null) return null;
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        if (!await SyncRebuildLinkMatchesAsync(db, tx, expected.BoardId, expected.RemoteBoardId,
                expected.DestinationKey, cancellationToken)) return null;
        var pending = await ReadSyncRebuildAsync(db, tx, expected.BoardId, cancellationToken);
        if (pending is not null) return pending;
        var id = "rebuild_" + Guid.NewGuid().ToString("N");
        await ExecuteSharingAsync(db, tx, """
            INSERT INTO BoardSyncRebuilds
                (Id, BoardId, RemoteBoardId, DestinationKey, Phase, CreatedUTC, UpdatedUTC)
            VALUES ($id, $board, $remote, $destination, 'delete', $now, $now);
            """, cancellationToken, ("$id", id), ("$board", expected.BoardId),
            ("$remote", expected.RemoteBoardId), ("$destination", expected.DestinationKey), ("$now", ToDb(DateTime.UtcNow)));
        await tx.CommitAsync(cancellationToken);
        return new(id, expected.BoardId, expected.RemoteBoardId, expected.DestinationKey, "delete", null);
    }

    private static async Task<bool> SyncRebuildLinkMatchesAsync(SqliteConnection db, SqliteTransaction tx,
        string boardId, string remoteBoardId, string destination, CancellationToken ct) =>
        await ScalarLongAsync(db, tx, """
            SELECT COUNT(*) FROM BoardSyncLinks l JOIN Boards b ON b.Id=l.BoardId
            WHERE l.BoardId=$board AND l.RemoteBoardId=$remote AND l.DestinationKey=$destination
              AND b.SyncEnabled=1
              AND NOT EXISTS (SELECT 1 FROM BoardSharedOrigins o WHERE o.BoardId=l.BoardId);
            """, ("$board", boardId), ct, ("$remote", remoteBoardId), ("$destination", destination)) == 1;

    /// <inheritdoc />
    public async Task ConfirmSyncRebuildDeletedAsync(string rebuildId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        await ExecuteSharingAsync(db, tx, """
            UPDATE BoardSyncRebuilds SET Phase='publish', UpdatedUTC=$now
            WHERE Id=$id AND Phase='delete' AND CompletedUTC IS NULL;
            """, cancellationToken, ("$id", rebuildId), ("$now", ToDb(DateTime.UtcNow)));
        await tx.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AbandonSyncRebuildAsync(string rebuildId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        await ExecuteSharingAsync(db, tx, """
            UPDATE BoardSyncRebuilds SET Phase='abandoned', CompletedUTC=$now, UpdatedUTC=$now
            WHERE Id=$id AND Phase='delete' AND CompletedUTC IS NULL;
            """, cancellationToken, ("$id", rebuildId), ("$now", ToDb(DateTime.UtcNow)));
        await tx.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BoardSyncLinkRecord?> PrepareSyncRebuildUploadAsync(BoardSyncRebuildRecord expected,
        string replacementRemoteBoardId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(replacementRemoteBoardId, out var remoteId) || remoteId == Guid.Empty
            || string.Equals(expected.RemoteBoardId, replacementRemoteBoardId, StringComparison.OrdinalIgnoreCase))
            throw new BoardValidationException("The server did not confirm a new hosted copy; local work is retained.");
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        var pending = await ReadSyncRebuildAsync(db, tx, expected.BoardId, cancellationToken);
        if (pending?.Id != expected.Id || pending.Phase != "publish"
            || !await SyncRebuildLinkMatchesAsync(db, tx, expected.BoardId, expected.RemoteBoardId,
                expected.DestinationKey, cancellationToken)) return null;

        // Requeue portable history, including past rejections, but preserve local-only entries.
        await ExecuteSharingAsync(db, tx, """
            DELETE FROM BoardSyncRejectedFields WHERE EntryId IN (
                SELECT m.Id FROM BoardSyncLog m JOIN BoardCards c ON c.Id=m.CardId
                JOIN BoardColumns k ON k.Id=c.ColumnId WHERE COALESCE(m.SyncBoardId,k.BoardId)=$board);
            WITH current_cards AS (SELECT c.Id FROM BoardCards c JOIN BoardColumns k ON k.Id=c.ColumnId WHERE k.BoardId=$board)
            UPDATE BoardComments SET
                RemoteSeq=CASE WHEN SyncBoardId IS NOT NULL OR (Kind='created' AND CardId IN current_cards) THEN 0 ELSE NULL END,
                TransferRemoteSeq=CASE WHEN SyncBoardId IS NOT NULL AND Kind='created' AND CardId IN current_cards THEN 0 ELSE NULL END
            WHERE ((CASE WHEN SyncBoardId IS NULL THEN RemoteSeq ELSE TransferRemoteSeq END) <> 0
                   OR (Kind='created' AND CardId IN current_cards))
              AND COALESCE(SyncBoardId, (SELECT k.BoardId FROM BoardCards c
                  JOIN BoardColumns k ON k.Id=c.ColumnId WHERE c.Id=BoardComments.CardId))=$board;
            """, cancellationToken, ("$board", expected.BoardId));

        var link = await ReadRebuildLinkAsync(db, tx, expected.BoardId, cancellationToken);
        if (link is null) return null;
        // Final snapshots follow every older change, so the host's current values and liveness win.
        await QueueSyncRebuildSnapshotsAsync(db, tx, link.ProjectPath, expected.BoardId, cancellationToken);
        var now = ToDb(DateTime.UtcNow);
        await ExecuteSharingAsync(db, tx, """
            UPDATE BoardSyncLinks SET RemoteBoardId=$remote, Cursor=0, LayoutHash=NULL,
                ActivityAfter=NULL, LastSyncUTC=NULL, LastError=NULL, UpdatedUTC=$now WHERE BoardId=$board;
            UPDATE BoardSyncRebuilds SET Phase='upload', ReplacementRemoteBoardId=$remote, UpdatedUTC=$now
                WHERE Id=$id AND CompletedUTC IS NULL;
            """, cancellationToken, ("$remote", replacementRemoteBoardId), ("$board", expected.BoardId),
            ("$now", now), ("$id", expected.Id));
        await tx.CommitAsync(cancellationToken);
        return link with { RemoteBoardId = replacementRemoteBoardId, Cursor = 0, LayoutHash = null,
            ActivityAfter = null, LastSyncUtc = null, LastError = null };
    }

    private async Task<BoardSyncLinkRecord?> ReadRebuildLinkAsync(SqliteConnection db, SqliteTransaction tx,
        string boardId, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = SyncLinkSelectSql + " WHERE l.BoardId=$board;";
        command.Parameters.AddWithValue("$board", boardId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSyncLink(reader) : null;
    }

    private async Task QueueSyncRebuildSnapshotsAsync(SqliteConnection db, SqliteTransaction tx,
        string projectPath, string boardId, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = CardSelectSql + " " + """
            WHERE c.ProjectPath=$project AND c.ColumnId IN (SELECT Id FROM BoardColumns WHERE BoardId=$board)
            ORDER BY c.Number;
            """;
        command.Parameters.AddWithValue("$project", projectPath);
        command.Parameters.AddWithValue("$board", boardId);
        var cards = new List<BoardCardRecord>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) cards.Add(ReadCard(reader));
        var now = DateTime.UtcNow;
        foreach (var card in cards)
        {
            var changes = new CardLogChanges();
            changes.Created(card);
            changes.SyncRecovery();
            var json = changes.ToJson();
            await InsertLogEntryAsync(db, tx, card.Id, BoardAuthor.System(), BoardCommentKinds.Created,
                "Local host snapshot for replacement hosted copy", json, card.CreatedUtc, ct);
            var deleted = await IsDeletedAsync(db, tx, card.Id, ct);
            if (!deleted)
                await InsertLogEntryAsync(db, tx, card.Id, BoardAuthor.System(), BoardCommentKinds.Change,
                    "Local host values for replacement hosted copy", json, now, ct);
            await InsertLogEntryAsync(db, tx, card.Id, BoardAuthor.System(), deleted ? "deleted" : "restored",
                deleted ? "Deleted on local host" : "Retained on local host", SyncRecoveryMarker, now, ct);
        }
    }

    /// <inheritdoc />
    public async Task CompleteSyncRebuildAsync(string rebuildId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        await ExecuteSharingAsync(db, tx, """
            UPDATE BoardSyncRebuilds SET Phase='complete', CompletedUTC=$now, UpdatedUTC=$now
            WHERE Id=$id AND Phase='upload' AND CompletedUTC IS NULL;
            """, cancellationToken, ("$id", rebuildId), ("$now", ToDb(DateTime.UtcNow)));
        await tx.CommitAsync(cancellationToken);
    }
}
