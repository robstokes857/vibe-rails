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
    public async Task DefaultColumn_RoundTripsThroughApi_AndRequiresCredentialsAndProjectScope()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/board/columns", "test-session", "test-tab",
            new CreateBoardColumnRequest("Needs refinement", "#64748b", IsDefaultForNewCards: true));
        created.EnsureSuccessStatusCode();
        var lane = (await created.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.BoardColumnResponse, ct))!;
        Assert.True(lane.IsDefaultForNewCards);
        using var listing = await SendAsync(HttpMethod.Get, "/api/v1/board/columns", "test-session", "test-tab");
        var columns = (await listing.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.BoardColumnListResponse, ct))!.Columns;
        Assert.Equal(lane.Id, Assert.Single(columns, column => column.IsDefaultForNewCards).Id);

        using var newCard = await PostJsonAsync("/api/v1/board/cards", new { title = "Default destination" });
        newCard.EnsureSuccessStatusCode();
        Assert.Equal(lane.Id, (await newCard.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.BoardCardResponse, ct))!.ColumnId);

        foreach (var credentials in new[] { (Session: (string?)null, Tab: (string?)null), ("test-session", null), (null, "test-tab") })
        {
            using var denied = await SendAsync(HttpMethod.Put, $"/api/v1/board/columns/{lane.Id}", credentials.Session, credentials.Tab,
                new UpdateBoardColumnRequest(IsDefaultForNewCards: false));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        var foreign = await store.CreateBoardAsync(_project + "-foreign", "Foreign", ct);
        var foreignLane = (await store.GetColumnsAsync(_project + "-foreign", ct, foreign.Id))[0];
        using var outside = await SendAsync(HttpMethod.Put, $"/api/v1/board/columns/{foreignLane.Id}", "test-session", "test-tab",
            new UpdateBoardColumnRequest(IsDefaultForNewCards: true));
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);

        using var cleared = await SendAsync(HttpMethod.Put, $"/api/v1/board/columns/{lane.Id}", "test-session", "test-tab",
            new UpdateBoardColumnRequest(IsDefaultForNewCards: false));
        cleared.EnsureSuccessStatusCode();
        Assert.False((await cleared.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.BoardColumnResponse, ct))!.IsDefaultForNewCards);
    }
}
