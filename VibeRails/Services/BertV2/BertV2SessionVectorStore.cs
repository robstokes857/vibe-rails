using Microsoft.Data.Sqlite;

namespace VibeRails.Services.BertV2;

public class BertV2SessionVectorStore : IBertV2SessionVectorStore
{
    private readonly SqliteConnection _connection;

    public BertV2SessionVectorStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared");
        _connection.Open();
        BertV2VectorStore.ConfigureConcurrencyPragmas(_connection);
        _connection.EnableExtensions(true);
        _connection.LoadExtension(SqliteVec0PathResolver.GetPath());

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {BertSearchSchema.SessionDocumentTableName} (
                Id TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                ChunkIndex INTEGER NOT NULL,
                Text TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS idx_{BertSearchSchema.SessionDocumentTableName}_session ON {BertSearchSchema.SessionDocumentTableName}(SessionId)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {BertSearchSchema.SessionVectorTableName} USING vec0(
                Id TEXT PRIMARY KEY,
                Embedding FLOAT[{BertSearchSchema.EmbeddingDimension}] distance_metric=cosine
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void AddOrUpdate(string sessionId, int chunkIndex, string text, float[] embedding)
    {
        var id = BertSessionDocumentId.Create(sessionId, chunkIndex);

        using var transaction = _connection.BeginTransaction();
        WriteChunk(transaction, id, sessionId, chunkIndex, text, embedding);
        transaction.Commit();
    }

    /// <summary>
    /// Replace every chunk belonging to <paramref name="sessionId"/> in a single
    /// transaction. The session ends with exactly the chunks supplied — or, if the
    /// transaction rolls back, with the chunks it had before. Used by
    /// <c>BertV2SessionEmbeddingService.CaptureSession</c> so a mid-loop failure
    /// can't leave a half-embedded session searchable.
    /// </summary>
    public void ReplaceSession(string sessionId, IReadOnlyList<BertSessionChunkWrite> chunks)
    {
        using var transaction = _connection.BeginTransaction();

        // Delete every existing chunk for the session — pull ids from the doc
        // table first so we know which rows to remove from the vector table.
        var existingIds = new List<string>();
        using (var read = _connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT Id FROM {BertSearchSchema.SessionDocumentTableName} WHERE SessionId = @sessionId;";
            read.Parameters.AddWithValue("@sessionId", sessionId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
                existingIds.Add(reader.GetString(0));
        }
        if (existingIds.Count > 0)
        {
            using var deleteDoc = _connection.CreateCommand();
            using var deleteVec = _connection.CreateCommand();
            deleteDoc.Transaction = transaction;
            deleteVec.Transaction = transaction;
            deleteDoc.CommandText = $"DELETE FROM {BertSearchSchema.SessionDocumentTableName} WHERE Id = @id;";
            deleteVec.CommandText = $"DELETE FROM {BertSearchSchema.SessionVectorTableName} WHERE Id = @id;";
            var docParam = deleteDoc.Parameters.Add("@id", SqliteType.Text);
            var vecParam = deleteVec.Parameters.Add("@id", SqliteType.Text);
            foreach (var id in existingIds)
            {
                docParam.Value = id;
                deleteDoc.ExecuteNonQuery();
                vecParam.Value = id;
                deleteVec.ExecuteNonQuery();
            }
        }

        foreach (var chunk in chunks)
        {
            var id = BertSessionDocumentId.Create(sessionId, chunk.ChunkIndex);
            WriteChunk(transaction, id, sessionId, chunk.ChunkIndex, chunk.Text, chunk.Embedding);
        }

        transaction.Commit();
    }

    private void WriteChunk(SqliteTransaction transaction, string id, string sessionId, int chunkIndex, string text, float[] embedding)
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                INSERT INTO {BertSearchSchema.SessionDocumentTableName} (Id, SessionId, ChunkIndex, Text)
                VALUES (@id, @sessionId, @chunkIndex, @text)
                ON CONFLICT(Id) DO UPDATE SET
                    SessionId = @sessionId,
                    ChunkIndex = @chunkIndex,
                    Text = @text
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@chunkIndex", chunkIndex);
            cmd.Parameters.AddWithValue("@text", text);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {BertSearchSchema.SessionVectorTableName} WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                INSERT INTO {BertSearchSchema.SessionVectorTableName} (Id, Embedding)
                VALUES (@id, vec_f32(@embedding))
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@embedding", SerializeVector(embedding));
            cmd.ExecuteNonQuery();
        }
    }

    private static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose() => _connection.Dispose();
}

public readonly record struct BertSessionChunkWrite(int ChunkIndex, string Text, float[] Embedding);
