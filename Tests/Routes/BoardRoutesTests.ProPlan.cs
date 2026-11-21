using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// The hosted service's <c>pro_required</c> denial reaches the dashboard as a 403 with its code,
/// feature and payer, so it can offer Upgrade (or say the owner must renew) instead of "check your
/// API key". Every other sync failure keeps the plain error body it had.
/// </summary>
public sealed partial class BoardRoutesTests
{
    [Theory]
    [InlineData("account", BoardSyncProPause.AccountMessage)]
    [InlineData("board_owner", BoardSyncProPause.BoardOwnerMessage)]
    public async Task AProRequiredDenialIsAnswered403WithItsCodeFeatureAndPayer(string payer, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var boardId = (await store.GetBoardsAsync(_project, ct)).Single().Id;
        var client = Mock.Get(_app.Services.GetRequiredService<IBoardSyncClient>());
        var denied = BoardSyncProPause.Failure(new ProDenial(ProFeatures.RemoteBoards, payer));
        client.SetupGet(c => c.IsConfigured).Returns(true);
        client.SetupGet(c => c.DestinationKey).Returns("account");
        client.Setup(c => c.DiscoverPageAsync(0, It.IsAny<CancellationToken>(), "account")).ThrowsAsync(denied);
        client.Setup(c => c.PublishAsync(It.IsAny<BoardSyncPublishRequest>(), It.IsAny<CancellationToken>(), "account")).ThrowsAsync(denied);

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/api/v1/board/remote", null),                                               // Remote boards
            (HttpMethod.Put, $"/api/v1/board/boards/{boardId}/sync", new SetBoardSyncRequest(true)),      // publish
            (HttpMethod.Post, $"/api/v1/board/boards/{boardId}/sync/now", null),                          // sync now
            (HttpMethod.Get, $"/api/v1/board/boards/{boardId}/sharing", null)                             // collaborators
        })
        {
            using var response = await SendAsync(method, path, "test-session", "test-tab", body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var json = await ReadJsonAsync(response);
            Assert.Equal(["error", "code", "feature", "payer"], json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(message, json.RootElement.GetProperty("error").GetString());
            Assert.Equal("pro_required", json.RootElement.GetProperty("code").GetString());
            Assert.Equal("remote_boards", json.RootElement.GetProperty("feature").GetString());
            Assert.Equal(payer, json.RootElement.GetProperty("payer").GetString());
        }
    }

    [Fact]
    public async Task OtherSyncFailuresKeepTheirPlainErrorBody_AndTheStatusReportsThePauseFromItsStoredReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _app.Services.GetRequiredService<IBoardStore>();
        await store.EnsureDefaultColumnsAsync(_project, ct);
        var board = (await store.GetBoardsAsync(_project, ct)).Single();
        var client = Mock.Get(_app.Services.GetRequiredService<IBoardSyncClient>());
        client.SetupGet(c => c.DestinationKey).Returns("account");
        client.Setup(c => c.DiscoverPageAsync(0, It.IsAny<CancellationToken>(), "account"))
            .ThrowsAsync(new BoardSyncClientException("viberails.ai rejected the API key. Check it in Settings.", "unauthorized", 403));

        using (var rejected = await SendAsync(HttpMethod.Get, "/api/v1/board/remote", "test-session", "test-tab"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            using var json = await ReadJsonAsync(rejected);
            Assert.Equal(["error"], json.RootElement.EnumerateObject().Select(p => p.Name));
        }

        var link = new BoardSyncLinkRecord(board.Id, Guid.NewGuid().ToString("D"), 0, true, null, null, "Sync failed.", default, default,
            _project, board.Name, "account");
        Assert.NotNull(await store.SaveSyncLinkAsync(link, ct));
        var path = $"/api/v1/board/boards/{board.Id}/sync";
        using (var ordinary = await GetJsonAsync(path))
        {
            Assert.Equal("Sync failed.", ordinary.RootElement.GetProperty("lastError").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, ordinary.RootElement.GetProperty("pausedCode").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, ordinary.RootElement.GetProperty("pausedPayer").ValueKind);
        }
        foreach (var (reason, payer) in new[] { (BoardSyncProPause.AccountMessage, "account"), (BoardSyncProPause.BoardOwnerMessage, "board_owner") })
        {
            await store.SaveSyncLinkAsync((await store.GetSyncLinkAsync(_project, board.Id, ct))! with { LastError = reason }, ct);
            using var paused = await GetJsonAsync(path);
            Assert.Equal(reason, paused.RootElement.GetProperty("lastError").GetString());
            Assert.Equal("pro_required", paused.RootElement.GetProperty("pausedCode").GetString());
            Assert.Equal(payer, paused.RootElement.GetProperty("pausedPayer").GetString());
        }
    }
}
