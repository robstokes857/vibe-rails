using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardRemoteOriginTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-remote-origin-" + Guid.NewGuid().ToString("N"));
    private readonly string connectionString;
    private readonly BoardStore store;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardRemoteOriginTests()
    {
        Directory.CreateDirectory(root);
        connectionString = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new(connectionString, connectionString);
    }

    private async Task<BoardCardRecord> ReceiveAsync(string lane, string board, string title, int number)
    {
        return (await store.CreateSyncedCardAsync(root, "card_remote_" + number, $"VB-ABCDE-{number}",
            new(lane, title, "", null, "medium", null, [], false, BoardId: board), BoardAuthor.User(),
            new("log_remote_" + number, number, DateTime.UtcNow, BoardId: board), Ct))!;
    }

    [Fact]
    public async Task RemoteOriginSurvivesEditsAndAppearsInEveryCardResponse()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var lane = (await store.GetColumnsAsync(root, Ct)).First();
        var card = await ReceiveAsync(lane.Id, lane.BoardId, "Remote work", 100);
        Assert.True(card.RemoteCreated);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Edited locally"), Ct);
        var service = new BoardService(store, new Mock<IBoardCommitService>().Object, new NullBoardLiveSessionProbe());
        Assert.True(Assert.Single((await service.GetCardsAsync(root, Ct)).Cards).RemoteCreated);
        Assert.True((await service.GetCardAsync(root, card.Id, Ct))!.RemoteCreated);
        Assert.True(Assert.Single((await service.GetCardsPageAsync(root, new(Origin: "remote"), Ct)).Cards).RemoteCreated);
    }

    [Fact]
    public async Task RemoteFilterCountsAndPagesTheWholeLaneAndCombinesWithOtherFilters()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var lane = (await store.GetColumnsAsync(root, Ct)).First(c => c.Name == "Done");
        var local = await store.CreateCardAsync(root, new(lane.Id, "Local", "", null, "medium", null, [], false), Ct);
        for (var number = 100; number < 103; number++)
            await ReceiveAsync(lane.Id, lane.BoardId, "Remote " + number, number);
        var first = await store.GetCardsPageAsync(root, new(2, lane.Id, Origin: "remote"), Ct);
        Assert.Equal(4, first.TotalCount);
        Assert.Equal(3, first.FilteredCount);
        Assert.Equal(2, first.Cards.Count);
        Assert.All(first.Cards, card => Assert.True(card.RemoteCreated));
        var next = await store.GetCardsPageAsync(root, new(2, lane.Id, Offset: 2, Origin: "remote",
            ContinuationToken: first.Lanes.Single(l => l.ColumnId == lane.Id).ContinuationToken), Ct);
        Assert.Single(next.Cards);
        Assert.Equal(3, first.Cards.Concat(next.Cards).Select(card => card.Id).Distinct().Count());
        Assert.Equal(local.Id, Assert.Single((await store.GetCardsPageAsync(root, new(Origin: "human"), Ct)).Cards).Id);
        Assert.Single((await store.GetCardsPageAsync(root, new(Q: "Remote 101", Origin: "remote"), Ct)).Cards);
        Assert.Empty((await store.GetCardsPageAsync(root, new(Type: "bug", Origin: "remote"), Ct)).Cards);
    }

    [Fact]
    public async Task SyncAcknowledgementsAndRemoteEditsDoNotRelabelLocalCards()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var local = await store.CreateCardAsync(root, new(null, "Local", "", null, "medium", null, [], false), Ct);
        var entry = Assert.Single(await store.GetUnsentLogEntriesAsync(local.BoardId, 200, Ct));
        await store.MarkLogEntriesSentAsync([new(entry.Entry.Id, 1)], Ct);
        await store.CreateSyncedCardAsync(root, local.Id, local.Key,
            new(local.ColumnId, "Remote baseline", "", null, "medium", null, [], false, BoardId: local.BoardId),
            BoardAuthor.User(), new("log_baseline", 2, DateTime.UtcNow, BoardId: local.BoardId), Ct);
        await store.UpdateSyncedCardAsync(root, local.Id, new(Title: "Remote edit"), BoardAuthor.User(),
            new("log_edit", 3, DateTime.UtcNow, BoardId: local.BoardId), Ct);
        Assert.False((await store.FindCardAsync(root, local.Id, Ct))!.RemoteCreated);
        Assert.Empty((await store.GetCardsPageAsync(root, new(Origin: "remote"), Ct)).Cards);
    }

    [Fact]
    public async Task UpgradePreservesExistingCardsWithoutInventingProvenanceAndOldUpdatesKeepTheMarker()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var local = await store.CreateCardAsync(root, new(null, "Before upgrade", "", null, "medium", null, [], false), Ct);
        await ExecuteAsync("ALTER TABLE BoardCards DROP COLUMN RemoteCreated; DELETE FROM SchemaMigrations WHERE Component='board-remote-origin';");
        var upgraded = new BoardStore(connectionString, connectionString);
        Assert.False((await upgraded.FindCardAsync(root, local.Id, Ct))!.RemoteCreated);
        Assert.Equal("Before upgrade", (await upgraded.FindCardAsync(root, local.Id, Ct))!.Title);
        var remote = await ReceiveAsync(local.ColumnId, local.BoardId, "After upgrade", 100);
        await ExecuteAsync("UPDATE BoardCards SET Title='Updated by an older writer';");
        Assert.True((await upgraded.FindCardAsync(root, remote.Id, Ct))!.RemoteCreated);
        Assert.False((await upgraded.FindCardAsync(root, local.Id, Ct))!.RemoteCreated);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public void Dispose() => Directory.Delete(root, true);
}
