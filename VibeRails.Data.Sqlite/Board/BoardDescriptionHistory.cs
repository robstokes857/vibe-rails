using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;



public sealed partial class BoardStore
{
    private static void EnsureDescriptionSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS BoardCardOptions (
                CardId TEXT PRIMARY KEY REFERENCES BoardCards(Id) ON DELETE CASCADE,
                OptionsJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BoardDescriptionRevisions (
                CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
                Revision INTEGER NOT NULL,
                Description TEXT NOT NULL,
                CreatedUTC TEXT NOT NULL,
                Source TEXT NOT NULL,
                AuthorKind TEXT NOT NULL,
                AuthorLabel TEXT NOT NULL,
                AuthorCli TEXT NULL,
                AuthorSessionId TEXT NULL,
                PRIMARY KEY (CardId, Revision)
            );
            CREATE TABLE IF NOT EXISTS BoardDescriptionSessionEvents (
                CardId TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                SessionId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                Status TEXT NOT NULL,
                CreatedUTC TEXT NOT NULL,
                UpdatedUTC TEXT NOT NULL,
                Message TEXT NULL,
                PRIMARY KEY (CardId, Revision, SessionId, Kind),
                FOREIGN KEY (CardId, Revision) REFERENCES BoardDescriptionRevisions(CardId, Revision) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS BoardDescriptionRevisionAttachments (
                CardId TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                AttachmentId TEXT NOT NULL REFERENCES BoardAttachments(Id) ON DELETE CASCADE,
                PRIMARY KEY (CardId, Revision, AttachmentId),
                FOREIGN KEY (CardId, Revision) REFERENCES BoardDescriptionRevisions(CardId, Revision) ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Gives every card lacking history an "imported" revision 1, plus that revision's attachment
    /// manifest. Idempotent (guarded by NOT EXISTS / INSERT OR IGNORE) and re-run on every startup
    /// by ReconcileDerivedRows, because cards can arrive from an older build that never wrote
    /// history at all. Requires a $now parameter.
    /// </summary>
    internal const string DescriptionBaselineSql = """
        INSERT INTO BoardDescriptionRevisions
            (CardId, Revision, Description, CreatedUTC, Source, AuthorKind, AuthorLabel)
        SELECT c.Id, 1, c.Description, $now, 'imported', 'user', 'Existing card'
        FROM BoardCards c WHERE NOT EXISTS (SELECT 1 FROM BoardDescriptionRevisions r WHERE r.CardId = c.Id);
        INSERT OR IGNORE INTO BoardDescriptionRevisionAttachments (CardId, Revision, AttachmentId)
        SELECT r.CardId, r.Revision, a.Id FROM BoardDescriptionRevisions r
        JOIN BoardAttachments a ON a.CardId = r.CardId AND a.DeletedUTC IS NULL
        WHERE r.Source = 'imported' AND r.CreatedUTC = $now;
        """;

    private static async Task WriteBaseLlmOptionsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string cardId, BaseLlmOptions? options, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = options is null
            ? "DELETE FROM BoardCardOptions WHERE CardId = $card;"
            : "INSERT INTO BoardCardOptions (CardId, OptionsJson) VALUES ($card, $json) ON CONFLICT(CardId) DO UPDATE SET OptionsJson = excluded.OptionsJson;";
        command.Parameters.AddWithValue("$card", cardId);
        if (options is not null)
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(options, StorageJsonSerializerContext.Default.BaseLlmOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Called inside the card/attachment write transaction. The same immutable revision also owns
    // its attachment manifest; removing a file from the current card never rewrites that manifest.
    private static async Task<int> AppendDescriptionRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardCardRecord card, string description, string source, BoardAuthor author,
        IReadOnlyList<string>? activeSessionIds, CancellationToken cancellationToken)
    {
        var revision = checked((int)await ScalarLongAsync(connection, transaction,
            "SELECT COALESCE(MAX(Revision), 0) + 1 FROM BoardDescriptionRevisions WHERE CardId = $card;",
            ("$card", card.Id), cancellationToken));
        var now = ToDb(DateTime.UtcNow);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO BoardDescriptionRevisions
                    (CardId, Revision, Description, CreatedUTC, Source, AuthorKind, AuthorLabel, AuthorCli, AuthorSessionId)
                VALUES ($card, $revision, $description, $now, $source, $kind, $label, $cli, $session);
                INSERT INTO BoardDescriptionRevisionAttachments (CardId, Revision, AttachmentId)
                SELECT $card, $revision, Id FROM BoardAttachments WHERE CardId = $card AND DeletedUTC IS NULL;
                """;
            command.Parameters.AddWithValue("$card", card.Id);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$kind", author.Kind);
            command.Parameters.AddWithValue("$label", author.Label);
            command.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
            command.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var sessionId in (activeSessionIds ?? []).Distinct(StringComparer.Ordinal))
            await InsertSessionEventAsync(connection, transaction, card.Id, revision, sessionId, "updated", "recorded", null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(author.SessionId))
            await InsertSessionEventAsync(connection, transaction, card.Id, revision, author.SessionId, "updated", "recorded", null, cancellationToken);
        return revision;
    }

    /// <summary>
    /// Read-only, so it runs without a transaction: a Serializable one here took state.db's write
    /// lock (BEGIN IMMEDIATE) just to read, and opening the history rail is a common, idle action.
    /// Attachment rows come back without their data URL — a revision's manifest can name the same
    /// file as every other revision, and returning the bytes once per pair made a card with a few
    /// screenshots and a dozen edits ship megabytes per open. Callers fetch content by id.
    /// </summary>
    public async Task<BoardDescriptionHistoryResponse?> GetDescriptionHistoryAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, NormalizeProjectPath(projectPath), idOrKey, cancellationToken);
        if (card is null) return null;
        var revisions = new List<BoardDescriptionRevisionDto>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Revision, Description, CreatedUTC, Source, AuthorKind, AuthorLabel, AuthorCli, AuthorSessionId
                FROM BoardDescriptionRevisions WHERE CardId = $card ORDER BY Revision DESC;
                """;
            command.Parameters.AddWithValue("$card", card.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                revisions.Add(new BoardDescriptionRevisionDto(reader.GetInt32(0), reader.GetString(1), ParseDb(reader.GetString(2)), reader.GetString(3),
                    new BoardAuthorDto(reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)), [], []));
        }
        var byRevision = revisions.ToDictionary(r => r.Revision);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Revision, SessionId, Kind, Status, CreatedUTC, UpdatedUTC, Message FROM BoardDescriptionSessionEvents WHERE CardId = $card ORDER BY CreatedUTC, SessionId, Kind;";
            command.Parameters.AddWithValue("$card", card.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (byRevision.TryGetValue(reader.GetInt32(0), out var revision))
                    revision.Sessions.Add(new BoardDescriptionSessionDto(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        ParseDb(reader.GetString(4)), ParseDb(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.Revision, a.Id, a.CardId, a.Name, a.MimeType, a.Bytes, a.CreatedUTC
                FROM BoardDescriptionRevisionAttachments r JOIN BoardAttachments a ON a.Id = r.AttachmentId
                WHERE r.CardId = $card ORDER BY a.CreatedUTC;
                """;
            command.Parameters.AddWithValue("$card", card.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (byRevision.TryGetValue(reader.GetInt32(0), out var revision))
                    revision.Attachments.Add(BoardAttachmentData.ToDto(new BoardAttachmentRecord(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), reader.GetInt64(5), string.Empty, ParseDb(reader.GetString(6)))));
        }
        return new BoardDescriptionHistoryResponse(card.Id, card.DescriptionRevision, revisions);
    }

    /// <summary>
    /// Records that a session launched from, edited, or read this exact revision. Re-reading a
    /// revision already recorded is the most frequent board event there is, so the common path
    /// stays a pure read: the write only happens the first time, and there is no surrounding
    /// transaction to hold state.db's write lock open around it.
    /// </summary>
    public async Task<bool> RecordDescriptionSessionAsync(string projectPath, string cardId, int revision, string sessionId, string kind, CancellationToken cancellationToken = default)
    {
        if (kind is not ("launch" or "read" or "updated"))
            throw new ArgumentException("Unknown description session event kind.", nameof(kind));
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        if (card is null || !await HasRevisionAsync(connection, null, card.Id, revision, cancellationToken)) return false;
        if (await HasSessionEventAsync(connection, card.Id, revision, sessionId, kind, cancellationToken)) return false;
        // A reader that belongs to another card is noted as such, so the card can show "read by an
        // agent working VB-3" without that read binding the session to this card.
        var note = kind == "read" ? await DescribeReaderOriginAsync(connection, card.Id, sessionId, cancellationToken) : null;
        return await InsertSessionEventAsync(connection, null, card.Id, revision, sessionId, kind, "recorded", note, cancellationToken);
    }

    private static async Task<bool> HasRevisionAsync(SqliteConnection connection, SqliteTransaction? transaction, string cardId, int revision, CancellationToken cancellationToken) =>
        await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM BoardDescriptionRevisions WHERE CardId = $card AND Revision = $revision;",
            ("$card", cardId), cancellationToken, ("$revision", revision)) > 0;

    private static async Task<bool> HasSessionEventAsync(SqliteConnection connection, string cardId, int revision, string sessionId, string kind, CancellationToken cancellationToken) =>
        await ScalarLongAsync(connection, null,
            "SELECT COUNT(*) FROM BoardDescriptionSessionEvents WHERE CardId = $card AND Revision = $revision AND SessionId = $session AND Kind = $kind;",
            ("$card", cardId), cancellationToken, ("$revision", revision), ("$session", sessionId), ("$kind", kind)) > 0;

    /// <summary>The card a reading session is actually linked to, when that is not this one.</summary>
    private static async Task<string?> DescribeReaderOriginAsync(SqliteConnection connection, string cardId, string sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Number FROM BoardCardSessions s JOIN BoardCards c ON c.Id = s.CardId
            WHERE s.SessionId = $session AND s.CardId <> $card LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$card", cardId);
        var number = await command.ExecuteScalarAsync(cancellationToken);
        return number is null or DBNull ? null : $"working {BoardKeys.Format(Convert.ToInt32(number))}";
    }

    private static async Task<bool> InsertSessionEventAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string cardId, int revision, string sessionId, string kind, string status, string? message, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A read is recorded even for a session this card does not own: reading a card is not a
        // claim on it, and refusing to record the read was the only reason reads had to link
        // first. launch/updated describe this card's own sessions, so they keep the requirement.
        // The read branch still guards on the revision existing, inside the one statement: OR
        // IGNORE suppresses UNIQUE/PRIMARY KEY conflicts but NOT foreign-key violations, and with
        // no surrounding transaction the card can be deleted between the caller's check and this
        // insert — which would throw and turn an already-successful card read into a FAIL.
        command.CommandText = kind == "read"
            ? """
              INSERT OR IGNORE INTO BoardDescriptionSessionEvents (CardId, Revision, SessionId, Kind, Status, CreatedUTC, UpdatedUTC, Message)
              SELECT $card, $revision, $session, $kind, $status, $now, $now, $message
              WHERE EXISTS (SELECT 1 FROM BoardDescriptionRevisions WHERE CardId = $card AND Revision = $revision);
              """
            : """
              INSERT OR IGNORE INTO BoardDescriptionSessionEvents (CardId, Revision, SessionId, Kind, Status, CreatedUTC, UpdatedUTC, Message)
              SELECT $card, $revision, $session, $kind, $status, $now, $now, $message
              WHERE EXISTS (SELECT 1 FROM BoardCardSessions WHERE CardId = $card AND SessionId = $session);
              """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
