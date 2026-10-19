using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tests.Services.AzureDevOps;
using VibeRails.DTOs;
using VibeRails.Services.AzureDevOps;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// /api/v1/board/boards/{boardId}/ado (VIBE-168): behind both credentials, project-scoped, the
/// personal access token write-only, and configuration problems as readable 400s.
/// </summary>
public sealed partial class BoardRoutesTests
{
    private const string AdoPat = "test-only-pat-0123456789";
    private const string AdoBoardLink = "https://dev.azure.com/contoso/Web/_boards/board/t/Web%20Team/Stories";
    private readonly AdoPullServiceTests.ScriptedAdo _ado = new();

    [Fact]
    public async Task AdoRoutesRequireBothCredentials()
    {
        using var list = await GetJsonAsync("/api/v1/board/boards");
        var boardId = list.RootElement.GetProperty("boards")[0].GetProperty("id").GetString()!;
        var path = $"/api/v1/board/boards/{boardId}/ado";
        foreach (var (method, suffix, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "", null),
            (HttpMethod.Get, "?settings=true", null),
            (HttpMethod.Put, "", new SaveAdoConnectionRequest(AdoBoardLink, AdoPat, true)),
            (HttpMethod.Delete, "", null),
            (HttpMethod.Post, "/test", null),
            (HttpMethod.Post, "/pull?dryRun=true", null)
        })
        {
            foreach (var (session, tab) in new (string?, string?)[] { (null, null), ("test-session", null), (null, "test-tab") })
            {
                using var denied = await SendAsync(method, path + suffix, session, tab, body);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
        }
        Assert.Null(await _app.Services.GetRequiredService<IBoardStore>().GetAdoConnectionAsync(_project, boardId, TestContext.Current.CancellationToken));
        Assert.Empty(_ado.Tokens);
    }

    [Fact]
    public async Task AdoConnectTestPullAndUnlink_NeverEchoThePersonalAccessToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        var secrets = _app.Services.GetRequiredService<IAdoSecretStore>();
        using var list = await GetJsonAsync("/api/v1/board/boards");
        var source = list.RootElement.GetProperty("boards")[0].GetProperty("id").GetString()!;

        using var empty = await GetJsonAsync($"/api/v1/board/boards/{source}/ado");
        Assert.False(empty.RootElement.GetProperty("hasToken").GetBoolean());
        Assert.Equal(BoardAdoAuthStatus.None, empty.RootElement.GetProperty("authStatus").GetString());

        // Save gives the connection its own board; the response names it for the form to follow.
        using var saved = await SendJsonAsync(HttpMethod.Put, $"/api/v1/board/boards/{source}/ado",
            new SaveAdoConnectionRequest(AdoBoardLink, AdoPat, true));
        var savedText = await saved.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(AdoPat, savedText);
        string boardId;
        using (var body = JsonDocument.Parse(savedText))
        {
            boardId = body.RootElement.GetProperty("boardId").GetString()!;
            Assert.NotEqual(source, boardId);
            Assert.True(body.RootElement.GetProperty("hasToken").GetBoolean());
            Assert.True(body.RootElement.GetProperty("dedicatedBoard").GetBoolean());
            Assert.Equal("contoso", body.RootElement.GetProperty("organization").GetString());
            Assert.Equal("Web Team", body.RootElement.GetProperty("team").GetString());
            Assert.Equal(AdoBoardLink, body.RootElement.GetProperty("boardLink").GetString());
            Assert.False(body.RootElement.TryGetProperty("personalAccessToken", out _));
        }
        var connection = (await store.GetAdoConnectionAsync(_project, boardId, ct))!;
        Assert.Equal(AdoPat, secrets.ReadToken(connection.Id));

        using var boards = await GetJsonAsync("/api/v1/board/boards");
        var dedicated = Assert.Single(boards.RootElement.GetProperty("boards").EnumerateArray(), b => b.GetProperty("id").GetString() == boardId);
        Assert.Equal("ado", dedicated.GetProperty("remoteProvider").GetString());
        Assert.False(dedicated.GetProperty("isJiraBoard").GetBoolean());

        using var settings = await GetJsonAsync($"/api/v1/board/boards/{boardId}/ado?settings=true");
        Assert.True(settings.RootElement.GetProperty("lanes").GetArrayLength() > 0);
        using var plain = await GetJsonAsync($"/api/v1/board/boards/{boardId}/ado");
        Assert.Equal(JsonValueKind.Null, plain.RootElement.GetProperty("lanes").ValueKind);

        using var tested = await SendAsync(HttpMethod.Post, $"/api/v1/board/boards/{boardId}/ado/test", "test-session", "test-tab");
        var testText = await tested.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.DoesNotContain(AdoPat, testText);
        using (var body = JsonDocument.Parse(testText))
        {
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("Web", body.RootElement.GetProperty("project").GetString());
            Assert.Equal(boardId, body.RootElement.GetProperty("boardId").GetString());
            Assert.Equal("Stories", body.RootElement.GetProperty("board").GetProperty("name").GetString());
            Assert.Equal(4, body.RootElement.GetProperty("board").GetProperty("columns").GetArrayLength());
        }
        Assert.All(_ado.Tokens, token => Assert.Equal(AdoPat, token));

