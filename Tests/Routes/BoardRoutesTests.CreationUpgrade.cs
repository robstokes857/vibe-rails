using System.Net;
using Microsoft.Data.Sqlite;
using Tests.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed partial class BoardRoutesTests
{
    [Theory]
    [Trait("Category", "BoardReleaseGate")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateCard_ThroughHttp_PersistsAcrossRestartAndDatabaseUpgrade(bool originalDeletionSchema)
    {
        var ct = TestContext.Current.CancellationToken;
        // No prior lane request: the first HTTP create must initialize a fresh Board too.
        using var initialResponse = await PostJsonAsync("/api/v1/board/cards", new
        {
            title = "Existing release-test card", description = "Keep this description across upgrades."
        });
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        using var initial = await ReadJsonAsync(initialResponse);
        var existingId = initial.RootElement.GetProperty("id").GetString()!;
        var boardId = initial.RootElement.GetProperty("boardId").GetString()!;
        var columnId = initial.RootElement.GetProperty("columnId").GetString()!;

        await _app.StopAsync(ct);
        await _app.DisposeAsync();
        if (originalDeletionSchema)
        {
            // Model an installed board.db with /1 and /2 already applied, while the rest of
            // the database retains its existing card. Never use the developer's database.
            var boardConnection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, "board.db"), Pooling = false
            }.ToString();
            await BoardDeletionUpgradeFixture.RestoreOriginalSchemaAsync(boardConnection, ct);
        }
        await StartHostAsync();

        // Real Kestrel, auth middleware, Board routes, service, JSON context and SQLite store.
        // The host's ordinary DI initialization must perform any upgrade before this write.
        using var response = await PostJsonAsync("/api/v1/board/cards", new
        {
            boardId, columnId, title = "Release gate: create card", description = "Created through the Board API.",
            type = "bug", assignee = "base:codex", workflowMode = "manual", blocked = false
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var created = await ReadJsonAsync(response);
        var cardId = created.RootElement.GetProperty("id").GetString()!;
        var key = created.RootElement.GetProperty("key").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cardId));
        Assert.False(string.IsNullOrWhiteSpace(key));
        Assert.NotEqual(existingId, cardId);

        // A second host/store instance proves this is persisted data, and that startup can repeat.
        await _app.StopAsync(ct);
        await _app.DisposeAsync();
        await StartHostAsync();
        using var loaded = await GetJsonAsync($"/api/v1/board/cards/{cardId}");
        Assert.Equal(key, loaded.RootElement.GetProperty("key").GetString());
        Assert.Equal(boardId, loaded.RootElement.GetProperty("boardId").GetString());
        Assert.Equal(columnId, loaded.RootElement.GetProperty("columnId").GetString());
        Assert.Equal("Release gate: create card", loaded.RootElement.GetProperty("title").GetString());
        Assert.Equal("Created through the Board API.", loaded.RootElement.GetProperty("description").GetString());
        Assert.Equal("bug", loaded.RootElement.GetProperty("type").GetString());
        Assert.Equal("base:codex", loaded.RootElement.GetProperty("assignee").GetString());
        using var retained = await GetJsonAsync($"/api/v1/board/cards/{existingId}");
        Assert.Equal("Keep this description across upgrades.", retained.RootElement.GetProperty("description").GetString());
    }
}
