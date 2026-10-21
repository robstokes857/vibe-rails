using System.Net;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task GitHubLinksRequireBothCredentialsAndCardOwnership()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        await store.EnsureDefaultColumnsAsync(_project + "-foreign", ct);
        var card = await store.CreateCardAsync(_project, new(null, "Links", "", null, "medium", null, [], false), ct);
        var foreign = await store.CreateCardAsync(_project + "-foreign", new(null, "Foreign", "", null, "medium", null, [], false), ct);
        var path = $"/api/v1/board/cards/{card.Id}/code-links";
        var body = new LinkBoardCodeRequest("https://github.com/org/repo/pull/42");
        foreach (var (session, tab) in new (string?, string?)[] { (null, null), ("test-session", null), (null, "test-tab") })
        {
            using var denied = await SendAsync(HttpMethod.Post, path, session, tab, body);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var outside = await SendAsync(HttpMethod.Post, $"/api/v1/board/cards/{foreign.Id}/code-links", "test-session", "test-tab", body);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        using var invalid = await SendAsync(HttpMethod.Post, path, "test-session", "test-tab", new LinkBoardCodeRequest("javascript:alert(1)"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var added = await SendAsync(HttpMethod.Post, path, "test-session", "test-tab", body);
        added.EnsureSuccessStatusCode();
        using var json = await ReadJsonAsync(added);
        var id = json.RootElement.GetProperty("id").GetString();
        using var detail = await GetJsonAsync($"/api/v1/board/cards/{card.Id}");
        Assert.Equal(body.Url, detail.RootElement.GetProperty("codeLinks")[0].GetProperty("url").GetString());
        using var deniedDelete = await SendAsync(HttpMethod.Delete, path + "/" + id, "test-session");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedDelete.StatusCode);
        using var removed = await SendAsync(HttpMethod.Delete, path + "/" + id, "test-session", "test-tab");
        removed.EnsureSuccessStatusCode();
        Assert.Empty(await store.GetCodeLinksAsync(_project, card.Id, ct));
    }
}
