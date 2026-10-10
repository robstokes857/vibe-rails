using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed partial class SessionDataExportRepositoryTests
{
    [Fact]
    public async Task CardRefreshQueue_PreservesPendingBackoffAcrossRestarts_AndRecoversMissingCompletedArchives()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid().ToString("D");
        var repo = new Repository(_connectionString);
        await InsertSessionAsync(_connectionString, id, now.AddHours(-1), 0);
        Assert.True(await repo.EnsureSessionShareUploadAsync(id, "key-a", now, ct));
        Assert.True(await repo.DeferSessionExportAsync(id, now.AddHours(1), ct));
        repo = new Repository(_connectionString);
        Assert.True(await repo.EnsureSessionShareUploadAsync(id, "key-a", now.AddMinutes(1), ct));
        Assert.Null(await repo.GetNextSharedSessionAsync("key-a", now.AddMinutes(2), ct));
        Assert.Equal(1, (await repo.GetNextSharedSessionAsync("key-a", now.AddHours(1), ct))?.Attempts);
        Assert.Null(await repo.GetNextSharedSessionAsync("key-b", now.AddHours(1), ct));
        Assert.True(await repo.AcknowledgeSessionExportAsync("key-a", id, now, "included", 7, ct));
        Assert.True(await repo.EnsureSessionShareUploadAsync(id, "key-a", now, ct));
        Assert.Equal(0, (await repo.GetNextSharedSessionAsync("key-a", now, ct))?.Attempts);
        Assert.Equal("included", await ScalarAsync<string>(_connectionString, "SELECT ExportedProxyCoverage FROM Sessions WHERE Id=$id", ("$id", id)));
        Assert.False(await repo.EnsureSessionShareUploadAsync("missing", "key-a", now, ct));
    }

    [Fact]
    public async Task SharingQueue_Persists_WaitsForCompletion_AndBypassesOrdinarySettleDelay()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid().ToString("D");
        var repo = new Repository(_connectionString);
        await InsertSessionAsync(_connectionString, id, null, 0);
        Assert.True(await repo.QueueSessionShareUploadAsync(id, "key-a", now, ct));
        Assert.True(await repo.QueueSessionShareUploadAsync(id, "key-a", now, ct));
        Assert.Null(await repo.GetNextSharedSessionAsync("key-a", now, ct));
        Assert.False(await repo.CanExportSessionToKeyAsync(id, "key-a", ct));
        Assert.Null(await repo.WriteSessionExportAsync(id, new MemoryStream(), ct));
        await ExecuteAsync(_connectionString, "UPDATE Sessions SET EndedUTC=$now WHERE Id=$id",
            ("$id", id), ("$now", now.ToString("O")));
        var reopened = new Repository($"Mode=ReadWriteCreate;Data Source={_databasePath};Cache=Shared");
        Assert.Equal(id, (await reopened.GetNextSharedSessionAsync("key-a", now, ct))?.SessionId);
        Assert.Null(await reopened.GetNextSharedSessionAsync("key-b", now, ct));
        Assert.Null(await reopened.GetOldestUnexportedSessionAsync(now.AddMinutes(1), now, ct));
        Assert.True(await reopened.CanExportSessionToKeyAsync(id, "key-a", ct));
        Assert.False(await reopened.CanExportSessionToKeyAsync(id, "key-b", ct));
        Assert.Equal(1L, await ScalarAsync<long>(_connectionString, "SELECT COUNT(*) FROM SessionShareUploads"));
        Assert.False(await reopened.QueueSessionShareUploadAsync("missing", "key-a", now, ct));
    }

    [Fact]
    public async Task SharingQueue_ReexportsPriorAccount_RetriesAndAcknowledgesOnlyMatchingDestination()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid().ToString("D");
        var repo = new Repository(_connectionString);
        await InsertSessionAsync(_connectionString, id, now.AddDays(-1), 0, now.AddHours(-1));
        await ExecuteAsync(_connectionString, "UPDATE Sessions SET ExportedProxyCoverage='included', ExportedProxyMaxRowId=7 WHERE Id=$id", ("$id", id));
        await repo.QueueSessionShareUploadAsync(id, "key-a", now, ct);
        await repo.QueueSessionShareUploadAsync(id, "key-b", now, ct);
        Assert.True(await repo.SessionAwaitsExportAsync(id, ct));
        Assert.NotNull(await repo.WriteSessionExportAsync(id, new MemoryStream(), ct));
        Assert.True(await repo.DeferSessionExportAsync(id, now.AddMinutes(2), ct));
        Assert.Null(await repo.GetNextSharedSessionAsync("key-a", now, ct));
        Assert.Equal(1, (await repo.GetNextSharedSessionAsync("key-a", now.AddMinutes(2), ct))?.Attempts);
        Assert.False(await repo.AcknowledgeSessionExportAsync("wrong", id, now, "empty", null, ct));
        Assert.True(await repo.AcknowledgeSessionExportAsync("key-a", id, now, "empty", null, ct));
        Assert.Null(await repo.GetNextSharedSessionAsync("key-a", now.AddHours(1), ct));
        Assert.NotNull(await repo.GetNextSharedSessionAsync("key-b", now.AddHours(1), ct));
        Assert.True(await repo.SessionAwaitsExportAsync(id, ct));
        Assert.False(await repo.CanExportSessionToKeyAsync(id, "key-a", ct));
        Assert.True(await repo.AcknowledgeSessionExportAsync("key-b", id, now, null, null, ct));
        Assert.False(await repo.SessionAwaitsExportAsync(id, ct));
        Assert.Null(await repo.WriteSessionExportAsync(id, new MemoryStream(), ct));
        Assert.Equal("included", await ScalarAsync<string>(_connectionString, "SELECT ExportedProxyCoverage FROM Sessions WHERE Id=$id", ("$id", id)));
        Assert.Equal(7L, await ScalarAsync<long>(_connectionString, "SELECT ExportedProxyMaxRowId FROM Sessions WHERE Id=$id", ("$id", id)));
        await ExecuteAsync(_connectionString, "PRAGMA foreign_keys=ON; DELETE FROM Sessions WHERE Id=$id", ("$id", id));
        Assert.Equal(0L, await ScalarAsync<long>(_connectionString, "SELECT COUNT(*) FROM SessionShareUploads"));
    }
}
