using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Routes;

/// <summary>VIBE-36: the tab list names the Board card each tab's session is linked to.</summary>
public sealed class TerminalTabsRoutesTests
{
    private static readonly HttpClient Client = new();
    private static readonly DateTime Created = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ListTabs_NamesThePrimaryCardOfEachLinkedSession_UsingAotJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var tabHost = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        tabHost.SetupGet(h => h.MaxTabs).Returns(100);
        tabHost.Setup(h => h.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new TerminalTabStatusResponse("linked", Created, true, "session-a", "claude"),
            new TerminalTabStatusResponse("unlinked", Created, true, "session-b", "codex"),
            new TerminalTabStatusResponse("blank", Created, false)
        ]);
        var board = new Mock<IBoardStore>(MockBehavior.Strict);
        board.Setup(b => b.GetAttentionSessionIdsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "session-a" });
        board.Setup(b => b.GetSessionCardsAsync(
                It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "session-a", "session-b" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new BoardSessionCard("session-a", "card-1", "VB-8L17B-108", "Link back <&>", "VIBE-36"),
                new BoardSessionCard("session-a", "card-2", "VB-8L17B-109", "Attached later")
            ]);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(tabHost.Object);
        builder.Services.AddSingleton(board.Object);
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "session-token");
        auth.Setup(a => a.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "tab-token");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        TerminalTabsRoutes.Map(app);
        await app.StartAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(app.Urls.First()), "/api/v1/terminal/tabs"));
            request.Headers.Add("viberails_session", "session-token");
            request.Headers.Add("viberails_tab", "tab-token");
            using var response = await Client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tabs = json.RootElement.GetProperty("tabs");
            Assert.Equal(3, tabs.GetArrayLength());
            var card = tabs[0].GetProperty("boardCard");
            Assert.Equal("card-1", card.GetProperty("id").GetString());
            Assert.Equal("VB-8L17B-108", card.GetProperty("key").GetString());
            Assert.Equal("Link back <&>", card.GetProperty("title").GetString());
            Assert.Equal("VIBE-36", card.GetProperty("displayId").GetString());
            Assert.True(tabs[0].GetProperty("needsAttention").GetBoolean());
            Assert.False(tabs[1].GetProperty("needsAttention").GetBoolean());
            Assert.Equal(JsonValueKind.Null, tabs[1].GetProperty("boardCard").ValueKind);
            Assert.Equal(JsonValueKind.Null, tabs[2].GetProperty("boardCard").ValueKind);
            Assert.Equal(100, json.RootElement.GetProperty("maxTabs").GetInt32());
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    // VIBE-159: the script workbench's agent pane owns its tab, so the Terminals page can leave it
    // out of the strip and the pane can find it again.
    [Fact]
    public async Task CreateTab_WithOwner_CreatesAnOwnedTab_AndAnOrdinaryTabWithoutOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var tabHost = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        tabHost.Setup(h => h.CreateTabAsync("script-agent:reg-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse("owned", Created, false, Owner: "script-agent:reg-1"));
        tabHost.Setup(h => h.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse("plain", Created, false));
        tabHost.Setup(h => h.CreateTabAsync("bad\u0007owner", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("A terminal tab owner cannot contain control characters."));

        await using var app = await StartAsync(tabHost.Object, new Mock<IBoardStore>(MockBehavior.Strict).Object, ct);

        using (var response = await SendAsync(app, HttpMethod.Post, "/api/v1/terminal/tabs?owner=script-agent%3Areg-1", ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("owned", json.RootElement.GetProperty("tabId").GetString());
            Assert.Equal("script-agent:reg-1", json.RootElement.GetProperty("owner").GetString());
        }
        using (var response = await SendAsync(app, HttpMethod.Post, "/api/v1/terminal/tabs", ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("plain", json.RootElement.GetProperty("tabId").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("owner").ValueKind);
        }
        using (var response = await SendAsync(app, HttpMethod.Post, "/api/v1/terminal/tabs?owner=bad%07owner", ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        tabHost.Verify(h => h.CreateTabAsync(It.IsAny<CancellationToken>()), Times.Once);
        tabHost.Verify(h => h.CreateTabAsync("script-agent:reg-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  script-agent:reg-1 ", "script-agent:reg-1")]
    public void NormalizeOwner_TrimsAndTreatsBlankAsNoOwner(string? owner, string? expected)
        => Assert.Equal(expected, TerminalTabHostService.NormalizeOwner(owner));

    [Theory]
    [InlineData("script-agent:\nreg-1")]
    [InlineData("a\u001bb")]
    public void NormalizeOwner_RefusesControlCharacters(string owner)
        => Assert.Throws<ArgumentException>(() => TerminalTabHostService.NormalizeOwner(owner));

    [Fact]
    public void NormalizeOwner_RefusesAnOverlongLabel()
    {
        Assert.Equal(new string('a', TerminalTabHostService.MaxOwnerLength),
            TerminalTabHostService.NormalizeOwner(new string('a', TerminalTabHostService.MaxOwnerLength)));
        Assert.Throws<ArgumentException>(() =>
            TerminalTabHostService.NormalizeOwner(new string('a', TerminalTabHostService.MaxOwnerLength + 1)));
    }

    private static async Task<WebApplication> StartAsync(ITerminalTabHostService tabHost, IBoardStore board, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(tabHost);
        builder.Services.AddSingleton(board);
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "session-token");
        auth.Setup(a => a.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "tab-token");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        TerminalTabsRoutes.Map(app);
        await app.StartAsync(ct);
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplication app, HttpMethod method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(app.Urls.First()), path));
        request.Headers.Add("viberails_session", "session-token");
        request.Headers.Add("viberails_tab", "tab-token");
        return await Client.SendAsync(request, ct);
    }

    [Fact]
    public async Task WithBoardCards_BoardStoreFailure_KeepsEveryTab()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = new Mock<IBoardStore>(MockBehavior.Strict);
        board.Setup(b => b.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("board.db is locked"));
        TerminalTabStatusResponse[] tabs =
        [
            new("one", Created, true, "session-a", "claude"),
            new("two", Created, true, "session-b", "codex")
        ];

        var result = await TerminalTabsRoutes.WithBoardCardsAsync(tabs, board.Object, ct);

        Assert.Equal(tabs, result);
        Assert.All(result, tab => Assert.Null(tab.BoardCard));
    }

    [Fact]
    public async Task WithBoardCards_NoSessions_DoesNotReadTheBoard()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = new Mock<IBoardStore>(MockBehavior.Strict);
        TerminalTabStatusResponse[] tabs = [new("blank", Created, false), new("empty", Created, false, "")];

        var result = await TerminalTabsRoutes.WithBoardCardsAsync(tabs, board.Object, ct);

        Assert.Equal(tabs, result);
        board.VerifyNoOtherCalls();
    }
}
