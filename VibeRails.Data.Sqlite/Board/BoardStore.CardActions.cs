using System.Data;
using VibeRails.DTOs;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private static string DeletedCommentChanges(string commentId) =>
        "{\"deletedComment\":{\"to\":" + JsonSerializer.Serialize(commentId, StorageJsonSerializerContext.Default.String) + "}}";

    private static async Task ApplyCommentDeletionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string cardId, string? changes, DateTime now, CancellationToken ct)
    {
        if (changes is null) return;
        using var document = JsonDocument.Parse(changes);
        if (!document.RootElement.TryGetProperty("deletedComment", out var field)) return;
        if (!field.TryGetProperty("to", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 64 } commentId)
            throw new BoardValidationException("Invalid deleted comment identity.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO BoardDeletedComments (CommentId, DeletedUTC)
            SELECT Id, $now FROM BoardComments WHERE Id = $comment AND CardId = $card AND Kind IN ('comment', 'note');
            """;
        command.Parameters.AddWithValue("$comment", commentId);
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$now", ToDb(now));
        await command.ExecuteNonQueryAsync(ct);
    }
    private const string CardActionsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardDeletedComments (
            CommentId TEXT PRIMARY KEY REFERENCES BoardComments(Id) ON DELETE CASCADE,
            DeletedUTC TEXT NOT NULL
        );
        """;

    // Only an explicitly transferred event uses the new ledger. Keep the legacy value local-only,
    // including when an older backend resets delivery after reconnecting to a hosted board.
    private const string TransferDeliverySchemaSql = """
        CREATE INDEX IF NOT EXISTS IX_BoardComments_TransferRemoteSeq ON BoardComments(TransferRemoteSeq) WHERE SyncBoardId IS NOT NULL;
        CREATE VIEW IF NOT EXISTS BoardSyncLog AS
        SELECT rowid, Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind, Changes,
            SyncBoardId, DiscussionHidden,
            CASE WHEN SyncBoardId IS NULL THEN RemoteSeq ELSE TransferRemoteSeq END AS RemoteSeq
        FROM BoardComments;
        CREATE TRIGGER IF NOT EXISTS TR_BoardComments_KeepTransferLocal
        AFTER UPDATE OF RemoteSeq ON BoardComments
        WHEN NEW.SyncBoardId IS NOT NULL AND NEW.RemoteSeq IS NOT 0
        BEGIN
            UPDATE BoardComments SET RemoteSeq = 0 WHERE Id = NEW.Id;
        END;
        """;

    /// <summary>Hides a discussion entry without removing its retained data or sync identity.</summary>
    public async Task<bool> DeleteCommentAsync(string projectPath, string cardId, string commentId,
        BoardAuthor author, CancellationToken cancellationToken = default)
    {
        if (author.Kind != BoardAuthor.UserKind)
            throw new BoardValidationException("Only a user can delete comments.");
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO BoardDeletedComments (CommentId, DeletedUTC)
            SELECT Id, $now FROM BoardComments WHERE Id = $comment AND CardId = $card AND Kind IN ('comment', 'note');
            """;
        var now = DateTime.UtcNow;
        command.Parameters.AddWithValue("$now", ToDb(now));
        command.Parameters.AddWithValue("$comment", commentId);
        command.Parameters.AddWithValue("$card", card.Id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        var changes = DeletedCommentChanges(commentId);
        await InsertLogEntryAsync(connection, transaction, card.Id, author, BoardCommentKinds.Change,
            "Deleted a comment", changes, now, cancellationToken);
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Copies source content and rails, then retires the source in the same transaction.</summary>
    public async Task<BoardCardRecord?> MergeCardsAsync(string projectPath, string sourceId, string targetId,
        CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var source = await ReadCardAsync(connection, transaction, project, sourceId, cancellationToken);
        var target = await ReadCardAsync(connection, transaction, project, targetId, cancellationToken);
        if (source is null || target is null) return null;
        if (source.Id == target.Id) throw new BoardValidationException("Choose a different card to merge into.");

        var attachmentIds = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Id FROM BoardAttachments WHERE CardId = $source;";
            read.Parameters.AddWithValue("$source", source.Id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) attachmentIds.Add(reader.GetString(0), NewId("att"));
        }
        var targetFiles = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM BoardAttachments WHERE CardId = $card;", ("$card", target.Id), cancellationToken);
        if (targetFiles + attachmentIds.Count > BoardAttachmentData.MaxAttachmentsPerCard)
            throw new BoardValidationException($"The merged card would exceed {BoardAttachmentData.MaxAttachmentsPerCard} attachments. Remove unwanted files first.");

        string Remap(string text)
        {
            foreach (var (oldId, newId) in attachmentIds)
                text = text.Replace("attachment:" + oldId, "attachment:" + newId, StringComparison.Ordinal);
            return text;
        }
        var description = target.Description + (target.Description.Length == 0 ? "" : "\n\n")
            + $"Merged from {source.DisplayId} · {source.Title}\n" + Remap(source.Description);
        if (description.Length > 100_000)
            throw new BoardValidationException("The merged description would exceed 100,000 characters. Shorten it before merging.");

        foreach (var (oldId, newId) in attachmentIds)
        {
            await using var copy = connection.CreateCommand();
            copy.Transaction = transaction;
            copy.CommandText = """
                INSERT INTO BoardAttachments (Id, CardId, Name, MimeType, Bytes, DataUrl, CreatedUTC)
                SELECT $new, $target, Name, MimeType, Bytes, DataUrl, CreatedUTC FROM BoardAttachments WHERE Id = $old;
                INSERT INTO BoardAttachmentContents (AttachmentId, Content)
                SELECT $new, Content FROM BoardAttachmentContents WHERE AttachmentId = $old;
                """;
            copy.Parameters.AddWithValue("$new", newId);
            copy.Parameters.AddWithValue("$old", oldId);
            copy.Parameters.AddWithValue("$target", target.Id);
            await copy.ExecuteNonQueryAsync(cancellationToken);
        }
        await CopyDiscussionAsync(connection, transaction, source.Id, target.Id, Remap, cancellationToken);

        await using (var copy = connection.CreateCommand())
        {
            copy.Transaction = transaction;
            copy.CommandText = $"""
                INSERT OR IGNORE INTO BoardAdditionalCardSessions (SessionId, CardId, TabId, Selection, Cli, DisplayName, Origin, CreatedUTC)
                SELECT SessionId, $target, TabId, Selection, Cli, DisplayName, Origin, CreatedUTC FROM {AllSessionsSql}
                WHERE CardId = $source AND SessionId NOT IN (SELECT SessionId FROM {AllSessionsSql} WHERE CardId = $target);
                INSERT OR IGNORE INTO BoardCommits (CardId, Sha, Author, Message, CommittedUTC, LinkedUTC)
                SELECT $target, Sha, Author, Message, CommittedUTC, LinkedUTC FROM BoardCommits WHERE CardId = $source;
                INSERT OR IGNORE INTO BoardCommitSnapshots (CardId, Sha, SnapshotJson)
                SELECT $target, Sha, SnapshotJson FROM BoardCommitSnapshots WHERE CardId = $source;
                INSERT OR IGNORE INTO BoardCardLinks (CardId, LinkedCardId)
                SELECT MIN($target, c.Id), MAX($target, c.Id) FROM BoardCardLinks l JOIN BoardCards c
                  ON c.Id = CASE WHEN l.CardId = $source THEN l.LinkedCardId ELSE l.CardId END
                WHERE (l.CardId = $source OR l.LinkedCardId = $source) AND c.Id <> $target
                  AND c.DeletedUTC IS NULL AND c.ProjectPath = $project{ProjectPathCollation};
                UPDATE BoardCards SET Description = $description, UpdatedUTC = $now WHERE Id = $target;
                INSERT INTO BoardHandoffs(Id,CardId,Json,CreatedUTC)
                SELECT 'handoff_' || lower(hex(randomblob(16))), $target, Json, CreatedUTC
                FROM BoardHandoffs WHERE CardId=$source;
                UPDATE BoardCards SET DeletedUTC = $now, UpdatedUTC = $now WHERE Id = $source;
                DELETE FROM BoardPendingAutomations WHERE CardId = $source;
                DELETE FROM BoardPendingAdditionalAutomations WHERE CardId = $source;
                """;
            copy.Parameters.AddWithValue("$source", source.Id);
            copy.Parameters.AddWithValue("$target", target.Id);
            copy.Parameters.AddWithValue("$project", project);
            copy.Parameters.AddWithValue("$description", description);
            copy.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
            await copy.ExecuteNonQueryAsync(cancellationToken);
        }
        var now = DateTime.UtcNow;
        var after = target with { Description = description, UpdatedUtc = now };
        await LogCardChangedAsync(connection, transaction, target, after, null, null, BoardAuthor.User(), cancellationToken);
        await InsertLogEntryAsync(connection, transaction, target.Id, BoardAuthor.User(), BoardCommentKinds.Comment,
            $"Merged {source.DisplayId} ({source.Key}) · {source.Title} into this card. Its description, comments, attachments, sessions, commits and card links were copied. Destination settings were kept.", null, now, cancellationToken);
        await LogCardDeletedAsync(connection, transaction, source.Id, BoardAuthor.User(), now, cancellationToken);
        await TouchCardAsync(connection, transaction, target.Id, cancellationToken);
        await RenumberColumnAsync(connection, transaction, source.ColumnId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadCardAsync(connection, null, project, target.Id, cancellationToken);
    }

    private static async Task CopyDiscussionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string source, string target, Func<string, string> mapBody, CancellationToken ct)
    {
        var rows = new List<BoardCommentRecord>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT Id, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC FROM BoardComments m
                WHERE CardId = $source AND Kind IN ('comment', 'note') AND DiscussionHidden = 0
                  AND NOT EXISTS (SELECT 1 FROM BoardDeletedComments d WHERE d.CommentId = m.Id)
                ORDER BY CreatedUTC, Id;
                """;
            read.Parameters.AddWithValue("$source", source);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(new BoardCommentRecord(reader.GetString(0), source,
                new BoardAuthor(reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)),
                reader.GetString(5), ParseDb(reader.GetString(6))));
        }
        foreach (var row in rows)
        {
            // Discussion permits 50K characters, unlike the short History summary helper.
            await using var copy = connection.CreateCommand();
            copy.Transaction = transaction;
            copy.CommandText = """
                INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind)
                VALUES ($id, $card, $kind, $label, $cli, $session, $body, $created, 'comment');
                """;
            copy.Parameters.AddWithValue("$id", NewId("cm"));
            copy.Parameters.AddWithValue("$card", target);
            copy.Parameters.AddWithValue("$kind", row.Author.Kind);
            copy.Parameters.AddWithValue("$label", row.Author.Label);
            copy.Parameters.AddWithValue("$cli", (object?)row.Author.Cli ?? DBNull.Value);
            copy.Parameters.AddWithValue("$session", (object?)row.Author.SessionId ?? DBNull.Value);
            copy.Parameters.AddWithValue("$body", mapBody(row.Body));
            copy.Parameters.AddWithValue("$created", ToDb(row.CreatedUtc));
            await copy.ExecuteNonQueryAsync(ct);
        }
    }

    // Delivery marks are board-scoped. Keep old events on their original board and publish a
    // fresh baseline/discussion on the destination; never reuse another board's sequence numbers.
    private static async Task TransferCardLogAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardCardRecord before, BoardCardRecord after, BoardAuthor author, CancellationToken ct)
    {
        if (before.BoardId == after.BoardId) return;
        await using var bind = connection.CreateCommand();
        bind.Transaction = transaction;
        bind.CommandText = "UPDATE BoardComments SET SyncBoardId = $board, TransferRemoteSeq = RemoteSeq, RemoteSeq = 0 WHERE CardId = $card AND SyncBoardId IS NULL;";
        bind.Parameters.AddWithValue("$card", before.Id);
        bind.Parameters.AddWithValue("$board", before.BoardId);
        await bind.ExecuteNonQueryAsync(ct);
        await LogCardCreatedAsync(connection, transaction, after, "", author, ct);
        var snapshot = new CardLogChanges();
        snapshot.Created(after);
        await InsertLogEntryAsync(connection, transaction, after.Id, author, BoardCommentKinds.Change,
            "Moved to this board", snapshot.ToJson(), DateTime.UtcNow, ct);
        // A return to a board must revive its earlier soft-deleted projection. The hosted wire
        // already supports restored. This event has a new id, making lost-ACK retries harmless.
        await InsertLogEntryAsync(connection, transaction, after.Id, author, "restored", "Moved to this board", null, DateTime.UtcNow, ct);
        await CopyDiscussionAsync(connection, transaction, before.Id, after.Id, text => text, ct);
        var retiredComments = new List<string>();
        bind.CommandText = "SELECT Id FROM BoardComments WHERE CardId = $card AND SyncBoardId = $board AND Kind IN ('comment', 'note') AND DiscussionHidden = 0;";
        await using (var reader = await bind.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) retiredComments.Add(reader.GetString(0));
        foreach (var id in retiredComments)
            await InsertLogEntryAsync(connection, transaction, before.Id, BoardAuthor.System(), BoardCommentKinds.Change,
                "Discussion moved to another board", DeletedCommentChanges(id), DateTime.UtcNow, ct, syncBoardId: before.BoardId);
        bind.CommandText = "UPDATE BoardComments SET DiscussionHidden = 1 WHERE CardId = $card AND SyncBoardId = $board AND Kind IN ('comment', 'note');";
        await bind.ExecuteNonQueryAsync(ct);
        // Route the departure to the old board even though the card now belongs to the new one.
        await InsertLogEntryAsync(connection, transaction, before.Id, author, BoardCommentKinds.Deleted,
            "Moved to another board", null, DateTime.UtcNow, ct, syncBoardId: before.BoardId);
    }
}
