using System.Text.Json;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<IReadOnlyList<string>> GetSyncActivityCardIdsAsync(string projectPath, string boardId,
        string? after, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.Id FROM BoardCards c JOIN BoardColumns l ON l.Id = c.ColumnId
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND l.BoardId = $board
              AND c.DeletedUTC IS NULL AND ($after IS NULL OR c.Id > $after)
            ORDER BY c.Id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20));
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<BoardSyncActivityRecord?> GetSyncActivityAsync(string projectPath, string boardId,
        string cardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, project, cardId, cancellationToken);
        if (card is null || card.BoardId != boardId) return null;
        var attachments = new List<BoardAttachmentMetadata>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, CardId, Name, MimeType, Bytes, CreatedUTC FROM BoardAttachments WHERE CardId = $card ORDER BY CreatedUTC, Id LIMIT 41;";
            command.Parameters.AddWithValue("$card", card.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                attachments.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4), ParseDb(reader.GetString(5))));
        }
        return new(await ReadSessionsAsync(connection, card.Id, cancellationToken, limit: 201, newestFirst: true),
            await ReadCommitsAsync(connection, card.Id, cancellationToken, limit: 201), attachments,
            await ReadLinkedCardsAsync(connection, project, card.Id, cancellationToken, limit: 101));
    }

    public async Task<byte[]?> GetSyncAttachmentContentAsync(string projectPath, string cardId,
        string attachmentId, int maxBytes, CancellationToken cancellationToken = default)
    {
        maxBytes = Math.Clamp(maxBytes, 0, 1024 * 1024);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.Content, CASE WHEN c.Content IS NULL THEN a.DataUrl ELSE NULL END
            FROM BoardAttachments a LEFT JOIN BoardAttachmentContents c ON c.AttachmentId = a.Id
            WHERE a.CardId = $card AND a.Id = $attachment AND a.Bytes BETWEEN 0 AND $max
              AND ((c.Content IS NOT NULL AND length(c.Content) <= $max AND length(c.Content) = a.Bytes)
                OR (c.Content IS NULL AND length(a.DataUrl) <= $encoded))
              AND EXISTS (SELECT 1 FROM BoardCards b WHERE b.Id = a.CardId
                AND b.ProjectPath = $project{ProjectPathCollation} AND b.DeletedUTC IS NULL);
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$attachment", attachmentId);
        command.Parameters.AddWithValue("$max", maxBytes);
        command.Parameters.AddWithValue("$encoded", (maxBytes + 2) / 3 * 4 + 256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var content = reader.IsDBNull(0) ? BoardAttachmentData.DecodeDataUrl(reader.GetString(1)) : (byte[])reader.GetValue(0);
        return content.Length <= maxBytes ? content : null;
    }

    public async Task<SandboxDiffResponse?> GetSyncCommitSnapshotAsync(string projectPath, string cardId,
        string sha, int maxChars, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.SnapshotJson FROM BoardCommitSnapshots s
            WHERE s.CardId = $card AND s.Sha = $sha AND length(s.SnapshotJson) <= $max
              AND EXISTS (SELECT 1 FROM BoardCards b WHERE b.Id = s.CardId
                AND b.ProjectPath = $project{ProjectPathCollation} AND b.DeletedUTC IS NULL);
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$sha", sha);
        command.Parameters.AddWithValue("$max", Math.Clamp(maxChars, 0, 8 * 1024 * 1024));
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize(json, StorageJsonSerializerContext.Default.SandboxDiffResponse);
    }
}
