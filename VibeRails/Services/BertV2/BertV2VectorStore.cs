using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.BertV2;

public record SearchResult(string Id, string Text, double Score);

public class BertV2VectorStore : IBertV2VectorStore
{
    private const int EmbeddingDimension = 384;
    private const string DocTable = "documents";
    private const string VecTable = "vec_documents";

    private readonly SqliteConnection _connection;

    public BertV2VectorStore(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared");
        _connection.Open();
        _connection.EnableExtensions(true);
        _connection.LoadExtension(GetVec0Path());

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {DocTable} (
                Id TEXT PRIMARY KEY,
                Text TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {VecTable} USING vec0(
                Id TEXT PRIMARY KEY,
                Embedding FLOAT[{EmbeddingDimension}] distance_metric=cosine
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
                INSERT INTO {DocTable} (Id, Text) VALUES (@id, @text)
                ON CONFLICT(Id) DO UPDATE SET Text = @text
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@text", text);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {VecTable} WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = $"""
                INSERT INTO {VecTable} (Id, Embedding)
                VALUES (@id, vec_f32(@embedding))
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@embedding", SerializeVector(embedding));
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public List<SearchResult> Search(float[] queryEmbedding, int topK)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT d.Id, d.Text, v.distance
            FROM {VecTable} AS v
            JOIN {DocTable} AS d ON v.Id = d.Id
            WHERE v.Embedding MATCH vec_f32(@query)
              AND k = @topK
            ORDER BY v.distance ASC
            """;
        cmd.Parameters.AddWithValue("@query", SerializeVector(queryEmbedding));
        cmd.Parameters.AddWithValue("@topK", topK);

        var results = new List<SearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SearchResult(
                reader.GetString(0),
                reader.GetString(1),
                1.0 - reader.GetDouble(2)));
        }
        return results;
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

    public void Dispose() => _connection.Dispose();
}
