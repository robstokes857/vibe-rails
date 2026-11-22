using System.Text.Json;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSyncServiceTests
{
    [Fact]
    public async Task ClearingDefaultIsPushedAfterAnOlderWriterSavesItsLayoutHash()
    {
        var card = await Card();
        var board = (await store.GetBoardAsync(root, card.BoardId, Ct))!;
        var lanes = await store.GetColumnsAsync(root, Ct, board.Id);
        await store.UpdateColumnAsync(root, lanes[1].Id, null, null, Ct, true);
        client.SupportsOwnedRebuild = true;
        Assert.Null((await service.SetPublishedAsync(root, board.Id, true, Ct))!.LastError);
        Assert.Equal(lanes[1].Id, Assert.Single(client.PublishedLayout!.Lanes,
            lane => lane.IsDefaultForNewCards).Id);

        await store.UpdateColumnAsync(root, lanes[1].Id, null, null, Ct, false);

        // Older versions share this receipt but omit every default flag from their
        // layouts, which makes the hosted reader retain its previously selected lane.
        // Freeze their hash format here rather than calling the current hash helper.
        var prefix = await store.EnsureProjectKeyPrefixAsync(root, Ct);
        var legacyLayout = string.Join('\n', new[] { board.Name, prefix, board.EffectiveDisplayPrefix }
            .Concat(lanes.OrderBy(lane => lane.Position).Select((lane, index) =>
                $"{lane.Id}\t{lane.Name}\t{(string.IsNullOrWhiteSpace(lane.Color) ? null : lane.Color)}\t{index}")));
        var legacyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(legacyLayout)));
        var link = (await store.GetSyncLinkAsync(root, board.Id, Ct))!;
        await store.SaveSyncLinkAsync(link with { LayoutHash = legacyHash }, Ct);

        var pushes = new List<BoardSyncPushRequest>();
        client.BeforePush = pushes.Add;
        Assert.Null((await service.SyncNowAsync(root, board.Id, Ct))!.LastError);
        Assert.Null((await service.SyncNowAsync(root, board.Id, Ct))!.LastError);

        var cleared = Assert.Single(pushes, push => push.Lanes is not null);
        Assert.All(cleared.Lanes!, lane => Assert.False(lane.IsDefaultForNewCards));
        Assert.Empty(cleared.Entries);
        Assert.NotEqual(legacyHash, (await store.GetSyncLinkAsync(root, board.Id, Ct))!.LayoutHash);
    }

    [Fact]
    public async Task RemoteLayoutRefreshPreservesDefaultAndRejectsAStaleDefaultSnapshot()
    {
        var card = await Card();
        var board = (await store.GetBoardAsync(root, card.BoardId, Ct))!;
        var lanes = await store.GetColumnsAsync(root, Ct, board.Id);
        await store.UpdateColumnAsync(root, lanes[1].Id, null, null, Ct, true);
        var snapshot = await store.GetColumnsAsync(root, Ct, board.Id);
        var remote = new BoardRemoteDescriptor(Guid.NewGuid().ToString(), "Renamed remotely", "VB", "VB",
            lanes.Select(lane => new BoardRemoteLane(lane.Id, lane.Name, lane.Color, lane.Position)).ToList(), true, 0);

        await store.UpdateColumnAsync(root, lanes[2].Id, null, null, Ct, true);
        Assert.Null(await store.ApplyRemoteLayoutAsync(root, board.Id, remote, board, snapshot, Ct));
        Assert.Equal(board.Name, (await store.GetBoardAsync(root, board.Id, Ct))!.Name);

        var current = await store.GetColumnsAsync(root, Ct, board.Id);
        var hash = await store.ApplyRemoteLayoutAsync(root, board.Id, remote, board, current, Ct);
        Assert.Equal(BoardLayoutHash.Compute(remote.Name, remote.KeyPrefix, remote.DisplayPrefix, remote.Lanes, lanes[2].Id), hash);
        Assert.Equal(lanes[2].Id, Assert.Single(await store.GetColumnsAsync(root, Ct, board.Id),
            lane => lane.IsDefaultForNewCards).Id);
    }

    [Fact]
    public async Task DefaultLaneIsPublishedAndChangesArePushedWithoutCardEdits()
    {
        var card = await Card();
        var lanes = await store.GetColumnsAsync(root, Ct, card.BoardId);
        await store.UpdateColumnAsync(root, lanes[1].Id, null, null, Ct, true);
        client.SupportsOwnedRebuild = true; // Exercise the remote layout refresh too.
        Assert.Null((await service.SetPublishedAsync(root, card.BoardId, true, Ct))!.LastError);

        using var published = JsonDocument.Parse(JsonSerializer.Serialize(client.PublishedLayout,
            BoardSyncJsonContext.Default.BoardSyncPublishRequest));
        Assert.Equal(lanes[1].Id, Assert.Single(published.RootElement.GetProperty("lanes").EnumerateArray(),
            lane => lane.GetProperty("isDefaultForNewCards").GetBoolean()).GetProperty("id").GetString());

        var pushes = new List<BoardSyncPushRequest>();
        client.BeforePush = pushes.Add;
        await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.All(pushes, push => Assert.Null(push.Lanes));

        pushes.Clear();
        await store.UpdateColumnAsync(root, lanes[2].Id, null, null, Ct, true);
        Assert.Null((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
        var changed = Assert.Single(pushes, push => push.Lanes is not null);
        using var changedJson = JsonDocument.Parse(JsonSerializer.Serialize(changed, BoardSyncJsonContext.Default.BoardSyncPushRequest));
        Assert.Equal(lanes[2].Id, Assert.Single(changedJson.RootElement.GetProperty("lanes").EnumerateArray(),
            lane => lane.GetProperty("isDefaultForNewCards").GetBoolean()).GetProperty("id").GetString());
        Assert.Empty(changed.Entries);

        pushes.Clear();
        await store.UpdateColumnAsync(root, lanes[2].Id, null, null, Ct, false);
        Assert.Null((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
        var cleared = Assert.Single(pushes, push => push.Lanes is not null);
        using var clearedJson = JsonDocument.Parse(JsonSerializer.Serialize(cleared, BoardSyncJsonContext.Default.BoardSyncPushRequest));
        Assert.All(clearedJson.RootElement.GetProperty("lanes").EnumerateArray(),
            lane => Assert.False(lane.GetProperty("isDefaultForNewCards").GetBoolean()));
        Assert.Empty(cleared.Entries);
    }
}
