using System.Text.Json;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSyncServiceTests
{
    [Fact]
    public async Task AutomaticSyncSendsSourceLabelsOnFirstPublishAndEveryIdleTick()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var board = Assert.Single(await store.GetBoardsAsync(root, Ct));
        var pushes = new List<BoardSyncPushRequest>();
        client.BeforePush = pushes.Add;

        // No checkbox change, card edit or manual sync precedes publication.
        Assert.False(board.SyncEnabled);
        await service.SyncDueAsync(Ct);
        Assert.Null((await service.GetStatusAsync(root, board.Id, Ct))!.LastError);
        var published = Assert.IsType<BoardSyncPublishRequest>(client.PublishedLayout);
        Assert.Equal(Path.GetFileName(root), published.SourceRepository);
        Assert.Equal(Environment.MachineName, published.SourceComputer);
        Assert.Equal(1, client.Publications);
        pushes.Clear();

        // The hosted copy may still have missing labels from an older client. A quiet
        // scheduled tick must refresh them without republishing or changing the layout.
        await service.SyncDueAsync(Ct);
        Assert.Equal(1, client.Publications);
        var automatic = Assert.Single(pushes);
        Assert.Empty(automatic.Entries);
        Assert.Null(automatic.Name);
        Assert.Null(automatic.Lanes);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(automatic,
            BoardSyncJsonContext.Default.BoardSyncPushRequest));
        Assert.Equal(Path.GetFileName(root), body.RootElement.GetProperty("sourceRepository").GetString());
        Assert.Equal(Environment.MachineName, body.RootElement.GetProperty("sourceComputer").GetString());

        pushes.Clear();
        await service.SyncNowAsync(root, board.Id, Ct);
        var manual = Assert.Single(pushes);
        Assert.Equal(automatic.SourceRepository, manual.SourceRepository);
        Assert.Equal(automatic.SourceComputer, manual.SourceComputer);
    }
}
