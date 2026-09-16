using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.Services.UserInOut;

namespace VibeRails.Services.BertV2;

/// <summary>Shared schema and extension setup for both vector collections.</summary>
internal static class BertVectorDatabase
{
    public static SqliteConnection Open(string databasePath, bool readOnly = false)
    {
        var connection = SqliteConnectionFactory.Open(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString(), readOnly);
        try
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(SqliteVec0PathResolver.GetPath());
            connection.EnableExtensions(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static void Initialize(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var connection = Open(databasePath);
        SqliteMigrationRunner.RequireGenerationAtMost(connection, Generation, "vector");
        SqliteMigrationRunner.Apply(connection, "bert-vectors", 1, MigrationKind.Additive, (db, transaction) =>
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {BertSearchSchema.DocumentTableName} (
                    Id TEXT PRIMARY KEY,
                    Text TEXT NOT NULL
                );
                CREATE VIRTUAL TABLE IF NOT EXISTS {BertSearchSchema.VectorTableName} USING vec0(
                    Id TEXT PRIMARY KEY,
                    Embedding FLOAT[{BertSearchSchema.EmbeddingDimension}] distance_metric=cosine
                );
                CREATE TABLE IF NOT EXISTS {BertSearchSchema.SessionDocumentTableName} (
                    Id TEXT PRIMARY KEY,
                    SessionId TEXT NOT NULL,
                    ChunkIndex INTEGER NOT NULL,
                    Text TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_{BertSearchSchema.SessionDocumentTableName}_session
                    ON {BertSearchSchema.SessionDocumentTableName}(SessionId);
                CREATE VIRTUAL TABLE IF NOT EXISTS {BertSearchSchema.SessionVectorTableName} USING vec0(
                    Id TEXT PRIMARY KEY,
                    Embedding FLOAT[{BertSearchSchema.EmbeddingDimension}] distance_metric=cosine
                );
                """;
            command.ExecuteNonQuery();
            PurgeLegacySecretDocuments(db, transaction);
        });
        SqliteMigrationRunner.StampGeneration(connection, Generation);
    }

    /// <summary>
    /// Vector database generation (PRAGMA user_version). Generation 1 doubles as the legacy
    /// "secret documents purged" marker that <see cref="PurgeLegacySecretDocuments"/> checks, so it
    /// must never be lowered below 1.
    /// </summary>
    internal const int Generation = 1;

    private static void PurgeLegacySecretDocuments(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(command.ExecuteScalar() ?? 0) >= 1)
            return;

        var ids = new List<string>();
        command.CommandText = $"SELECT Id, Text FROM {BertSearchSchema.DocumentTableName};";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                if (InputEtlFilter.ContainsSecret(reader.GetString(1)))
                    ids.Add(reader.GetString(0));
        }

        command.CommandText = $"""
            DELETE FROM {BertSearchSchema.VectorTableName} WHERE Id = @id;
            DELETE FROM {BertSearchSchema.DocumentTableName} WHERE Id = @id;
            """;
        var parameter = command.Parameters.Add("@id", SqliteType.Text);
        foreach (var id in ids)
        {
            parameter.Value = id;
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = "PRAGMA user_version = 1;";
        command.ExecuteNonQuery();
    }

    public static byte[] Serialize(float[] vector)
    {
        if (vector.Length != BertSearchSchema.EmbeddingDimension)
            throw new ArgumentException($"Expected {BertSearchSchema.EmbeddingDimension} embedding dimensions.", nameof(vector));
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
