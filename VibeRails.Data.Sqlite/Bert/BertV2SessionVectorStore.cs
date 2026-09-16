using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.BertV2;

public sealed class BertV2SessionVectorStore : IBertV2SessionVectorStore
{
    private readonly string _databasePath;

    public BertV2SessionVectorStore(string databasePath)
    {
        _databasePath = databasePath;
        SqliteStorageErrors.Execute(() => BertVectorDatabase.Initialize(databasePath));
    }

    public bool ContainsCurrentSession(string sessionId, IReadOnlyList<string> chunks) => SqliteStorageErrors.Execute(() =>
    {
        using var connection = BertVectorDatabase.Open(_databasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.ChunkIndex, d.Text, v.Id
            FROM {BertSearchSchema.SessionDocumentTableName} AS d
            LEFT JOIN {BertSearchSchema.SessionVectorTableName} AS v ON v.Id = d.Id
            WHERE d.SessionId = @sessionId
            ORDER BY d.ChunkIndex;
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);
        using var reader = command.ExecuteReader();
        var index = 0;
        while (reader.Read())
        {
            if (index >= chunks.Count || reader.GetInt32(0) != index ||
                !string.Equals(reader.GetString(1), chunks[index], StringComparison.Ordinal) || reader.IsDBNull(2))
                return false;
            index++;
        }
        return index == chunks.Count;
    });

    public void AddOrUpdate(string sessionId, int chunkIndex, string text, float[] embedding) => SqliteStorageErrors.Execute(() =>
    {
        using var connection = BertVectorDatabase.Open(_databasePath);
        using var transaction = connection.BeginTransaction();
        WriteChunk(connection, transaction, sessionId, new(chunkIndex, text, embedding));
        transaction.Commit();
    });

    public void ReplaceSession(string sessionId, IReadOnlyList<BertSessionChunkWrite> chunks) => SqliteStorageErrors.Execute(() =>
    {
        using var connection = BertVectorDatabase.Open(_databasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM {BertSearchSchema.SessionVectorTableName}
                WHERE Id IN (SELECT Id FROM {BertSearchSchema.SessionDocumentTableName} WHERE SessionId = @sessionId);
            DELETE FROM {BertSearchSchema.SessionDocumentTableName} WHERE SessionId = @sessionId;
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.ExecuteNonQuery();
        foreach (var chunk in chunks)
            WriteChunk(connection, transaction, sessionId, chunk);
        transaction.Commit();
    });

    private static void WriteChunk(SqliteConnection connection, SqliteTransaction transaction, string sessionId, BertSessionChunkWrite chunk)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {BertSearchSchema.SessionDocumentTableName} (Id, SessionId, ChunkIndex, Text)
                VALUES (@id, @sessionId, @chunkIndex, @text)
                ON CONFLICT(Id) DO UPDATE SET SessionId = @sessionId, ChunkIndex = @chunkIndex, Text = @text;
            DELETE FROM {BertSearchSchema.SessionVectorTableName} WHERE Id = @id;
            INSERT INTO {BertSearchSchema.SessionVectorTableName} (Id, Embedding)
                VALUES (@id, vec_f32(@embedding));
            """;
        command.Parameters.AddWithValue("@id", BertSessionDocumentId.Create(sessionId, chunk.ChunkIndex));
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@chunkIndex", chunk.ChunkIndex);
        command.Parameters.AddWithValue("@text", chunk.Text);
        command.Parameters.AddWithValue("@embedding", BertVectorDatabase.Serialize(chunk.Embedding));
        command.ExecuteNonQuery();
    }

    // Connections are scoped to individual operations; retained for contract compatibility.
    public void Dispose() { }
}
