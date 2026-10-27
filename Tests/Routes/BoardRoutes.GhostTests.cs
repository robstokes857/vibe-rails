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
    public async Task GhostRecheckRequiresBothCredentialsAndResolvesOnlyTheCurrentProject()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var card = await store.CreateCardAsync(_project, new(null, "Check code", "", null, "medium", null, [], false), ct);
        var foreignProject = _project + "-foreign";
        await store.EnsureDefaultColumnsAsync(foreignProject, ct);
        var foreign = await store.CreateCardAsync(foreignProject, new(null, "Other code", "", null, "medium", null, [], false), ct);
        var path = $"/api/v1/board/cards/{card.Id}/ghost/recheck";
        foreach (var (session, tab) in new[] { ((string?)null, (string?)null), ("test-session", null), (null, "test-tab") })
        {
            using var denied = await SendAsync(HttpMethod.Post, path, session, tab);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var response = await SendAsync(HttpMethod.Post, path, "test-session", "test-tab");
        response.EnsureSuccessStatusCode();
        Assert.NotNull(await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.BoardGhostCheckResponse, ct));
        using var outside = await SendAsync(HttpMethod.Post, $"/api/v1/board/cards/{foreign.Id}/ghost/recheck", "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }
}