        using var pulled = await SendAsync(HttpMethod.Post, $"/api/v1/board/boards/{boardId}/ado/pull?dryRun=true", "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.OK, pulled.StatusCode);
        using (var body = await ReadJsonAsync(pulled))
        {
            Assert.True(body.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal("ok", body.RootElement.GetProperty("outcome").GetString());
        }

        // Unlink keeps the board and forgets the token; another project's board is not found.
        var foreign = await store.CreateBoardAsync(_project + "-foreign", "Foreign", ct);
        using var outside = await SendAsync(HttpMethod.Delete, $"/api/v1/board/boards/{foreign.Id}/ado", "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        using var unlinked = await SendAsync(HttpMethod.Delete, $"/api/v1/board/boards/{boardId}/ado", "test-session", "test-tab");
        unlinked.EnsureSuccessStatusCode();
        using var repeated = await SendAsync(HttpMethod.Delete, $"/api/v1/board/boards/{boardId}/ado", "test-session", "test-tab");
        repeated.EnsureSuccessStatusCode();
        Assert.False(secrets.HasToken(connection.Id));
        Assert.Null(await store.GetAdoConnectionAsync(_project, boardId, ct));
        Assert.NotNull(await store.GetBoardAsync(_project, boardId, ct));
    }

    [Fact]
    public async Task AdoConfigurationErrors_AreReadable400s_NotServerErrors()
    {
        using var list = await GetJsonAsync("/api/v1/board/boards");
        var boardId = list.RootElement.GetProperty("boards")[0].GetProperty("id").GetString()!;

        using var untested = await SendAsync(HttpMethod.Post, $"/api/v1/board/boards/{boardId}/ado/test", "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.BadRequest, untested.StatusCode);
        using (var body = await ReadJsonAsync(untested))
            Assert.Contains("Save the Azure DevOps connection", body.RootElement.GetProperty("error").GetString());

        using var unpulled = await SendAsync(HttpMethod.Post, $"/api/v1/board/boards/{boardId}/ado/pull", "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.BadRequest, unpulled.StatusCode);

        foreach (var link in new[] { "https://contoso.visualstudio.com/Web/_boards/board/t/Team/Stories", "http://dev.azure.com/contoso/Web/_boards/board/t/Team/Stories",
            "https://user:pass@dev.azure.com/contoso/Web/_boards/board/t/Team/Stories" })
        {
            using var refused = await SendJsonAsync(HttpMethod.Put, $"/api/v1/board/boards/{boardId}/ado", new SaveAdoConnectionRequest(link, AdoPat, true));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var text = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(AdoPat, text);
            Assert.Contains("dev.azure.com", text);
        }

        using var missingBoard = await SendJsonAsync(HttpMethod.Put, "/api/v1/board/boards/brd_missing/ado", new SaveAdoConnectionRequest(AdoBoardLink, AdoPat, true));
        Assert.Equal(HttpStatusCode.BadRequest, missingBoard.StatusCode);
        using (var body = await ReadJsonAsync(missingBoard))
            Assert.Contains("no longer exists", body.RootElement.GetProperty("error").GetString());
        Assert.Empty(_ado.Tokens);
    }

    [Fact]
    public async Task AdoSyncStatusDisablesHostedControlsAndRejectsPublication()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        var board = await store.CreateBoardAsync(_project, "Azure DevOps · Web Team", ct);
        await store.SaveAdoConnectionAsync(new("ado_routes0000000", _project, board.Id, "contoso", "Web", "Web Team", "Stories", null,
            AdoBoardLink, "Stories", null, null, null, null, true, BoardAdoAuthStatus.Saved, true, null, null, DedicatedBoard: true, null, null, null), ct);

        var path = $"/api/v1/board/boards/{board.Id}/sync";
        using var status = await GetJsonAsync(path);
        Assert.Equal("ado", status.RootElement.GetProperty("remoteProvider").GetString());
        Assert.False(status.RootElement.GetProperty("isJiraBoard").GetBoolean());
        Assert.False(status.RootElement.GetProperty("enabled").GetBoolean());
        using var enable = await SendAsync(HttpMethod.Put, path, "test-session", "test-tab", new SetBoardSyncRequest(true));
        Assert.Equal(HttpStatusCode.BadRequest, enable.StatusCode);
        using var sync = await SendAsync(HttpMethod.Post, path + "/now", "test-session", "test-tab");
        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode);
    }

    [Fact]
    public async Task CommentRoutesAcceptSyncToRemoteWithSyncToJiraAsTheAlias()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var card = await store.CreateCardAsync(_project, new(null, "Remote sync", "", null, "medium", null, [], false), ct);

        using var remote = await SendJsonAsync(HttpMethod.Post, $"/api/v1/board/cards/{card.Id}/comments", new { body = "Internal", syncToRemote = false });
        using (var body = await ReadJsonAsync(remote))
            Assert.False(body.RootElement.GetProperty("syncToJira").GetBoolean());
        using var alias = await SendJsonAsync(HttpMethod.Post, $"/api/v1/board/cards/{card.Id}/notes", new { body = "Older client", syncToJira = false });
        using (var body = await ReadJsonAsync(alias))
            Assert.False(body.RootElement.GetProperty("syncToJira").GetBoolean());
        using var wins = await SendJsonAsync(HttpMethod.Post, $"/api/v1/board/local-cards/{card.Id}/comments", new { body = "Shared", syncToJira = false, syncToRemote = true });
        using (var body = await ReadJsonAsync(wins))
            Assert.True(body.RootElement.GetProperty("syncToJira").GetBoolean());
    }
}
