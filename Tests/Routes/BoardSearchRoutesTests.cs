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
    public async Task LocalSearchCombinesFiltersWithFullTextAcrossProjects()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        await store.CreateCardAsync(_project, new(null, "cobalt title", "", null, "medium", null, [], false), ct);
        var foreign = _project + "-foreign";
        await store.EnsureDefaultColumnsAsync(foreign, ct);
        var target = await store.CreateCardAsync(foreign,
            new(null, "Retry fix", "Deep cobalt detail", "base:codex", "high", null, [], false, Type: "bug", AgentMade: true), ct);
        await store.AddCommentAsync(foreign, target.Id, BoardAuthor.User(), "ultraviolet discussion", ct);
        IndexBoard();
        foreach (var query in new[] { "cobalt", "ultraviolet" })
        {
            using var response = await GetJsonAsync($"/api/v1/board/cards/search?q={query}&count=1&assignee=base%3Acodex&type=bug&priority=high&origin=agent");
            var card = Assert.Single(response.RootElement.GetProperty("cards").EnumerateArray());
            Assert.Equal(target.Id, card.GetProperty("id").GetString());
            Assert.False(card.GetProperty("isCurrentProject").GetBoolean());
        }
        using var noMatch = await GetJsonAsync("/api/v1/board/cards/search?q=cobalt&type=feature");
        Assert.Empty(noMatch.RootElement.GetProperty("cards").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalCardDetailReportsOnlyItsOwningBoardsDedicatedJiraConnection(bool dedicated)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        var elsewhere = _project + "-foreign";
        await store.EnsureDefaultColumnsAsync(elsewhere, ct);
        var card = await store.CreateCardAsync(elsewhere, new(null, "Foreign card", "", null, "medium", null, [], false), ct);
        var path = $"/api/v1/board/local-cards/{card.Id}";

        using var local = await GetJsonAsync(path);
        Assert.False(local.RootElement.GetProperty("isJiraBoard").GetBoolean());

        // Only a dedicated connection defines a Jira board, including while delivery is disabled.
        await store.SaveJiraConnectionAsync(new("jira_local_detail", elsewhere, card.BoardId,
            "https://example.atlassian.net", "user@example.com", false, BoardJiraAuthStatus.Expired,
            null, "project = TEST", false, null, null, null, null, null, DedicatedBoard: dedicated), ct);
        using var connected = await GetJsonAsync(path);
        Assert.Equal(dedicated, connected.RootElement.GetProperty("isJiraBoard").GetBoolean());

        Assert.True(await store.DeleteJiraConnectionAsync(elsewhere, card.BoardId, "jira_local_detail", ct));
        using var unlinked = await GetJsonAsync(path);
        Assert.False(unlinked.RootElement.GetProperty("isJiraBoard").GetBoolean());
    }

    /// <summary>The owning board's dedicated Azure DevOps connection names the provider; the card's work item link names the card's.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalCardDetailReportsItsOwningBoardsDedicatedAzureDevOpsConnection(bool dedicated)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        var elsewhere = _project + "-foreign";
        await store.EnsureDefaultColumnsAsync(elsewhere, ct);
        var card = await store.CreateCardAsync(elsewhere, new(null, "Foreign card", "", null, "medium", null, [], false), ct);
        var path = $"/api/v1/board/local-cards/{card.Id}";

        using var local = await GetJsonAsync(path);
        Assert.Equal(JsonValueKind.Null, local.RootElement.GetProperty("remoteProvider").ValueKind);
        Assert.Equal(JsonValueKind.Null, local.RootElement.GetProperty("card").GetProperty("remoteProvider").ValueKind);

        await store.SaveAdoConnectionAsync(new("ado_localdetail0", elsewhere, card.BoardId, "contoso", "Web", "Web Team", "Stories", null,
            "https://dev.azure.com/contoso/Web/_boards/board/t/Web%20Team/Stories", "Stories", null, null, null, null,
            false, BoardAdoAuthStatus.Expired, false, null, null, DedicatedBoard: dedicated, null, null, null), ct);
        await store.AddAdoLinkAsync(new(card.Id, "ado_localdetail0", 9, 1, DateTime.UtcNow, null, DateTime.UtcNow), ct);
        using var connected = await GetJsonAsync(path);
        Assert.Equal(dedicated ? "ado" : null, connected.RootElement.GetProperty("remoteProvider").GetString());
        Assert.False(connected.RootElement.GetProperty("isJiraBoard").GetBoolean());
        var linked = connected.RootElement.GetProperty("card");
        Assert.Equal("ado", linked.GetProperty("remoteProvider").GetString());
        Assert.Equal("#9", linked.GetProperty("remoteIssueKey").GetString());
        Assert.Equal("https://dev.azure.com/contoso/Web/_workitems/edit/9", linked.GetProperty("remoteIssueUrl").GetString());
        Assert.Equal(JsonValueKind.Null, linked.GetProperty("jiraIssueKey").ValueKind);

        Assert.True(await store.DeleteAdoConnectionAsync(elsewhere, card.BoardId, "ado_localdetail0", ct));
        using var unlinked = await GetJsonAsync(path);
        Assert.Equal(JsonValueKind.Null, unlinked.RootElement.GetProperty("remoteProvider").ValueKind);
        // The retained work item link keeps the card's identity; the page needs a live connection.
        Assert.Equal(JsonValueKind.Null, unlinked.RootElement.GetProperty("card").GetProperty("remoteIssueUrl").ValueKind);
    }

    /// <summary>Lane tiles link their issue: list and page summaries carry the URL the detail builds (VIBE-171).</summary>
    [Fact]
    public async Task CardListsAndPagesLinkEachTrackerIssueFromItsSavedConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var jira = await store.CreateCardAsync(_project, new(null, "Jira card", "", null, "medium", null, [], false), ct);
        var ado = await store.CreateCardAsync(_project, new(null, "Azure DevOps card", "", null, "medium", null, [], false), ct);
        var local = await store.CreateCardAsync(_project, new(null, "Local card", "", null, "medium", null, [], false), ct);
        await store.SaveJiraConnectionAsync(new("jira_tile_links", _project, jira.BoardId,
            "https://example.atlassian.net", "user@example.com", false, BoardJiraAuthStatus.Expired,
            null, "project = APP", false, null, null, null, null, null), ct);
        await store.AddJiraLinkAsync(new(jira.Id, "jira_tile_links", "10007", "APP-7", null, DateTime.UtcNow, DateTime.UtcNow), ct);
        await store.SaveAdoConnectionAsync(new("ado_tilelinks000", _project, ado.BoardId, "contoso", "Web App", "Web Team", "Stories", null,
            "https://dev.azure.com/contoso/Web%20App/_boards/board/t/Web%20Team/Stories", "Stories", null, null, null, null,
            false, BoardAdoAuthStatus.Expired, false, null, null, DedicatedBoard: false, null, null, null), ct);
        await store.AddAdoLinkAsync(new(ado.Id, "ado_tilelinks000", 42, 1, DateTime.UtcNow, null, DateTime.UtcNow), ct);

        foreach (var path in new[] { "/api/v1/board/cards", "/api/v1/board/cards?pageSize=10" })
        {
            using var list = await GetJsonAsync(path);
            var cards = list.RootElement.GetProperty("cards").EnumerateArray().ToDictionary(card => card.GetProperty("id").GetString()!);
            Assert.Equal("jira", cards[jira.Id].GetProperty("remoteProvider").GetString());
            Assert.Equal("https://example.atlassian.net/browse/APP-7", cards[jira.Id].GetProperty("remoteIssueUrl").GetString());
            Assert.Equal("#42", cards[ado.Id].GetProperty("remoteIssueKey").GetString());
            Assert.Equal("https://dev.azure.com/contoso/Web%20App/_workitems/edit/42", cards[ado.Id].GetProperty("remoteIssueUrl").GetString());
            Assert.Equal(JsonValueKind.Null, cards[local.Id].GetProperty("remoteIssueUrl").ValueKind);
        }
        using var detail = await GetJsonAsync($"/api/v1/board/cards/{ado.Id}");
        Assert.Equal("https://dev.azure.com/contoso/Web%20App/_workitems/edit/42", detail.RootElement.GetProperty("remoteIssueUrl").GetString());

        // As on the detail, the retained link keeps the badge but the page needs a live connection.
        Assert.True(await store.DeleteAdoConnectionAsync(_project, ado.BoardId, "ado_tilelinks000", ct));
        using var unlinked = await GetJsonAsync("/api/v1/board/cards?pageSize=10");
        var tile = unlinked.RootElement.GetProperty("cards").EnumerateArray().Single(card => card.GetProperty("id").GetString() == ado.Id);
        Assert.Equal("#42", tile.GetProperty("remoteIssueKey").GetString());
        Assert.Equal(JsonValueKind.Null, tile.GetProperty("remoteIssueUrl").ValueKind);
    }

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
            (HttpMethod.Get, "/api/v1/board/cards/search?q=indexed&type=bug&origin=human", null),
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
