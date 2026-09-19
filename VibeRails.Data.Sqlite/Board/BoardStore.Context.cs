using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<BoardContextSettingsRecord?> GetContextSettingsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        // A LEFT JOIN distinguishes a scoped board with no settings from a missing/foreign board.
        return await ReadContextSettingsAsync(connection, null, project, boardId, cancellationToken);
    }

    public async Task<BoardContextSettingsRecord?> SaveContextSettingsAsync(string projectPath, string boardId, BoardContextSettings context, int expectedRevision, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var current = await ReadContextSettingsAsync(connection, transaction, project, boardId, cancellationToken);
        if (current is null) return null;
        if (current.Revision != expectedRevision)
            throw new BoardConflictException("Board context changed while you were editing. Reopen settings to load the latest version; your draft has not been saved.");
        var updated = new BoardContextSettingsRecord(context, checked(current.Revision + 1));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO BoardContextSettings (BoardId, ContextJson, Revision) VALUES ($id, $json, $revision)
            ON CONFLICT(BoardId) DO UPDATE SET ContextJson = excluded.ContextJson, Revision = excluded.Revision;
            """;
        command.Parameters.AddWithValue("$id", boardId.Trim());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(context, StorageJsonSerializerContext.Default.BoardContextSettings));
        command.Parameters.AddWithValue("$revision", updated.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task<BoardContextSettingsRecord?> ReadContextSettingsAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string boardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT s.ContextJson, s.Revision FROM Boards b
            LEFT JOIN BoardContextSettings s ON s.BoardId = b.Id
            WHERE b.ProjectPath = $project{ProjectPathCollation} AND b.Id = $id;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$id", boardId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return reader.IsDBNull(0)
            ? new(BoardContextSettings.Empty, 0)
            : new(JsonSerializer.Deserialize(reader.GetString(0), StorageJsonSerializerContext.Default.BoardContextSettings)!, reader.GetInt32(1));
    }

    private const string ContextSettingsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardContextSettings (
            BoardId TEXT PRIMARY KEY REFERENCES Boards(Id) ON DELETE CASCADE,
            ContextJson TEXT NOT NULL,
            Revision INTEGER NOT NULL
        );
        """;
}
