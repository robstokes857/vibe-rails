using System.Data;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;



public sealed partial class BoardStore
{
    static partial void EnsureAttachmentSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        var hasDeleted = SqliteSchema.HasColumn(connection, transaction, "BoardAttachments", "DeletedUTC");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = (hasDeleted ? "" : "ALTER TABLE BoardAttachments ADD COLUMN DeletedUTC TEXT;\n") + """
            CREATE TABLE IF NOT EXISTS BoardAttachmentContents (
                AttachmentId TEXT PRIMARY KEY REFERENCES BoardAttachments(Id) ON DELETE CASCADE,
                Content BLOB NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Only the count of <em>current</em> files is bounded, and removed files never count against
    /// it. There is no byte budget: when there was one, it counted history-retained bytes that
    /// nothing could ever reclaim, so a few mistaken uploads permanently exhausted a card.
    /// </summary>
    private static async Task ValidateAttachmentCountAsync(SqliteConnection connection, SqliteTransaction transaction, string cardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM BoardAttachments WHERE CardId = $card AND DeletedUTC IS NULL;";
        command.Parameters.AddWithValue("$card", cardId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) >= BoardAttachmentData.MaxAttachmentsPerCard)
            throw new BoardValidationException($"A card can hold at most {BoardAttachmentData.MaxAttachmentsPerCard} current attachments.");
    }

    public async Task<BoardAttachmentRecord?> AddAttachmentContentAsync(string projectPath, string cardId, string name, string mimeType, byte[] content, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null) return null;
        await ValidateAttachmentCountAsync(connection, transaction, card.Id, cancellationToken);
        // Keep tiny safe raster previews compatible with existing inline comment images.
        // Every file's original bytes are stored separately; metadata stays bounded.
        var dataUrl = content.Length <= 300 * 1024 && mimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp"
            ? $"data:{mimeType};base64,{Convert.ToBase64String(content)}" : string.Empty;
        var record = new BoardAttachmentRecord(NewId("att"), card.Id, name, mimeType, content.LongLength, dataUrl, DateTime.UtcNow);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardAttachments (Id, CardId, Name, MimeType, Bytes, DataUrl, CreatedUTC)
                VALUES ($id, $card, $name, $mime, $bytes, $data, $created);
                INSERT INTO BoardAttachmentContents (AttachmentId, Content) VALUES ($id, $content);
                """;
            insert.Parameters.AddWithValue("$id", record.Id);
            insert.Parameters.AddWithValue("$card", record.CardId);
            insert.Parameters.AddWithValue("$name", record.Name);
            insert.Parameters.AddWithValue("$mime", record.MimeType);
            insert.Parameters.AddWithValue("$bytes", record.Bytes);
            insert.Parameters.AddWithValue("$data", record.DataUrl);
            insert.Parameters.AddWithValue("$created", ToDb(record.CreatedUtc));
            insert.Parameters.Add("$content", SqliteType.Blob).Value = content;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AppendDescriptionRevisionAsync(connection, transaction, card, card.Description, "attachments", BoardAuthor.User(), null, cancellationToken);
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<BoardAttachmentContent?> GetAttachmentContentAsync(string projectPath, string idOrKey, string attachmentId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, project, idOrKey, cancellationToken);
        if (card is null) return null;
        await using var command = connection.CreateCommand();
        // Deleted rows remain readable only through their original, project-scoped card so
        // a session's historical description can still open its exact original attachments.
        command.CommandText = """
            SELECT a.Id, a.CardId, a.Name, a.MimeType, a.Bytes, a.DataUrl, a.CreatedUTC, c.Content
            FROM BoardAttachments a LEFT JOIN BoardAttachmentContents c ON c.AttachmentId = a.Id
            WHERE a.CardId = $card AND a.Id = $attachment
                AND (a.DeletedUTC IS NULL OR EXISTS (
                    SELECT 1 FROM BoardDescriptionRevisionAttachments r
                    WHERE r.CardId = a.CardId AND r.AttachmentId = a.Id));
            """;
        command.Parameters.AddWithValue("$card", card.Id);
        command.Parameters.AddWithValue("$attachment", attachmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var record = new BoardAttachmentRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt64(4), reader.GetString(5), ParseDb(reader.GetString(6)));
        var bytes = reader.IsDBNull(7) ? BoardAttachmentData.DecodeDataUrl(record.DataUrl) : (byte[])reader.GetValue(7);
        return new BoardAttachmentContent(record, bytes);
    }
}
