using Microsoft.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class TokenSavingsPublishStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"savings-publish-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_path};Pooling=False";
    private TokenSavingsPublishStore Store() => new(ConnectionString);

    [Fact]
    public async Task FirstActivation_BaselinesHistory_ThenReportsOnlyNewSavings()
    {
        await SetTotalAsync(12_000);
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(12_125);
        var report = Assert.IsType<TokenSavingsPendingReport>(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        Assert.Equal(125, report.TokensSaved);
        Assert.Matches("^cmp_[0-9a-f]{32}$", report.ComputerId);
        Assert.Matches("^tsr_[0-9a-f]{32}$", report.ReportId);
    }

    [Fact]
    public async Task RestartAndRename_RetryExactPendingReport_KeepLaterSavings()
    {
        Assert.Null(await Store().PrepareAsync("original", TestContext.Current.CancellationToken));
        await SetTotalAsync(100);
        var first = Assert.IsType<TokenSavingsPendingReport>(await Store().PrepareAsync("original", TestContext.Current.CancellationToken));
        await SetTotalAsync(175);
        Assert.Equal(first, await Store().PrepareAsync("renamed", TestContext.Current.CancellationToken));
        await Store().AcknowledgeAsync(first.ReportId, TestContext.Current.CancellationToken);
        var next = Assert.IsType<TokenSavingsPendingReport>(await Store().PrepareAsync("renamed", TestContext.Current.CancellationToken));
        Assert.Equal(75, next.TokensSaved);
        Assert.Equal("renamed", next.ComputerName);
        Assert.Equal(first.ComputerId, next.ComputerId);
        Assert.NotEqual(first.ReportId, next.ReportId);
    }

    [Fact]
    public async Task WrongOrRepeatedAcknowledgement_CannotClearNewPendingReport()
    {
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(100);
        var first = Assert.IsType<TokenSavingsPendingReport>(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await Store().AcknowledgeAsync("tsr_wrong", TestContext.Current.CancellationToken);
        Assert.Equal(first, await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await Store().AcknowledgeAsync(first.ReportId, TestContext.Current.CancellationToken);
        await SetTotalAsync(110);
        var second = await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken);
        await Store().AcknowledgeAsync(first.ReportId, TestContext.Current.CancellationToken);
        Assert.Equal(second, await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LargeBacklog_SplitsAtOneHundredMillion()
    {
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(250_000_012);
        foreach (var expected in new long[] { 100_000_000, 100_000_000, 50_000_012 })
        {
            var report = Assert.IsType<TokenSavingsPendingReport>(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
            Assert.Equal(expected, report.TokensSaved);
            await Store().AcknowledgeAsync(report.ReportId, TestContext.Current.CancellationToken);
        }
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PositionsAboveDoublePrecision_PreserveIntegerIncrements()
    {
        const long baseline = 9_007_199_254_740_993;
        await SetTotalAsync(baseline);
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(baseline + 3);
        var report = Assert.IsType<TokenSavingsPendingReport>(
            await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        Assert.Equal(3, report.TokensSaved);
        await Store().AcknowledgeAsync(report.ReportId, TestContext.Current.CancellationToken);
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AutomaticUpgrade_PreservesMeasurementSchemaAndOlderWrites()
    {
        await SetTotalAsync(1000); // Existing measurement table, without the new publish schema.
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'TokenSavings';";
        var oldSchema = command.ExecuteScalar();
        command.CommandText = "PRAGMA user_version;";
        var generation = command.ExecuteScalar();

        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));

        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'TokenSavings';";
        Assert.Equal(oldSchema, command.ExecuteScalar());
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(generation, command.ExecuteScalar());
        command.CommandText = "SELECT BytesBefore FROM TokenSavings;";
        Assert.Equal(4000L, command.ExecuteScalar());
        command.CommandText = "PRAGMA schema_version;";
        var schemaVersion = command.ExecuteScalar();

        // The older measurement writer can keep recording while v2 state is present.
        await SetTotalAsync(1010);
        var pending = Assert.IsType<TokenSavingsPendingReport>(
            await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        Assert.Equal(10, pending.TokensSaved);
        command.CommandText = "PRAGMA schema_version;";
        Assert.Equal(schemaVersion, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Component = 'token-savings-publish' AND Version = 1;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ConcurrentStores_ReturnTheSamePendingBatch()
    {
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(100);
        var cancellationToken = TestContext.Current.CancellationToken;
        var reports = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => Store().PrepareAsync("desktop", cancellationToken), cancellationToken)));
        Assert.All(reports, report => Assert.Equal(reports[0], report));
    }

    [Fact]
    public async Task CounterReset_RebaselinesAndReportsSubsequentIncrease()
    {
        await SetTotalAsync(1_000);
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(10);
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(30);
        var report = Assert.IsType<TokenSavingsPendingReport>(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        Assert.Equal(20, report.TokensSaved);
    }

    [Fact]
    public async Task ResetWhilePending_FinishesOriginalReportBeforeRebaselining()
    {
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(1_000);
        var pending = Assert.IsType<TokenSavingsPendingReport>(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(10);
        Assert.Equal(pending, await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await Store().AcknowledgeAsync(pending.ReportId, TestContext.Current.CancellationToken);
        Assert.Null(await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken));
        await SetTotalAsync(15);
        Assert.Equal(5, (await Store().PrepareAsync("desktop", TestContext.Current.CancellationToken))!.TokensSaved);
    }

    [Fact]
    public async Task IndependentMachines_HaveIndependentIdsAndProgress()
    {
        using var other = new TokenSavingsPublishStoreTests();
        Assert.Null(await Store().PrepareAsync("same-name", TestContext.Current.CancellationToken));
        Assert.Null(await other.Store().PrepareAsync("same-name", TestContext.Current.CancellationToken));
        await SetTotalAsync(100);
        await other.SetTotalAsync(200);
        var first = (await Store().PrepareAsync("same-name", TestContext.Current.CancellationToken))!;
        var second = (await other.Store().PrepareAsync("same-name", TestContext.Current.CancellationToken))!;
        Assert.NotEqual(first.ComputerId, second.ComputerId);
        Assert.Equal(100, first.TokensSaved);
        Assert.Equal(200, second.TokensSaved);
    }

    private async Task SetTotalAsync(long tokens)
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        TokenSavingsStore.EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TokenSavings (Day, Provider, BytesBefore, BytesAfter, UpdatedUTC)
            VALUES ('2026-10-25', 'test', $bytes, 0, '2026-10-25T00:00:00Z')
            ON CONFLICT(Day, Provider) DO UPDATE SET BytesBefore = excluded.BytesBefore;
            """;
        command.Parameters.AddWithValue("$bytes", tokens * 4);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(_path + suffix);
    }
}
