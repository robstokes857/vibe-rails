using VibeRails.Data.Sqlite;

namespace VibeRails.Services.BertV2;

public sealed class BertV2VectorStore : IBertV2VectorStore
{
    private readonly string _databasePath;

    public BertV2VectorStore(string databasePath)
    {
        _databasePath = databasePath;
        SqliteStorageErrors.Execute(() => BertVectorDatabase.Initialize(databasePath));
    }

    public bool ContainsCurrent(string id, string text) => SqliteStorageErrors.Execute(() =>
    {
        using var connection = BertVectorDatabase.Open(_databasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT EXISTS (
                SELECT 1 FROM {BertSearchSchema.DocumentTableName} AS d
                JOIN {BertSearchSchema.VectorTableName} AS v ON v.Id = d.Id
                WHERE d.Id = @id AND d.Text = @text
            );
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@text", text);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    });

    public void AddOrUpdate(string id, string text, float[] embedding) => SqliteStorageErrors.Execute(() =>
    {
        var serialized = BertVectorDatabase.Serialize(embedding);
        using var connection = BertVectorDatabase.Open(_databasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {BertSearchSchema.DocumentTableName} (Id, Text) VALUES (@id, @text)
                ON CONFLICT(Id) DO UPDATE SET Text = @text;
            DELETE FROM {BertSearchSchema.VectorTableName} WHERE Id = @id;
            INSERT INTO {BertSearchSchema.VectorTableName} (Id, Embedding)
                VALUES (@id, vec_f32(@embedding));
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@text", text);
        command.Parameters.AddWithValue("@embedding", serialized);
        command.ExecuteNonQuery();
        transaction.Commit();
    });

    // Connections are scoped to individual operations; retained for contract compatibility.
    public void Dispose() { }
}
