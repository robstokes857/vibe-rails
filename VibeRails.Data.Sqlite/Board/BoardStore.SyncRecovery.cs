using System.Data;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string SyncRecoveryMarker = """{"syncRecovery":{"to":true}}""";

    /// <inheritdoc />
    public async Task<bool> HasPendingSyncRecoveryAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ScalarLongAsync(connection, null, """
            SELECT EXISTS (SELECT 1 FROM BoardSyncLog m
                JOIN BoardCards c ON c.Id = m.CardId JOIN BoardColumns k ON k.Id = c.ColumnId
                WHERE COALESCE(m.SyncBoardId, k.BoardId) = $board AND m.RemoteSeq IS NULL
                  AND json_extract(CASE WHEN json_valid(m.Changes) THEN m.Changes ELSE '{}' END, '$.syncRecovery.to') = 1);
            """, ("$board", boardId), cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<BoardSyncLinkRecord?> ReconcileOwnedSyncHistoryAsync(BoardSyncLinkRecord expected,
        IReadOnlyList<BoardSyncHistoryEntry> history, long lastSeq, CancellationToken cancellationToken = default)
    {
        if (expected.Imported || lastSeq < 0) return null;
        var remote = new Dictionary<string, BoardSyncHistoryEntry>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        foreach (var entry in history)
        {
            if (entry.Seq <= 0 || entry.Seq > lastSeq || !sequences.Add(entry.Seq) || !remote.TryAdd(entry.EntryId, entry))
                throw new BoardValidationException("The server returned inconsistent recovery history; entries remain queued.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*) FROM BoardSyncLinks l JOIN Boards b ON b.Id = l.BoardId
            WHERE l.BoardId = $board AND b.ProjectPath = $project{ProjectPathCollation}
              AND l.RemoteBoardId = $remote AND l.DestinationKey = $destination AND l.Cursor = $cursor
              AND NOT EXISTS (SELECT 1 FROM BoardSharedOrigins o WHERE o.BoardId = l.BoardId);
            """;
        command.Parameters.AddWithValue("$board", expected.BoardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(expected.ProjectPath));
        command.Parameters.AddWithValue("$remote", expected.RemoteBoardId);
        command.Parameters.AddWithValue("$destination", (object?)expected.DestinationKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$cursor", expected.Cursor);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 1) return null;

        command.CommandText = """
            SELECT m.Id, m.CardId, m.Kind, m.RemoteSeq FROM BoardSyncLog m
            JOIN BoardCards c ON c.Id = m.CardId JOIN BoardColumns k ON k.Id = c.ColumnId
            WHERE COALESCE(m.SyncBoardId, k.BoardId) = $board AND (m.RemoteSeq IS NULL OR m.RemoteSeq > 0);
            """;
        var local = new List<(string Id, long? Seq)>();
        var forked = lastSeq < expected.Cursor;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                var seq = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
                if (remote.TryGetValue(id, out var entry)
                    && (entry.CardId != reader.GetString(1) || entry.Kind != reader.GetString(2)))
                    throw new BoardValidationException("The server changed an entry's immutable identity; entries remain queued.");
                if (seq is > 0 && (entry is null || entry.Seq != seq)) forked = true;
                local.Add((id, entry?.Seq));
            }
        }
        if (!forked) return null;

        // Rebuild only delivery receipts. Keep complete historical rows, local-only metadata,
        // rejected entries and their protected fields. Missing ordinary entries return to the outbox.
        command.CommandText = """
            UPDATE BoardComments SET
                RemoteSeq = CASE WHEN SyncBoardId IS NULL THEN $seq ELSE 0 END,
                TransferRemoteSeq = CASE WHEN SyncBoardId IS NOT NULL THEN $seq ELSE TransferRemoteSeq END
            WHERE Id = $id;
            """;
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        var sequenceParameter = command.Parameters.Add("$seq", SqliteType.Integer);
        foreach (var (id, seq) in local)
        {
            idParameter.Value = id;
            sequenceParameter.Value = (object?)seq ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        // Skipped-entry receipts describe the old sequence space too. Do not let a later
        // version retry rewind this repaired cursor into the discarded hosted projection.
        command.CommandText = "DELETE FROM BoardSyncSkippedEntries WHERE BoardId = $board;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Fresh baselines rebuild missing server cards. Final changes run AFTER requeued old
        // edits, so today's local values win even when the restored server retained newer web edits.
        command.CommandText = CardSelectSql + $"""
             WHERE c.ProjectPath = $project{ProjectPathCollation}
               AND c.ColumnId IN (SELECT Id FROM BoardColumns WHERE BoardId = $board)
               AND EXISTS (SELECT 1 FROM BoardSyncLog x WHERE x.CardId = c.Id AND x.Kind = 'created'
                   AND COALESCE(x.SyncBoardId, $board) = $board)
               AND NOT EXISTS (SELECT 1 FROM BoardSyncLog x WHERE x.CardId = c.Id AND x.Kind = 'created'
                   AND COALESCE(x.SyncBoardId, $board) = $board AND x.RemoteSeq = -1)
             ORDER BY c.Number;
            """;
        var cards = new List<BoardCardRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) cards.Add(ReadCard(reader));
        var now = DateTime.UtcNow;
        foreach (var card in cards)
        {
            var changes = new CardLogChanges();
            changes.Created(card);
            changes.SyncRecovery();
            var json = changes.ToJson();
            await InsertLogEntryAsync(connection, transaction, card.Id, BoardAuthor.System(), BoardCommentKinds.Created,
                "Local snapshot after hosted history recovery", json, now, cancellationToken);
            var deleted = await IsDeletedAsync(connection, transaction, card.Id, cancellationToken);
            if (!deleted)
                await InsertLogEntryAsync(connection, transaction, card.Id, BoardAuthor.System(), BoardCommentKinds.Change,
                    "Local values after hosted history recovery", json, now, cancellationToken);
            await InsertLogEntryAsync(connection, transaction, card.Id, BoardAuthor.System(),
                deleted ? BoardCommentKinds.Deleted : "restored",
                deleted ? "Deleted locally" : "Retained locally after hosted history recovery", SyncRecoveryMarker, now, cancellationToken);
        }

        // Skip the restored server's historical projection: it is the copy being repaired.
        // Current local values and discussion stay intact; subsequent new web events still pull normally.
        command.CommandText = """
            UPDATE BoardSyncLinks SET Cursor = $last, LayoutHash = NULL, ActivityAfter = NULL, UpdatedUTC = $now
            WHERE BoardId = $board;
            """;
        command.Parameters.AddWithValue("$last", lastSeq);
        command.Parameters.AddWithValue("$now", ToDb(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return expected with { Cursor = lastSeq, LayoutHash = null, ActivityAfter = null, UpdatedUtc = now };
    }
}
