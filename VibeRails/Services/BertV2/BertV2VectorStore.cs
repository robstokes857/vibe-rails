using Microsoft.Data.Sqlite;
using VibeRails.Services.UserInOut;

namespace VibeRails.Services.BertV2;

public class BertV2VectorStore : IBertV2VectorStore
{
    // Bump when an upgrade needs to re-sweep the vector store with new filtering
    // rules. 1: corresponds to the introduction of InputEtlFilter at capture time;
    // any documents from pre-filter builds get InputEtlFilter.ContainsSecret-purged
    // on first open.
    private const int CurrentVectorStoreSchemaVersion = 1;

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
        ConfigureConcurrencyPragmas(_connection);
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

        MaybePurgeLegacySecretDocuments();
    }

    // WAL lets the read-only search connection in BertSearchDbService coexist with
    // the two write-singleton connections (per-message + session) on this same file
    // without serializing every reader behind a writer. busy_timeout gives any
    // contending writer a graceful wait window instead of an immediate SQLITE_BUSY.
    internal static void ConfigureConcurrencyPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        // PRAGMA journal_mode returns the new mode in a result row; ExecuteScalar
        // consumes it cleanly. ExecuteNonQuery also works but emits a warning on
        // some Microsoft.Data.Sqlite versions.
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteScalar();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One-time cleanup for pre-InputEtlFilter captures: walk every document and
    /// drop any whose stored Text now matches a secret pattern. Marker is stored
    /// in <c>PRAGMA user_version</c> on the shared vector DB file so we run this
    /// at most once per database upgrade.
    /// </summary>
    private void MaybePurgeLegacySecretDocuments()
    {
        int currentVersion;
        using (var readVersion = _connection.CreateCommand())
        {
            readVersion.CommandText = "PRAGMA user_version;";
            currentVersion = Convert.ToInt32(readVersion.ExecuteScalar() ?? 0);
        }
        if (currentVersion >= CurrentVectorStoreSchemaVersion)
            return;

        var idsToDelete = new List<string>();
        using (var read = _connection.CreateCommand())
        {
            read.CommandText = $"SELECT Id, Text FROM {BertSearchSchema.DocumentTableName};";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var text = reader.GetString(1);
                if (InputEtlFilter.ContainsSecret(text))
                    idsToDelete.Add(id);
            }
        }

        if (idsToDelete.Count > 0)
        {
            using var transaction = _connection.BeginTransaction();
            using (var deleteDoc = _connection.CreateCommand())
            using (var deleteVec = _connection.CreateCommand())
            {
                deleteDoc.Transaction = transaction;
                deleteVec.Transaction = transaction;
                deleteDoc.CommandText = $"DELETE FROM {BertSearchSchema.DocumentTableName} WHERE Id = @id;";
                deleteVec.CommandText = $"DELETE FROM {BertSearchSchema.VectorTableName} WHERE Id = @id;";
                var docId = deleteDoc.Parameters.Add("@id", SqliteType.Text);
                var vecId = deleteVec.Parameters.Add("@id", SqliteType.Text);
                foreach (var id in idsToDelete)
                {
                    docId.Value = id;
                    deleteDoc.ExecuteNonQuery();
                    vecId.Value = id;
                    deleteVec.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }

        using (var bump = _connection.CreateCommand())
        {
            bump.CommandText = $"PRAGMA user_version = {CurrentVectorStoreSchemaVersion};";
            bump.ExecuteNonQuery();
        }
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
