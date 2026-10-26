using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task EveryContextRoutesRequireCredentialsAndKeepBoardRevisionsIndependent()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        var board = await store.CreateBoardAsync(_project, "Context board", ct);
        var other = await store.CreateBoardAsync(_project + "-foreign", "Foreign", ct);
        var path = $"/api/v1/board/boards/{board.Id}/every-context";
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        foreach (var credentials in new[] { (Session: (string?)null, Tab: (string?)null), ("test-session", null), (null, "test-tab") })
        {
            using var denied = await SendAsync(method, path, credentials.Session, credentials.Tab,
                method == HttpMethod.Put ? new UpdateBoardEveryContextRequest("Denied", 0) : null);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var saved = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardEveryContextRequest("  Board message  ", 0));
        saved.EnsureSuccessStatusCode();
        var value = await saved.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.BoardEveryContextSettingsRecord, ct);
        Assert.Equal(new BoardEveryContextSettingsRecord("Board message", 1), value);
        using var stale = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardEveryContextRequest("Stale", 0));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var oversized = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new UpdateBoardEveryContextRequest(new('x', 4001), 1));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        using var foreign = await SendAsync(HttpMethod.Put, $"/api/v1/board/boards/{other.Id}/every-context", "test-session", "test-tab", new UpdateBoardEveryContextRequest("Foreign", 0));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("Board message", (await store.GetEveryContextSettingsAsync(_project, board.Id, ct))!.Message);
    }

    [Fact]
    public async Task PinRouteRequiresCredentialsAndPreservesCardPositionAndFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        var board = await store.CreateBoardAsync(_project, "Pins", ct);
        var lane = (await store.GetColumnsAsync(_project, ct, board.Id))[0];
        var card = await store.CreateCardAsync(_project, new(lane.Id, "Pin me", "Description", null, "medium", null, [], false), ct);
        await store.CreateCardAsync(_project, new(lane.Id, "Newer", "", null, "medium", null, [], false), ct);
        var path = $"/api/v1/board/cards/{card.Id}/pin";
        using var denied = await SendAsync(HttpMethod.Put, path, body: new SetBoardCardPinRequest(true));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var saved = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new SetBoardCardPinRequest(true));
        saved.EnsureSuccessStatusCode();
        var value = await saved.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.BoardCardResponse, ct);
        Assert.True(value!.Pinned);
        Assert.Equal(1, value.Position);
        Assert.Equal("Description", value.Description);
        Assert.True((await store.FindCardAsync(_project, card.Id, ct))!.Pinned);
        await store.EnsureDefaultColumnsAsync(_project + "-foreign", ct);
        var foreign = await store.CreateCardAsync(_project + "-foreign", new(null, "Foreign", "", null, "medium", null, [], false), ct);
        using var outside = await SendAsync(HttpMethod.Put, $"/api/v1/board/cards/{foreign.Id}/pin", "test-session", "test-tab", new SetBoardCardPinRequest(true));
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }
}
