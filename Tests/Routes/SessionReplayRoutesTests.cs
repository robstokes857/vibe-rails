using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.Data.Replay;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Routes;

public sealed class SessionReplayRoutesTests
{
    [Fact]
    public async Task RecordingReadsRequireBothCredentials_AndKeepBoardLabelsBehindStore()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ValidateToken(It.IsAny<string?>())).Returns((string? s) => s == "test-session");
        auth.Setup(a => a.ValidateTabToken(It.IsAny<string?>())).Returns((string? s) => s == "test-tab");
        var store = new Mock<IReplayStore>(MockBehavior.Strict);
        var manifest = new Manifest(new("one", "codex", "", "", "", "Recording", 0, 1, 0, true, 0),
            [], [], [], [], "raw", 1, 0, 1, 3, 1, []);
        store.Setup(s => s.Manifest("one")).Returns(manifest);
        store.Setup(s => s.Manifest("missing")).Returns((Manifest?)null);
        store.Setup(s => s.Frames("one", 0, 1, "raw")).Returns(new FramePage([new(1, 0, [65, 66, 67], 80, 24)], 1, true));
        store.Setup(s => s.Diff("wrong-session", 1)).Returns((DiffDetail?)null);
        store.Setup(s => s.ExchangeDetail("wrong-session", "capture")).Returns((ExchangeDetail?)null);
        var board = new Mock<IBoardStore>();
        board.Setup(b => b.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BoardSessionCard("one", "card", "VB-1", "Card title", "VIBE-1")]);
        builder.Services.AddSingleton(auth.Object);
        builder.Services.AddSingleton(store.Object);
        builder.Services.AddSingleton(board.Object);
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        SessionReplayRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        try
        {
            string[] paths = ["status", "sessions", "sessions/one", "sessions/one/frames?after=0&max=1&source=raw",
                "sessions/one/exchanges?after=0&max=1", "sessions/one/changes/1", "sessions/one/exchanges/capture"];
            foreach (var path in paths)
            foreach (var credentials in new[] { 0, 1, 2 })
            {
                using var response = await Get(path, credentials);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            store.VerifyNoOtherCalls();
            using var success = await Get("sessions/one");
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            Assert.True(success.Headers.CacheControl?.NoStore);
            var loaded = await success.Content.ReadFromJsonAsync<Manifest>(TestContext.Current.CancellationToken);
            Assert.Equal("VIBE-1", Assert.Single(loaded!.Cards).Key);
            using var frames = await Get(paths[3]);
            Assert.Equal("ABC", System.Text.Encoding.UTF8.GetString((await frames.Content.ReadFromJsonAsync<FramePage>(TestContext.Current.CancellationToken))!.Items[0].Data));
            foreach (var path in new[] { "sessions/missing", "sessions/wrong-session/changes/1", "sessions/wrong-session/exchanges/capture" })
            {
                using var missing = await Get(path);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            using var invalid = await Get("sessions/one/frames?after=-1&max=1&source=raw");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally { await app.StopAsync(CancellationToken.None); }

        async Task<HttpResponseMessage> Get(string path, int credentials = 3)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session-replay/" + path);
            if ((credentials & 1) != 0) request.Headers.Add("viberails_session", "test-session");
            if ((credentials & 2) != 0) request.Headers.Add("viberails_tab", "test-tab");
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }
    }
}
