using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

/// <summary>
/// Board sync with viberails.ai (VB-51): the <c>BoardSyncLinks</c> rows, the send ledger on
/// <c>BoardComments.RemoteSeq</c> and the stamped writes that apply pulled entries. The HTTP
/// exchange itself lives in the host (<c>Services/Board/Sync</c>); this partial only decides what
/// is unsent, records what the server accepted, and writes pulled entries under their remote ids.
/// </summary>
public sealed partial class BoardStore
{
    private const string RejectedFieldsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardSyncRejectedFields (
            EntryId TEXT NOT NULL REFERENCES BoardComments(Id) ON DELETE CASCADE,
            Field TEXT NOT NULL,
            PRIMARY KEY (EntryId, Field)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardComments_RemoteSeq ON BoardComments(RemoteSeq) WHERE RemoteSeq > 0;
        """;

    private const string SyncLinkSelectSql = """
        SELECT l.BoardId, l.RemoteBoardId, l.Cursor, l.Enabled, l.LayoutHash, l.LastSyncUTC, l.LastError,
               l.CreatedUTC, l.UpdatedUTC, b.ProjectPath, b.Name, l.DestinationKey
        FROM BoardSyncLinks l
        JOIN Boards b ON b.Id = l.BoardId
        """;

    // Only cards the server has been told about take part: an entry about a card without a
    // `created` entry (a pre-board/14 card the baseline never reached, or one deleted before the
    // board was published) would be a comment on a card the web has never seen.
    private const string UnsentEntriesFromSql = """
        FROM BoardComments m
        JOIN BoardCards c ON c.Id = m.CardId
        JOIN BoardColumns k ON k.Id = c.ColumnId
        LEFT JOIN BoardProjectKeys pk ON pk.ProjectPath = c.ProjectPath
        WHERE k.BoardId = $board AND m.RemoteSeq IS NULL
          AND EXISTS (SELECT 1 FROM BoardComments x WHERE x.CardId = c.Id AND x.Kind = 'created')
          AND NOT EXISTS (SELECT 1 FROM BoardComments x WHERE x.CardId = c.Id AND x.Kind = 'created' AND x.RemoteSeq = -1)
        """;

    public async Task<BoardSyncLinkRecord?> GetSyncLinkAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SyncLinkSelectSql + $" WHERE l.BoardId = $board AND b.ProjectPath = $project{ProjectPathCollation} LIMIT 1;";
        command.Parameters.AddWithValue("$board", boardId.Trim());
        command.Parameters.AddWithValue("$project", project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSyncLink(reader) : null;
    }

    public async Task<IReadOnlyList<BoardSyncLinkRecord>> GetSyncLinksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SyncLinkSelectSql + " ORDER BY b.ProjectPath, l.BoardId;";
        var links = new List<BoardSyncLinkRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            links.Add(ReadSyncLink(reader));
        return links;
    }

    public async Task<BoardSyncLinkRecord?> SaveSyncLinkAsync(BoardSyncLinkRecord link, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var now = DateTime.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            // The board check and the write are one statement, so a board deleted concurrently
            // cannot be left with a link. CreatedUTC is kept from the first save.
            command.CommandText = """
                INSERT INTO BoardSyncLinks (BoardId, RemoteBoardId, Cursor, Enabled, LayoutHash, LastSyncUTC, LastError, CreatedUTC, UpdatedUTC, DestinationKey)
                SELECT $board, $remote, $cursor, $enabled, $layout, $lastSync, $lastError, $created, $updated, $destination
                WHERE EXISTS (SELECT 1 FROM Boards WHERE Id = $board)
                ON CONFLICT(BoardId) DO UPDATE SET
                    RemoteBoardId = excluded.RemoteBoardId, Cursor = excluded.Cursor, Enabled = excluded.Enabled,
                    LayoutHash = excluded.LayoutHash, LastSyncUTC = excluded.LastSyncUTC, LastError = excluded.LastError,
                    UpdatedUTC = excluded.UpdatedUTC, DestinationKey = excluded.DestinationKey;
                """;
            command.Parameters.AddWithValue("$board", link.BoardId);
            command.Parameters.AddWithValue("$remote", link.RemoteBoardId);
            command.Parameters.AddWithValue("$cursor", link.Cursor);
            command.Parameters.AddWithValue("$enabled", link.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$layout", (object?)link.LayoutHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$destination", (object?)link.DestinationKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastSync", link.LastSyncUtc is DateTime sync ? ToDb(sync) : DBNull.Value);
            command.Parameters.AddWithValue("$lastError", (object?)link.LastError ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", ToDb(link.CreatedUtc == default ? now : link.CreatedUtc));
            command.Parameters.AddWithValue("$updated", ToDb(now));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return null;
        }
        await using var read = connection.CreateCommand();
        read.CommandText = SyncLinkSelectSql + " WHERE l.BoardId = $board LIMIT 1;";
        read.Parameters.AddWithValue("$board", link.BoardId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSyncLink(reader) : null;
    }

    public async Task<string> EnsureProjectKeyPrefixAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var prefix = await EnsureProjectKeyPrefixAsync(connection, transaction, project, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return prefix;
    }

    public async Task<int> WriteSyncBaselineAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var lanes = (await ReadColumnsAsync(connection, transaction, project, boardId, cancellationToken))
            .ToDictionary(lane => lane.Id, lane => lane.Name, StringComparer.Ordinal);

        var cards = new List<BoardCardRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = CardSelectSql + $"""
                 WHERE c.ProjectPath = $project{ProjectPathCollation}
                   AND c.ColumnId IN (SELECT k.Id FROM BoardColumns k WHERE k.BoardId = $board)
                   AND c.DeletedUTC IS NULL
                   AND NOT EXISTS (SELECT 1 FROM BoardComments x WHERE x.CardId = c.Id AND x.Kind = 'created')
                 ORDER BY c.CreatedUTC, c.Number;
                """;
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$board", boardId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                cards.Add(ReadCard(reader));
        }

        // The entry carries today's state at the card's creation time: the only history these
        // cards have, and the point every later comment and change sorts after.
        foreach (var card in cards)
        {
            var laneName = lanes.TryGetValue(card.ColumnId, out var name) ? name : card.ColumnId;
            await LogCardCreatedAsync(connection, transaction, card, laneName, BoardAuthor.System(), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return cards.Count;
    }

    public async Task<IReadOnlyList<BoardSyncOutboundEntry>> GetUnsentLogEntriesAsync(string boardId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Oldest first, so a card's `created` entry reaches the server before its changes and
        // comments; the server applies entries in the order it receives them.
        command.CommandText = $"""
            SELECT m.Id, m.CardId, m.AuthorKind, m.AuthorLabel, m.AuthorCli, m.SessionId, m.Body, m.CreatedUTC, m.Kind, m.Changes,
                   COALESCE(c.CardKey, {CardPrefixSql} || '-' || c.Number)
            {UnsentEntriesFromSql}
            ORDER BY CASE WHEN m.Kind = 'created' THEN 0 ELSE 1 END, m.rowid
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        var entries = new List<BoardSyncOutboundEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entry = new BoardCommentRecord(
                reader.GetString(0),
                reader.GetString(1),
                new BoardAuthor(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)),
                reader.GetString(6),
                ParseDb(reader.GetString(7)),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9));
            entries.Add(new BoardSyncOutboundEntry(entry, reader.GetString(10)));
        }
        return entries;
    }

    public async Task<int> CountUnsentLogEntriesAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (int)await ScalarLongAsync(connection, null, "SELECT COUNT(*) " + UnsentEntriesFromSql + ";", ("$board", boardId), cancellationToken);
    }

    public async Task<bool> RejectLogEntryAsync(string boardId, string entryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE BoardComments SET RemoteSeq = -1
            WHERE Id = $entry AND RemoteSeq IS NULL
              AND CardId IN (SELECT c.Id FROM BoardCards c JOIN BoardColumns k ON k.Id = c.ColumnId WHERE k.BoardId = $board);
            """;
        command.Parameters.AddWithValue("$entry", entryId);
        command.Parameters.AddWithValue("$board", boardId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        command.CommandText = """
            INSERT OR IGNORE INTO BoardSyncRejectedFields (EntryId, Field)
            SELECT m.Id, j.key FROM BoardComments m,
                 json_each(CASE WHEN json_valid(m.Changes) THEN m.Changes ELSE '{}' END) j
            WHERE m.Id = $entry AND m.Kind IN ('created', 'change');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<long> GetMaxAcknowledgedSequenceAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ScalarLongAsync(connection, null, """
            SELECT COALESCE(MAX(m.RemoteSeq), 0) FROM BoardComments m
            JOIN BoardCards c ON c.Id = m.CardId JOIN BoardColumns k ON k.Id = c.ColumnId
            WHERE k.BoardId = $board AND m.RemoteSeq > 0;
            """, ("$board", boardId), cancellationToken);
    }

    public async Task<int> CountRejectedLogEntriesAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (int)await ScalarLongAsync(connection, null, """
            SELECT COUNT(*) FROM BoardComments m JOIN BoardCards c ON c.Id = m.CardId
            JOIN BoardColumns k ON k.Id = c.ColumnId WHERE k.BoardId = $board AND m.RemoteSeq = -1;
            """, ("$board", boardId), cancellationToken);
    }

    public async Task<IReadOnlyList<BoardSyncRejectedEntry>> GetRejectedLogEntriesAsync(string boardId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m.Id, COALESCE(c.CardKey, {CardPrefixSql} || '-' || c.Number), m.Kind
            FROM BoardComments m JOIN BoardCards c ON c.Id = m.CardId
            JOIN BoardColumns k ON k.Id = c.ColumnId
            LEFT JOIN BoardProjectKeys pk ON pk.ProjectPath = c.ProjectPath
            WHERE k.BoardId = $board AND m.RemoteSeq = -1 ORDER BY m.rowid DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
        var entries = new List<BoardSyncRejectedEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return entries;
    }

    public async Task MarkLogEntriesSentAsync(IReadOnlyList<KeyValuePair<string, long>> sent, CancellationToken cancellationToken = default)
    {
        if (sent.Count == 0)
            return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE BoardComments SET RemoteSeq = $seq WHERE Id = $id;";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var seq = command.Parameters.Add("$seq", SqliteType.Integer);
        foreach (var (entryId, remoteSeq) in sent)
        {
            if (remoteSeq > 0)
            {
                // The explicit `RemoteSeq > 0` beside `= $seq` lets SQLite use the partial index
                // IX_BoardComments_RemoteSeq; a bound parameter alone cannot prove the index's
                // WHERE clause, and without it every acknowledgement scans the whole table.
                if (await ScalarLongAsync(connection, transaction, """
                    SELECT COUNT(*) FROM BoardComments existing
                    JOIN BoardCards c ON c.Id = existing.CardId JOIN BoardColumns k ON k.Id = c.ColumnId
                    WHERE (existing.Id = $id AND existing.RemoteSeq > 0 AND existing.RemoteSeq <> $seq)
                       OR (existing.Id <> $id AND existing.RemoteSeq > 0 AND existing.RemoteSeq = $seq AND k.BoardId = (
                           SELECT sourceColumn.BoardId FROM BoardComments source
                           JOIN BoardCards sourceCard ON sourceCard.Id = source.CardId
                           JOIN BoardColumns sourceColumn ON sourceColumn.Id = sourceCard.ColumnId WHERE source.Id = $id));
                    """, ("$id", entryId), cancellationToken, ("$seq", remoteSeq)) > 0)
                    throw new BoardValidationException("The server returned an invalid sync acknowledgement; entries remain queued.");
                // Only an ACK of a later local outbox row releases rejected field protection.
                // Pull writes never use this path, so a web change cannot erase that protection.
                await using var resolved = connection.CreateCommand();
                resolved.Transaction = transaction;
                resolved.CommandText = """
                    DELETE FROM BoardSyncRejectedFields
                    WHERE EXISTS (
                        SELECT 1 FROM BoardComments rejected JOIN BoardComments accepted ON accepted.CardId = rejected.CardId
                        JOIN json_each(CASE WHEN json_valid(accepted.Changes) THEN accepted.Changes ELSE '{}' END) j
                        WHERE rejected.Id = BoardSyncRejectedFields.EntryId AND accepted.Id = $id
                          AND accepted.RemoteSeq IS NULL AND accepted.rowid > rejected.rowid
                          AND accepted.Kind IN ('created', 'change') AND j.key = BoardSyncRejectedFields.Field);
                    """;
                resolved.Parameters.AddWithValue("$id", entryId);
                await resolved.ExecuteNonQueryAsync(cancellationToken);
            }
            id.Value = entryId;
            seq.Value = remoteSeq;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> ResetSentMarksAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM BoardSyncRejectedFields WHERE EntryId IN (
                SELECT m.Id FROM BoardComments m JOIN BoardCards c ON c.Id = m.CardId
                JOIN BoardColumns k ON k.Id = c.ColumnId WHERE k.BoardId = $board);
            """;
        command.Parameters.AddWithValue("$board", boardId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            UPDATE BoardComments SET RemoteSeq = NULL
            WHERE RemoteSeq IS NOT NULL
              AND CardId IN (SELECT c.Id FROM BoardCards c JOIN BoardColumns k ON k.Id = c.ColumnId WHERE k.BoardId = $board);
            """;
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<bool> HasLogEntryAsync(string entryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ScalarLongAsync(connection, null, "SELECT COUNT(*) FROM BoardComments WHERE Id = $id;", ("$id", entryId), cancellationToken) > 0;
    }

    public async Task<IReadOnlySet<string>> GetFieldsChangedAfterAsync(string cardId, long remoteSeq, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await GetFieldsChangedAfterAsync(connection, null, cardId, remoteSeq, cancellationToken);
    }

    private static async Task<IReadOnlySet<string>> GetFieldsChangedAfterAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string cardId, long remoteSeq, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Changes FROM BoardComments
            WHERE CardId = $card AND (RemoteSeq IS NULL OR RemoteSeq > $seq)
              AND Kind IN ('created', 'change') AND Changes IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$seq", remoteSeq);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            try
            {
                using var changes = JsonDocument.Parse(reader.GetString(0));
                if (changes.RootElement.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var property in changes.RootElement.EnumerateObject())
                    fields.Add(property.Name);
            }
            catch (JsonException)
            {
                // A malformed row protects nothing; the readable body is all it has.
            }
        }
        await reader.DisposeAsync();
        command.CommandText = """
            SELECT r.Field FROM BoardSyncRejectedFields r JOIN BoardComments m ON m.Id = r.EntryId
            WHERE m.CardId = $card;
            """;
        await using var rejected = await command.ExecuteReaderAsync(cancellationToken);
        while (await rejected.ReadAsync(cancellationToken)) fields.Add(rejected.GetString(0));
        return fields;
    }

    public async Task<BoardCardRecord?> CreateSyncedCardAsync(string projectPath, string cardId, string cardKey, NewBoardCard card, BoardAuthor author, BoardSyncStamp stamp, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        var number = ParseKeyNumber(cardKey);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var taken = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM BoardCards WHERE Id = $id OR CardKey = $key;",
            ("$id", cardId), cancellationToken, ("$key", cardKey));
        if (taken > 0)
        {
            var existing = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken, includeDeleted: true);
            if (existing is null || existing.Key != cardKey)
                throw new BoardValidationException("The remote card identity conflicts with a local card.");
            RequireSyncBoard(existing, stamp);
            if (!await HasSyncStampAsync(connection, transaction, stamp, cancellationToken))
                await LogCardCreatedAsync(connection, transaction, existing, "", author, cancellationToken, stamp);
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        // Existing legacy cards may receive a baseline, but every newly imported card needs
        // the immutable random key. Check after the identity lookup in this transaction.
        if (!BoardKeys.TryParseStored(cardKey, out var storedKey) || storedKey != cardKey)
            throw new BoardValidationException("A new remote card requires a stored random key.");
        if (stamp.BoardId is { } boardId && card.BoardId != boardId)
            throw new BoardValidationException("A synced card must belong to its published board.");
        var created = await InsertCardAsync(connection, transaction, project, card, author, cancellationToken,
            new SyncedCardIdentity(cardId, cardKey, number), stamp);
        await ReconcileMissingSyncedLaneAsync(connection, transaction, created, stamp, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public Task<BoardCardRecord?> UpdateSyncedCardAsync(string projectPath, string cardId, BoardCardPatch patch, BoardAuthor author, BoardSyncStamp stamp, CancellationToken cancellationToken = default) =>
        UpdateCardCoreAsync(projectPath, cardId, patch, author, stamp, cancellationToken);

    public Task<bool> DeleteSyncedCardAsync(string projectPath, string cardId, BoardAuthor author, BoardSyncStamp stamp, CancellationToken cancellationToken = default) =>
        DeleteCardCoreAsync(projectPath, cardId, author, stamp, cancellationToken);

    public Task<BoardCommentRecord?> AddSyncedCommentAsync(string projectPath, string cardId, BoardAuthor author, string body, string kind, BoardSyncStamp stamp, CancellationToken cancellationToken = default)
    {
        if (!BoardCommentKinds.IsValid(kind))
            throw new BoardValidationException($"Unsupported Card Log kind: {kind}");
        return InsertCommentRowAsync(projectPath, cardId, author, body, kind, cancellationToken, stamp);
    }

    private static void RequireSyncBoard(BoardCardRecord card, BoardSyncStamp stamp)
    {
        if (stamp.BoardId is { } boardId && card.BoardId != boardId)
            throw new BoardValidationException("The remote card belongs to a different local board.");
    }

    private static void RequireLocalBoardTransfer(string source, string destination)
    {
        if (source != destination)
            throw new BoardValidationException("Cards can move between lanes on the same board. Create a new card to work on another board.");
    }

    private static async Task ReconcileMissingSyncedLaneAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardCardRecord card, BoardSyncStamp? stamp, CancellationToken ct)
    {
        if (stamp?.Changes is null) return;
        using var changes = JsonDocument.Parse(stamp.Changes);
        if (!changes.RootElement.TryGetProperty("lane", out var lane) || !lane.TryGetProperty("to", out var to)
            || to.ValueKind != JsonValueKind.String || to.GetString() == card.ColumnId) return;
        var oldLane = to.GetString()!;
        if (await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM BoardColumns WHERE Id = $lane;", ("$lane", oldLane), ct) > 0) return;
        // Keep the remote event and queue a correction to the surviving local lane atomically.
        var correction = new CardLogChanges();
        correction.Lane(oldLane, card.ColumnId, "removed lane", "surviving lane");
        await InsertLogEntryAsync(connection, transaction, card.Id, BoardAuthor.System(), BoardCommentKinds.Change,
            correction.Summary, correction.ToJson(), DateTime.UtcNow, ct);
    }

    private static async Task<bool> HasSyncStampAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardSyncStamp stamp, CancellationToken ct) =>
        await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM BoardComments WHERE Id = $id;", ("$id", stamp.EntryId), ct) > 0;

    private static async Task<bool> IsDeletedAsync(SqliteConnection connection, SqliteTransaction transaction, string cardId, CancellationToken ct) =>
        await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM BoardCards WHERE Id = $id AND DeletedUTC IS NOT NULL;", ("$id", cardId), ct) > 0;

    private static BoardCardPatch WithoutLockedFields(BoardCardPatch patch, IReadOnlySet<string> locked) => patch with
    {
        Title = locked.Contains("title") ? null : patch.Title,
        Description = locked.Contains("description") ? null : patch.Description,
        Type = locked.Contains("type") ? null : patch.Type,
        Priority = locked.Contains("priority") ? null : patch.Priority,
        Points = locked.Contains("points") ? null : patch.Points,
        ClearPoints = !locked.Contains("points") && patch.ClearPoints,
        Assignee = locked.Contains("assignee") ? null : patch.Assignee,
        ClearAssignee = !locked.Contains("assignee") && patch.ClearAssignee,
        Tags = locked.Contains("tags") ? null : patch.Tags,
        Blocked = locked.Contains("blocked") ? null : patch.Blocked,
        Flagged = locked.Contains("flagged") ? null : patch.Flagged,
        ColumnId = locked.Contains("lane") ? null : patch.ColumnId
    };

    /// <summary>The trailing number of <c>PREFIX-RRRRR-n</c> or <c>PREFIX-n</c>; 0 when the key has none.</summary>
    private static int ParseKeyNumber(string cardKey)
    {
        var dash = cardKey.LastIndexOf('-');
        return dash >= 0 && int.TryParse(cardKey.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
    }

    private static BoardSyncLinkRecord ReadSyncLink(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetInt32(3) != 0,
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : ParseDb(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        ParseDb(reader.GetString(7)),
        ParseDb(reader.GetString(8)),
        reader.GetString(9),
        reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11));
}
