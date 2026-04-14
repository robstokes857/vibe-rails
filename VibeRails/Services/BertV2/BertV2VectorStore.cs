using Microsoft.Data.Sqlite;

namespace VibeRails.Services.BertV2;

public class BertV2VectorStore : IBertV2VectorStore
{
    private readonly SqliteConnection _connection;

    public BertV2VectorStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared");
        _connection.Open();
        _connection.EnableExtensions(true);
        _connection.LoadExtension(SqliteVec0PathResolver.GetPath());

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {BertSearchSchema.DocumentTableName} (
                Id TEXT PRIMARY KEY,
                Text TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {BertSearchSchema.VectorTableName} USING vec0(
                Id TEXT PRIMARY KEY,
                Embedding FLOAT[{BertSearchSchema.EmbeddingDimension}] distance_metric=cosine
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void AddOrUpdate(string id, string text, float[] embedding)
    {
        using var transaction = _connection.BeginTransaction();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                INSERT INTO {BertSearchSchema.DocumentTableName} (Id, Text) VALUES (@id, @text)
                ON CONFLICT(Id) DO UPDATE SET Text = @text
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@text", text);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {BertSearchSchema.VectorTableName} WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                INSERT INTO {BertSearchSchema.VectorTableName} (Id, Embedding)
                VALUES (@id, vec_f32(@embedding))
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@embedding", SerializeVector(embedding));
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose() => _connection.Dispose();
}
