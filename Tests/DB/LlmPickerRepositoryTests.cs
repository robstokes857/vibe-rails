using Microsoft.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class LlmPickerRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"viberails-picker-repository-{Guid.NewGuid():N}");
    private readonly string _connectionString;

    public LlmPickerRepositoryTests()
    {
        Directory.CreateDirectory(_root);
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Environments (
                Id INTEGER PRIMARY KEY,
                CustomName TEXT NOT NULL,
                LLM INTEGER NOT NULL,
                Path TEXT NOT NULL DEFAULT '',
                CustomArgs TEXT NOT NULL DEFAULT '',
                CustomPrompt TEXT NOT NULL DEFAULT '',
                CreatedUTC TEXT NOT NULL,
                LastUsedUTC TEXT NOT NULL,
                Hidden INTEGER NOT NULL DEFAULT 0,
                AutomationWorker INTEGER NOT NULL DEFAULT 0,
                UNIQUE(CustomName, LLM)
            );
            CREATE TABLE GlobalCache (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL DEFAULT '',
                UpdatedUTC TEXT NOT NULL
            );
            INSERT INTO Environments
                (Id, CustomName, LLM, CreatedUTC, LastUsedUTC, Hidden)
            VALUES
                (1, 'One', 2, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', 0),
                (2, 'Two', 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', 0);
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task SaveLlmPickerStateAsync_CommitsDocumentAndVisibilityTogether()
    {
        var repository = new Repository(_connectionString);

        await repository.SaveLlmPickerStateAsync(
            "ui.llm-picker.v1",
            "{\"version\":1}",
            new Dictionary<int, bool> { [1] = true, [2] = false },
            TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("{\"version\":1}", await ScalarAsync<string>(
            connection,
            "SELECT Value FROM GlobalCache WHERE Key = 'ui.llm-picker.v1';"));
        Assert.Equal(1L, await ScalarAsync<long>(connection, "SELECT Hidden FROM Environments WHERE Id = 1;"));
        Assert.Equal(0L, await ScalarAsync<long>(connection, "SELECT Hidden FROM Environments WHERE Id = 2;"));
    }

    [Fact]
    public async Task SaveLlmPickerStateAsync_RollsBackEveryWriteWhenEnvironmentUpdateFails()
    {
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO GlobalCache (Key, Value, UpdatedUTC)
                VALUES ('ui.llm-picker.v1', 'old', '2026-01-01T00:00:00.0000000Z');
                CREATE TRIGGER RejectSecondPickerVisibility
                BEFORE UPDATE OF Hidden ON Environments
                WHEN NEW.Id = 2
                BEGIN
                    SELECT RAISE(ABORT, 'simulated visibility failure');
                END;
                """;
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var repository = new Repository(_connectionString);

        await Assert.ThrowsAsync<SqliteException>(() => repository.SaveLlmPickerStateAsync(
            "ui.llm-picker.v1",
            "new",
            new Dictionary<int, bool> { [1] = true, [2] = true },
            TestContext.Current.CancellationToken));

        await using var verify = new SqliteConnection(_connectionString);
        await verify.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("old", await ScalarAsync<string>(
            verify,
            "SELECT Value FROM GlobalCache WHERE Key = 'ui.llm-picker.v1';"));
        Assert.Equal(0L, await ScalarAsync<long>(verify, "SELECT Hidden FROM Environments WHERE Id = 1;"));
        Assert.Equal(0L, await ScalarAsync<long>(verify, "SELECT Hidden FROM Environments WHERE Id = 2;"));
    }

    public void Dispose()
    {
        // Release only this class's pooled connections before deleting the temp dir. A process-wide
        // ClearAllPools() here can dispose handles out from under DB test classes running in
        // parallel (seen as an intermittent ObjectDisposedException in JobStoreOverlapTests).
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value, typeof(T));
    }
}
