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

    // board/21: pulled entries this version can never apply (an unknown kind, an identity that
    // conflicts, a card of another board). The cursor moves past them; the status view counts them.
    // board/22 adds Version, the desktop version that last tried the entry: a newer one tries again.
    private const string SkippedEntriesSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardSyncSkippedEntries (
            BoardId TEXT NOT NULL REFERENCES Boards(Id) ON DELETE CASCADE,
            EntryId TEXT NOT NULL,
            Seq INTEGER NOT NULL,
            CardKey TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Reason TEXT NOT NULL,
            SkippedUTC TEXT NOT NULL,
            PRIMARY KEY (BoardId, EntryId)
        );
        """;

    private const string SyncLinkSelectSql = """
        SELECT l.BoardId, l.RemoteBoardId, l.Cursor, l.Enabled, l.LayoutHash, l.LastSyncUTC, l.LastError,
               l.CreatedUTC, l.UpdatedUTC, b.ProjectPath, b.Name, l.DestinationKey, l.ActivitySchema, l.ActivityAfter
        FROM BoardSyncLinks l
        JOIN Boards b ON b.Id = l.BoardId
        """;

    // Only cards the server has been told about take part: an entry about a card without a
    // `created` entry would be a comment on a card the web has never seen. Every push first writes
    // a baseline for live cards that lack one (WriteSyncBaselineAsync), including cards an older
    // binary created while the board was published, so only cards deleted before they were
    // published stay local.
    private const string UnsentEntriesFromSql = """
        FROM BoardSyncLog m
        JOIN BoardCards c ON c.Id = m.CardId
        JOIN BoardColumns k ON k.Id = c.ColumnId
        LEFT JOIN BoardProjectKeys pk ON pk.ProjectPath = c.ProjectPath
        WHERE COALESCE(m.SyncBoardId, k.BoardId) = $board AND m.RemoteSeq IS NULL
          AND EXISTS (SELECT 1 FROM BoardSyncLog x WHERE x.CardId = c.Id AND COALESCE(x.SyncBoardId, k.BoardId) = $board AND x.Kind = 'created')
          AND NOT EXISTS (SELECT 1 FROM BoardSyncLog x WHERE x.CardId = c.Id AND COALESCE(x.SyncBoardId, k.BoardId) = $board AND x.Kind = 'created' AND x.RemoteSeq = -1)
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

    public async Task<IReadOnlyList<BoardRecord>> GetBoardsForSyncAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = BoardSelectSql + " ORDER BY ProjectPath, Position, Id;";
        var boards = new List<BoardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) boards.Add(ReadBoard(reader));
        return boards;
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
                INSERT INTO BoardSyncLinks (BoardId, RemoteBoardId, Cursor, Enabled, LayoutHash, LastSyncUTC, LastError, CreatedUTC, UpdatedUTC, DestinationKey, ActivitySchema, ActivityAfter)
                SELECT $board, $remote, $cursor, $enabled, $layout, $lastSync, $lastError, $created, $updated, $destination, $activitySchema, $activityAfter
                WHERE EXISTS (SELECT 1 FROM Boards WHERE Id = $board)
                ON CONFLICT(BoardId) DO UPDATE SET
                    RemoteBoardId = excluded.RemoteBoardId, Cursor = excluded.Cursor, Enabled = excluded.Enabled,
                    LayoutHash = excluded.LayoutHash, LastSyncUTC = excluded.LastSyncUTC, LastError = excluded.LastError,
                    UpdatedUTC = excluded.UpdatedUTC, DestinationKey = excluded.DestinationKey,
                    ActivitySchema = excluded.ActivitySchema, ActivityAfter = excluded.ActivityAfter;
                """;
            command.Parameters.AddWithValue("$board", link.BoardId);
            command.Parameters.AddWithValue("$remote", link.RemoteBoardId);
            command.Parameters.AddWithValue("$cursor", link.Cursor);
            command.Parameters.AddWithValue("$enabled", link.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$layout", (object?)link.LayoutHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$destination", (object?)link.DestinationKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$activitySchema", link.ActivitySchema);
            command.Parameters.AddWithValue("$activityAfter", (object?)link.ActivityAfter ?? DBNull.Value);
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

    // Live cards of the board that no `created` entry describes: cards from before board/14, and cards
    // an older binary created (it writes no Card Log) while the board was published.
    private static string CardsWithoutCreatedEntrySql => $"""
         WHERE c.ProjectPath = $project{ProjectPathCollation}
           AND c.ColumnId IN (SELECT k.Id FROM BoardColumns k WHERE k.BoardId = $board)
           AND c.DeletedUTC IS NULL
           AND NOT EXISTS (SELECT 1 FROM BoardSyncLog x WHERE x.CardId = c.Id AND COALESCE(x.SyncBoardId, $board) = $board AND x.Kind = 'created')
        """;

    public async Task<int> WriteSyncBaselineAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        // Every push asks, so the usual answer (nothing to write) must not take the write lock.
        if (await ScalarLongAsync(connection, null, $"SELECT EXISTS (SELECT 1 FROM BoardCards c {CardsWithoutCreatedEntrySql});",
                ("$project", project), cancellationToken, ("$board", boardId)) == 0)
            return 0;
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var lanes = (await ReadColumnsAsync(connection, transaction, project, boardId, cancellationToken))
            .ToDictionary(lane => lane.Id, lane => lane.Name, StringComparer.Ordinal);

        var cards = new List<BoardCardRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = CardSelectSql + CardsWithoutCreatedEntrySql + " ORDER BY c.CreatedUTC, c.Number;";
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
            UPDATE BoardComments SET
                RemoteSeq = CASE WHEN SyncBoardId IS NULL THEN -1 ELSE 0 END,
                TransferRemoteSeq = CASE WHEN SyncBoardId IS NOT NULL THEN -1 ELSE TransferRemoteSeq END
            WHERE Id = $entry AND (CASE WHEN SyncBoardId IS NULL THEN RemoteSeq ELSE TransferRemoteSeq END) IS NULL
              AND COALESCE(SyncBoardId, (SELECT k.BoardId FROM BoardCards c JOIN BoardColumns k ON k.Id = c.ColumnId WHERE c.Id = BoardComments.CardId)) = $board;
            """;
        command.Parameters.AddWithValue("$entry", entryId);
        command.Parameters.AddWithValue("$board", boardId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        command.CommandText = """
            INSERT OR IGNORE INTO BoardSyncRejectedFields (EntryId, Field)
            SELECT m.Id, j.key FROM BoardSyncLog m,
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
            SELECT COALESCE(MAX(m.RemoteSeq), 0) FROM BoardSyncLog m
            JOIN BoardCards c ON c.Id = m.CardId JOIN BoardColumns k ON k.Id = c.ColumnId
            WHERE COALESCE(m.SyncBoardId, k.BoardId) = $board AND m.RemoteSeq > 0;
            """, ("$board", boardId), cancellationToken);
    }

    public async Task<int> CountRejectedLogEntriesAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (int)await ScalarLongAsync(connection, null, """
            SELECT COUNT(*) FROM BoardSyncLog m JOIN BoardCards c ON c.Id = m.CardId
            JOIN BoardColumns k ON k.Id = c.ColumnId WHERE COALESCE(m.SyncBoardId, k.BoardId) = $board AND m.RemoteSeq = -1;
            """, ("$board", boardId), cancellationToken);
    }

    public async Task<IReadOnlyList<BoardSyncRejectedEntry>> GetRejectedLogEntriesAsync(string boardId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m.Id, COALESCE(c.CardKey, {CardPrefixSql} || '-' || c.Number), m.Kind
            FROM BoardSyncLog m JOIN BoardCards c ON c.Id = m.CardId
            JOIN BoardColumns k ON k.Id = c.ColumnId
            LEFT JOIN BoardProjectKeys pk ON pk.ProjectPath = c.ProjectPath
            WHERE COALESCE(m.SyncBoardId, k.BoardId) = $board AND m.RemoteSeq = -1 ORDER BY m.rowid DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
        var entries = new List<BoardSyncRejectedEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return entries;
    }

    public async Task<bool> RecordSkippedSyncEntryAsync(string boardId, BoardSyncSkippedEntry entry, string version, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // The board check and the write are one statement, like SaveSyncLinkAsync. A repeat is a later
        // attempt at the same entry: it records that attempt's version and reason and keeps the rest.
        command.CommandText = """
            INSERT INTO BoardSyncSkippedEntries (BoardId, EntryId, Seq, CardKey, Kind, Reason, SkippedUTC, Version)
            SELECT $board, $entry, $seq, $key, $kind, $reason, $now, $version
            WHERE EXISTS (SELECT 1 FROM Boards WHERE Id = $board)
            ON CONFLICT(BoardId, EntryId) DO UPDATE SET Reason = excluded.Reason, Version = excluded.Version;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$entry", entry.EntryId);
        command.Parameters.AddWithValue("$seq", entry.Seq);
        command.Parameters.AddWithValue("$key", entry.CardKey);
        command.Parameters.AddWithValue("$kind", entry.Kind);
        command.Parameters.AddWithValue("$reason", entry.Reason);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$version", version);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<long?> RetrySkippedSyncEntriesAsync(string boardId, string version, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // Every pull asks and the answer is almost always no, which must not take the write lock.
        if (EarliestToRetry(await ReadSkippedAttemptsAsync(connection, null, boardId, cancellationToken), version) is null)
            return null;
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var attempts = await ReadSkippedAttemptsAsync(connection, transaction, boardId, cancellationToken);
        if (EarliestToRetry(attempts, version) is not long earliest)
            return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Only ever back: the pull moves the cursor forward, entry by entry.
        command.CommandText = "UPDATE BoardSyncLinks SET Cursor = MIN(Cursor, $cursor) WHERE BoardId = $board RETURNING Cursor;";
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$cursor", earliest - 1);
        if (await command.ExecuteScalarAsync(cancellationToken) is not long cursor)
            return null;
        // Marked as tried by this version in the rewind's own transaction: the pull that follows gives each
        // entry its attempt, and one that is interrupted resumes from the cursor, which is already back.
        command.CommandText = "UPDATE BoardSyncSkippedEntries SET Version = $version WHERE BoardId = $board AND Version IS $tried;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$version", version);
        var tried = command.Parameters.Add("$tried", SqliteType.Text);
        foreach (var attempt in attempts.Where(a => TriedByEarlierVersion(a.Version, version)))
        {
            tried.Value = (object?)attempt.Version ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return cursor;
    }

    /// <summary>Each version that last tried some of the board's skipped entries, with the first sequence it tried.</summary>
    private static async Task<List<(string? Version, long FirstSeq)>> ReadSkippedAttemptsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string boardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Version, MIN(Seq) FROM BoardSyncSkippedEntries WHERE BoardId = $board GROUP BY Version;";
        command.Parameters.AddWithValue("$board", boardId);
        var attempts = new List<(string?, long)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            attempts.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1)));
        return attempts;
    }

    private static long? EarliestToRetry(IEnumerable<(string? Version, long FirstSeq)> attempts, string version) =>
        attempts.Where(a => TriedByEarlierVersion(a.Version, version)).Select(a => (long?)a.FirstSeq).Min();

    /// <summary>
    /// True when <paramref name="tried"/>, the version that last tried an entry, is earlier than
    /// <paramref name="current"/>. No version (a row from before board/22) or one that does not parse
    /// counts as earlier; the retry then records the current version, so that happens once. A current
    /// version that does not parse retries nothing, because it cannot tell that it is newer.
    /// </summary>
    private static bool TriedByEarlierVersion(string? tried, string current) =>
        ReleaseOf(current) is { } now && (tried is null || ReleaseOf(tried) is not { } then || then < now);

    // 10.11.2, 10.11.2-rc.1 or 10.11.2+abc: only the release numbers order versions here.
    private static Version? ReleaseOf(string value)
    {
        var end = value.IndexOfAny(['-', '+']);
        return Version.TryParse(end < 0 ? value : value[..end], out var release) ? release : null;
    }

    /// <summary>
    /// An entry a stamped write applies is no longer skipped: removed in the write's own transaction,
    /// which is how a newer version's retry of an entry an earlier one passed over is recorded.
    /// </summary>
    private static async Task ForgetSkippedEntryAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardSyncStamp stamp, CancellationToken cancellationToken)
    {
        if (stamp.BoardId is null) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM BoardSyncSkippedEntries WHERE BoardId = $board AND EntryId = $entry;";
        command.Parameters.AddWithValue("$board", stamp.BoardId);
        command.Parameters.AddWithValue("$entry", stamp.EntryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountSkippedSyncEntriesAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (int)await ScalarLongAsync(connection, null, "SELECT COUNT(*) FROM BoardSyncSkippedEntries WHERE BoardId = $board;",
            ("$board", boardId), cancellationToken);
    }

    public async Task<IReadOnlyList<BoardSyncSkippedEntry>> GetSkippedSyncEntriesAsync(string boardId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EntryId, Seq, CardKey, Kind, Reason FROM BoardSyncSkippedEntries
            WHERE BoardId = $board ORDER BY Seq DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
        var entries = new List<BoardSyncSkippedEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
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
        command.CommandText = """
            UPDATE BoardComments SET
                RemoteSeq = CASE WHEN SyncBoardId IS NULL THEN $seq ELSE 0 END,
                TransferRemoteSeq = CASE WHEN SyncBoardId IS NOT NULL THEN $seq ELSE TransferRemoteSeq END
            WHERE Id = $id;
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var seq = command.Parameters.Add("$seq", SqliteType.Integer);
        foreach (var (entryId, remoteSeq) in sent)
        {
            if (remoteSeq > 0)
            {
                // Compare the effective ledger: transferred entries have a separate sequence
                // while their legacy RemoteSeq stays local-only for older backends.
                if (await ScalarLongAsync(connection, transaction, """
                    SELECT COUNT(*) FROM BoardSyncLog existing
                    JOIN BoardCards c ON c.Id = existing.CardId JOIN BoardColumns k ON k.Id = c.ColumnId
                    WHERE (existing.Id = $id AND existing.RemoteSeq > 0 AND existing.RemoteSeq <> $seq)
                       OR (existing.Id <> $id AND existing.RemoteSeq > 0 AND existing.RemoteSeq = $seq AND COALESCE(existing.SyncBoardId, k.BoardId) = (
                           SELECT COALESCE(source.SyncBoardId, sourceColumn.BoardId) FROM BoardSyncLog source
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
                        SELECT 1 FROM BoardSyncLog rejected JOIN BoardSyncLog accepted ON accepted.CardId = rejected.CardId
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
                SELECT m.Id FROM BoardSyncLog m JOIN BoardCards c ON c.Id = m.CardId
                JOIN BoardColumns k ON k.Id = c.ColumnId WHERE COALESCE(m.SyncBoardId, k.BoardId) = $board);
            """;
        command.Parameters.AddWithValue("$board", boardId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        // Entries skipped on the old remote board say nothing about the new one.
        command.CommandText = "DELETE FROM BoardSyncSkippedEntries WHERE BoardId = $board;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            UPDATE BoardComments SET
                RemoteSeq = CASE WHEN SyncBoardId IS NULL THEN NULL ELSE 0 END,
                TransferRemoteSeq = NULL
            WHERE (CASE WHEN SyncBoardId IS NULL THEN RemoteSeq ELSE TransferRemoteSeq END) IS NOT NULL
              AND COALESCE(SyncBoardId, (SELECT k.BoardId FROM BoardCards c JOIN BoardColumns k ON k.Id = c.ColumnId WHERE c.Id = BoardComments.CardId)) = $board;
            """;
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<bool> HasLogEntryAsync(string entryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ScalarLongAsync(connection, null, "SELECT COUNT(*) FROM BoardSyncLog WHERE Id = $id;", ("$id", entryId), cancellationToken) > 0;
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
        // An unsent created entry by the system author is a publication baseline (WriteSyncBaselineAsync):
        // state the card already had, sent so the web sees the card, not a local edit. It protects no
        // field, so a web change pulled before a large board's baseline has all been pushed still applies.
        command.CommandText = """
            SELECT Changes FROM BoardSyncLog
            WHERE CardId = $card AND (SyncBoardId IS NULL OR SyncBoardId = (SELECT k.BoardId FROM BoardCards c JOIN BoardColumns k ON k.Id = c.ColumnId WHERE c.Id = $card)) AND (RemoteSeq IS NULL OR RemoteSeq > $seq)
              AND Kind IN ('created', 'change') AND Changes IS NOT NULL
              AND NOT (Kind = 'created' AND AuthorKind = 'system' AND RemoteSeq IS NULL);
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
            SELECT r.Field FROM BoardSyncRejectedFields r JOIN BoardSyncLog m ON m.Id = r.EntryId
            WHERE m.CardId = $card AND (m.SyncBoardId IS NULL OR m.SyncBoardId = (SELECT k.BoardId FROM BoardCards c JOIN BoardColumns k ON k.Id = c.ColumnId WHERE c.Id = $card));
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
        await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM BoardSyncLog WHERE Id = $id;", ("$id", stamp.EntryId), ct) > 0;

    private static async Task<bool> IsDeletedAsync(SqliteConnection connection, SqliteTransaction transaction, string cardId, CancellationToken ct) =>
        await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM BoardCards WHERE Id = $id AND DeletedUTC IS NOT NULL;", ("$id", cardId), ct) > 0;

    private static BoardCardPatch WithoutLockedFields(BoardCardPatch patch, IReadOnlySet<string> locked) => patch with
    {
        DisplayId = locked.Contains("displayId") ? null : patch.DisplayId,
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
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetInt32(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));
}
