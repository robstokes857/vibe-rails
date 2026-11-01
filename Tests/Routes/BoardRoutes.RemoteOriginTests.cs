using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task RemoteOriginFiltersPagesAndIndexedSearchAndAppearsInCardJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var local = await store.CreateCardAsync(_project, new(null, "needle local", "", null, "medium", null, [], false), ct);
        var remote = (await store.CreateSyncedCardAsync(_project, "card_remote", "VB-ABCDE-100",
            new(local.ColumnId, "needle remote", "", null, "medium", null, [], false, BoardId: local.BoardId),
            BoardAuthor.User(), new("log_remote", 1, DateTime.UtcNow, BoardId: local.BoardId), ct))!;
        IndexBoard();
        using var page = await GetJsonAsync("/api/v1/board/cards?pageSize=1&origin=remote");
        var summary = Assert.Single(page.RootElement.GetProperty("cards").EnumerateArray());
        Assert.Equal(remote.Id, summary.GetProperty("id").GetString());
        Assert.True(summary.GetProperty("remoteCreated").GetBoolean());
        using var detail = await GetJsonAsync($"/api/v1/board/cards/{remote.Id}");
        Assert.True(detail.RootElement.GetProperty("remoteCreated").GetBoolean());
        using var search = await GetJsonAsync("/api/v1/board/cards/search?q=needle&origin=remote&count=1");
        Assert.Equal(remote.Id, Assert.Single(search.RootElement.GetProperty("cards").EnumerateArray()).GetProperty("id").GetString());
        using var human = await GetJsonAsync("/api/v1/board/cards?pageSize=1&origin=human");
        Assert.Equal(local.Id, Assert.Single(human.RootElement.GetProperty("cards").EnumerateArray()).GetProperty("id").GetString());
    }
}
