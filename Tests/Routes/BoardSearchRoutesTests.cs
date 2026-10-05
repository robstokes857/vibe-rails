using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task ProjectOnlyMergeCandidatesApplyScopeBeforeLimitAndKeepLinkedDestinations()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var source = await store.CreateCardAsync(_project, new(null, "Source", "", null, "medium", null, [], false), ct);
        var target = await store.CreateCardAsync(_project, new(null, "Tire task", "", null, "medium", null, [], false), ct);
        await store.LinkCardAsync(_project, source.Id, target.Id, ct);
        var foreignProject = _project + "-foreign";
        await store.EnsureDefaultColumnsAsync(foreignProject, ct);
        const string query = "Replace a flat tire";
        for (var index = 0; index < 51; index++)
            await store.CreateCardAsync(foreignProject, new(null, query, "", null, "medium", null, [], false), ct);
        var path = "/api/v1/board/cards/link-candidates?q=" + Uri.EscapeDataString(query);
        IndexBoard();
        using var global = await GetJsonAsync(path);
        Assert.Equal(50, global.RootElement.GetProperty("cards").GetArrayLength());
        Assert.All(global.RootElement.GetProperty("cards").EnumerateArray(), card => Assert.False(card.GetProperty("isCurrentProject").GetBoolean()));
        var scopedPath = path + "&currentProjectOnly=true&projectPath=" + Uri.EscapeDataString(foreignProject);
        foreach (var credentials in new[] { (Session: (string?)null, Tab: (string?)null), ("test-session", null), (null, "test-tab") })
        {
            using var denied = await SendAsync(HttpMethod.Get, scopedPath, credentials.Session, credentials.Tab);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var scoped = await GetJsonAsync(scopedPath);
        var candidate = Assert.Single(scoped.RootElement.GetProperty("cards").EnumerateArray());
        Assert.Equal(target.Id, candidate.GetProperty("id").GetString());
        Assert.True(candidate.GetProperty("isCurrentProject").GetBoolean());
        Assert.Equal(_project, candidate.GetProperty("projectPath").GetString());
    }

    [Fact]
    public async Task LocalSearchAndEditorRequireBothCredentialsAndResolveStoredOwnership()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var local = await store.CreateCardAsync(_project, new(null, "Local indexed", "", null, "medium", null, [], false), ct);
        var elsewhere = _project + "-foreign";
        await store.EnsureDefaultColumnsAsync(elsewhere, ct);
        var foreign = await store.CreateCardAsync(elsewhere, new(null, "Foreign indexed", "", null, "medium", null, [], false), ct);
        var path = $"/api/v1/board/local-cards/{foreign.Id}";
        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/api/v1/board/cards/search?q=indexed", null),
            (HttpMethod.Get, path, null),
            (HttpMethod.Put, path, new UpdateBoardCardRequest(Title: "Denied", Points: JsonSerializer.SerializeToElement((int?)null))),
            (HttpMethod.Post, path + "/comments", new AddBoardCommentRequest("Denied")),
            (HttpMethod.Post, path + "/move", new MoveBoardCardRequest(foreign.ColumnId)),
            (HttpMethod.Get, path + "/links/candidates", null),
            (HttpMethod.Post, path + "/links", new LinkBoardCardRequest(local.Id)),
            (HttpMethod.Delete, path + "/links/" + local.Id, null)
        })
        {
            foreach (var credentials in new[] { (Session: (string?)null, Tab: (string?)null), ("test-session", null), (null, "test-tab") })
            {
                using var denied = await SendAsync(method, url, credentials.Session, credentials.Tab, body);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
        }
        IndexBoard();
        using var found = await GetJsonAsync("/api/v1/board/cards/search?q=indexed");
        var hits = found.RootElement.GetProperty("cards");
        Assert.Equal(local.Id, hits[0].GetProperty("id").GetString());
        Assert.Equal(foreign.Id, hits[1].GetProperty("id").GetString());
        Assert.False(hits[1].GetProperty("isCurrentProject").GetBoolean());
        using var detail = await GetJsonAsync(path);
        Assert.Equal(elsewhere, detail.RootElement.GetProperty("projectPath").GetString());
        Assert.False(detail.RootElement.GetProperty("isCurrentProject").GetBoolean());
        Assert.Equal(foreign.Id, detail.RootElement.GetProperty("card").GetProperty("id").GetString());
        Assert.NotEmpty(detail.RootElement.GetProperty("columns").EnumerateArray());

        using var edited = await SendJsonAsync(HttpMethod.Put, path, new { title = "Foreign edited", projectPath = _project });
        edited.EnsureSuccessStatusCode();
        Assert.True(edited.Headers.CacheControl!.NoStore);
        Assert.Equal("Foreign edited", (await store.FindCardAsync(elsewhere, foreign.Id, ct))!.Title);
        Assert.Equal("Local indexed", (await store.FindCardAsync(_project, local.Id, ct))!.Title);
        using var commented = await PostJsonAsync(path + "/comments", new { body = "distinctcommentterm" });
        commented.EnsureSuccessStatusCode();
        IndexBoard();
        using var commentHit = await GetJsonAsync("/api/v1/board/cards/search?q=distinctcommentterm");
        Assert.Equal(foreign.Id, Assert.Single(commentHit.RootElement.GetProperty("cards").EnumerateArray()).GetProperty("id").GetString());
        using var linked = await PostJsonAsync(path + "/links", new { card = local.Id });
        linked.EnsureSuccessStatusCode();
        using var linkJson = await ReadJsonAsync(linked);
        Assert.True(linkJson.RootElement.GetProperty("isCurrentProject").GetBoolean());
        using var foreignDetail = await GetJsonAsync(path);
        Assert.True(foreignDetail.RootElement.GetProperty("card").GetProperty("linkedCards")[0].GetProperty("isCurrentProject").GetBoolean());
        using var removed = await SendAsync(HttpMethod.Delete, path + "/links/" + local.Id, "test-session", "test-tab");
        removed.EnsureSuccessStatusCode();

        // Existing repository-scoped routes and foreign alias resolution remain narrow.
        foreach (var url in new[] { $"/api/v1/board/cards/{foreign.Id}", "/api/v1/board/local-cards/" + foreign.DisplayId, "/api/v1/board/local-cards/missing" })
        {
            using var missing = await SendAsync(HttpMethod.Get, url, "test-session", "test-tab");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        using var badLane = await PostJsonAsync(path + "/move", new { columnId = local.ColumnId });
        Assert.Equal(HttpStatusCode.BadRequest, badLane.StatusCode);
        using var badQuery = await SendAsync(HttpMethod.Get, "/api/v1/board/cards/search?q=" + new string('x', 1001), "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.BadRequest, badQuery.StatusCode);
    }
}
