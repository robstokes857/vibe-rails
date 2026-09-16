using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Data.Abstractions;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

public sealed class BertVectorStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bert-storage-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "vectors.db");

    [Fact]
    public void ReplayedInput_SkipsInference_AndChangedTextEmbedsAgain()
    {
        using var store = new BertV2VectorStore(DatabasePath);
        var embedder = CreateEmbedder();
        var service = new BertV2InputService(embedder.Object, store);
        service.Capture("session", 1, "Explain the migration runner");
        service.Capture("session", 1, "Explain the migration runner");
        embedder.Verify(x => x.GenerateEmbedding(It.IsAny<string>()), Times.Once);

        service.Capture("session", 1, "Explain the connection factory");
        embedder.Verify(x => x.GenerateEmbedding(It.IsAny<string>()), Times.Exactly(2));
        Assert.True(store.ContainsCurrent("session:1", "Explain the connection factory"));
        Assert.False(store.ContainsCurrent("session:1", "Explain the migration runner"));
    }

    [Fact]
    public void ReplayedInput_WithMissingVector_RepairsIt()
    {
        using var store = new BertV2VectorStore(DatabasePath);
        var embedder = CreateEmbedder();
        var service = new BertV2InputService(embedder.Object, store);
        service.Capture("session", 1, "Explain the migration runner");
        ExecuteSql("DELETE FROM vec_bert_input_documents WHERE Id = 'session:1';");
        service.Capture("session", 1, "Explain the migration runner");
        embedder.Verify(x => x.GenerateEmbedding(It.IsAny<string>()), Times.Exactly(2));
        Assert.True(store.ContainsCurrent("session:1", "Explain the migration runner"));
    }

    [Fact]
    public void ReplayedSession_ChecksWholeChunkSet_AndChangedTextEmbedsAgain()
    {
        using var store = new BertV2SessionVectorStore(DatabasePath);
        var embedder = CreateEmbedder();
        var service = new BertV2SessionEmbeddingService(embedder.Object, store);
        Assert.Equal(1, service.CaptureSession("session", ["Explain the migration runner"]));
        Assert.Equal(1, service.CaptureSession("session", ["Explain the migration runner"]));
        embedder.Verify(x => x.GenerateEmbedding(It.IsAny<string>()), Times.Once);

        store.AddOrUpdate("session", 1, "stale extra chunk", Vector());
        service.CaptureSession("session", ["Explain the migration runner"]);
        embedder.Verify(x => x.GenerateEmbedding(It.IsAny<string>()), Times.Exactly(2));
        Assert.True(store.ContainsCurrentSession("session", ["Explain the migration runner"]));

        service.CaptureSession("session", ["Explain the connection factory"]);
        embedder.Verify(x => x.GenerateEmbedding(It.IsAny<string>()), Times.Exactly(3));
        Assert.True(store.ContainsCurrentSession("session", ["Explain the connection factory"]));
    }

    [Fact]
    public void SessionReplacement_WhenLaterChunkFails_PreservesPreviousContent()
    {
        using var store = new BertV2SessionVectorStore(DatabasePath);
        store.ReplaceSession("session", [new(0, "previous content", Vector())]);
        Assert.Throws<ArgumentException>(() => store.ReplaceSession("session",
            [new(0, "new first chunk", Vector()), new(1, "invalid second chunk", new float[3])]));
        Assert.True(store.ContainsCurrentSession("session", ["previous content"]));
    }

    [Fact]
    public void Reopen_AdoptsLegacySchemaOnce_AndPreservesExistingVectors()
    {
        using (var initial = new BertV2VectorStore(DatabasePath))
        {
            initial.AddOrUpdate("session:1", "keep this ordinary text", Vector());
            initial.AddOrUpdate("session:2", "sk-abcdefghijklmnopqrstuvwxyz123456", Vector());
        }
        ExecuteSql("DROP TABLE SchemaMigrations; PRAGMA user_version=0;");

        using (var adopted = new BertV2VectorStore(DatabasePath))
        {
            Assert.True(adopted.ContainsCurrent("session:1", "keep this ordinary text"));
            Assert.False(adopted.ContainsCurrent("session:2", "sk-abcdefghijklmnopqrstuvwxyz123456"));
        }
        using var reopened = new BertV2SessionVectorStore(DatabasePath);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Component = 'bert-vectors';";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ConcurrentStores_IsolateConnections_AndCommitCompleteRows()
    {
        using var inputStore = new BertV2VectorStore(DatabasePath);
        using var sessionStore = new BertV2SessionVectorStore(DatabasePath);
        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            inputStore.AddOrUpdate($"session:{i}", $"message {i}", Vector());
            sessionStore.ReplaceSession($"session-{i}", [new(0, $"session text {i}", Vector())]);
            Assert.True(inputStore.ContainsCurrent($"session:{i}", $"message {i}"));
            Assert.True(sessionStore.ContainsCurrentSession($"session-{i}", [$"session text {i}"]));
        }));
        await Task.WhenAll(tasks);
        var search = new BertSearchDbService(DatabasePath, Path.Combine(_directory, "absent-state.db"));
        Assert.Equal(12, search.CountDocuments());
        Assert.Equal(12, search.CountVectors());
        Assert.Equal(12, search.CountSessionVectors());
    }

    [Fact]
    public async Task ConcurrentInitialization_RecordsOneBaseline_ForBothStores()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            if (index % 2 == 0)
            {
                using var store = new BertV2VectorStore(DatabasePath);
                store.AddOrUpdate($"session:{index}", "concurrent initialization", Vector());
            }
            else
            {
                using var store = new BertV2SessionVectorStore(DatabasePath);
                store.AddOrUpdate("session", index, "concurrent initialization", Vector());
            }
        })));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Component = 'bert-vectors';";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void ReadFailure_UsesProviderNeutralException()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(DatabasePath, "not a database");
        var search = new BertSearchDbService(DatabasePath, Path.Combine(_directory, "absent-state.db"));
        var error = Assert.Throws<StorageException>(() => search.CountDocuments());
        Assert.False(error.IsTransient);
        Assert.IsType<SqliteException>(error.InnerException);
    }

    private static Mock<IBertV2BgeEmbedder> CreateEmbedder()
    {
        var embedder = new Mock<IBertV2BgeEmbedder>(MockBehavior.Strict);
        embedder.Setup(x => x.GenerateEmbedding(It.IsAny<string>())).Returns(Vector);
        return embedder;
    }

    private static float[] Vector()
    {
        var vector = new float[384];
        vector[0] = 1;
        return vector;
    }

    private SqliteConnection OpenConnection() => BertVectorDatabase.Open(DatabasePath);

    private void ExecuteSql(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
