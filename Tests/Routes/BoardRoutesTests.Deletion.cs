using System.Net;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task MultipleCardDeletesKeepSingleCardAuthorizationScopeAndIndependentResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var first = await store.CreateCardAsync(_project, new(null, "First selected", "", null, "medium", null, [], false), ct);
        var second = await store.CreateCardAsync(_project, new(null, "Second selected", "", null, "medium", null, [], false), ct);
        var keep = await store.CreateCardAsync(_project, new(null, "Not selected", "", null, "medium", null, [], false), ct);
        var foreignProject = _project + "-foreign";
        await store.EnsureDefaultColumnsAsync(foreignProject, ct);
        var foreign = await store.CreateCardAsync(foreignProject, new(null, "Foreign", "", null, "medium", null, [], false), ct);

        foreach (var card in new[] { first, second })
        {
            foreach (var credentials in new[] { (Session: (string?)null, Tab: (string?)null), ("test-session", null), (null, "test-tab") })
            {
                using var denied = await SendAsync(HttpMethod.Delete, $"/api/v1/board/cards/{card.Id}", credentials.Session, credentials.Tab);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            Assert.NotNull(await store.FindCardAsync(_project, card.Id, ct));
        }

        // The browser's batch sends independent requests. A bad/missing/foreign ID cannot
        // roll back successful requests or remove another card; the next request can proceed.
        foreach (var (id, expected) in new[] {
            (first.Id, HttpStatusCode.OK), (foreign.Id, HttpStatusCode.NotFound),
            ("missing", HttpStatusCode.NotFound), (second.Id, HttpStatusCode.OK),
            (first.Id, HttpStatusCode.NotFound) })
        {
            using var response = await SendAsync(HttpMethod.Delete, $"/api/v1/board/cards/{id}", "test-session", "test-tab");
            Assert.Equal(expected, response.StatusCode);
        }
        Assert.Null(await store.FindCardAsync(_project, first.Id, ct));
        Assert.Null(await store.FindCardAsync(_project, second.Id, ct));
        Assert.NotNull(await store.FindCardAsync(_project, keep.Id, ct));
        Assert.NotNull(await store.FindCardAsync(foreignProject, foreign.Id, ct));
    }
}
