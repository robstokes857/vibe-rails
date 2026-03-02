using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Bert;

/// <summary>
/// SQLite-backed vector store for BERT embeddings using sqlite-vec.
/// </summary>
public sealed class BertSqliteVectorStore : IDisposable
{
    private const int EmbeddingDimension = 384;
    private const string TableName = "bert_input_documents";
    private const string VecTableName = "vec_bert_input_documents";

    private readonly SqliteConnection _connection;

    public BertSqliteVectorStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared");
        _connection.Open();

        _connection.EnableExtensions(true);
        _connection.LoadExtension(GetVec0Path());

        InitializeTables();
    }

    public void AddOrUpdate(string id, string text, float[] embedding)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Document id is required", nameof(id));

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required", nameof(text));

        if (embedding is null || embedding.Length != EmbeddingDimension)
            throw new ArgumentException($"Embedding must be {EmbeddingDimension} dimensions", nameof(embedding));

        using var transaction = _connection.BeginTransaction();

        try
        {
            using (var docCmd = _connection.CreateCommand())
            {
                docCmd.Transaction = transaction;
                docCmd.CommandText = $@"
                    INSERT INTO {TableName} (Id, Text) VALUES (@id, @text)
                    ON CONFLICT(Id) DO UPDATE SET Text = @text";
                docCmd.Parameters.AddWithValue("@id", id);
                docCmd.Parameters.AddWithValue("@text", text);
                docCmd.ExecuteNonQuery();
            }

            using (var deleteVecCmd = _connection.CreateCommand())
            {
                deleteVecCmd.Transaction = transaction;
                deleteVecCmd.CommandText = $"DELETE FROM {VecTableName} WHERE Id = @id";
                deleteVecCmd.Parameters.AddWithValue("@id", id);
                deleteVecCmd.ExecuteNonQuery();
            }

            using (var insertVecCmd = _connection.CreateCommand())
            {
                insertVecCmd.Transaction = transaction;
                insertVecCmd.CommandText = $@"
                    INSERT INTO {VecTableName} (Id, Embedding)
                    VALUES (@id, vec_f32(@embedding))";
                insertVecCmd.Parameters.AddWithValue("@id", id);
                insertVecCmd.Parameters.AddWithValue("@embedding", SerializeVector(embedding));
                insertVecCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void InitializeTables()
    {
        using var cmd = _connection.CreateCommand();

        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {TableName} (
                Id TEXT PRIMARY KEY,
                Text TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();

        cmd.CommandText = $@"
            CREATE VIRTUAL TABLE IF NOT EXISTS {VecTableName} USING vec0(
                Id TEXT PRIMARY KEY,
                Embedding FLOAT[{EmbeddingDimension}] distance_metric=cosine
            );";
        cmd.ExecuteNonQuery();
    }

    private static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string GetVec0Path()
    {
        var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win-x64"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : RuntimeInformation.OSArchitecture == Architecture.Arm64
                    ? "linux-arm64"
                    : "linux-x64";

        var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ".dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? ".dylib"
                : ".so";

        var candidate = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "vec0");
        if (File.Exists(candidate + ext))
            return candidate;

        return "vec0";
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
