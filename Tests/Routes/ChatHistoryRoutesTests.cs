using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Routes;

public sealed class ChatHistoryRoutesTests
{
    private static readonly HttpClient Client = new();

    [Fact]
    public async Task HistoryAndSingleLookup_EnrichCards_UseAotJson_AndRequireBothCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var board = new Mock<IBoardStore>(MockBehavior.Strict);
        var item = new ChatHistoryItem("session", "codex", "Review", "C:/project", "Project",
            DateTime.UtcNow, null, null, null, null, "My renamed chat", 1, "Automatic preview", 1, null);
        repository.Setup(r => r.GetChatHistoryPageAsync(20, 0, null, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        repository.Setup(r => r.GetChatHistoryItemAsync("session", It.IsAny<CancellationToken>())).ReturnsAsync(item);
        repository.Setup(r => r.GetChatHistoryItemAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((ChatHistoryItem?)null);
        board.Setup(s => s.GetSessionCardsAsync(It.Is<IReadOnlyList<string>>(ids => ids.Count == 1 && ids[0] == "session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new("session", "card", "VB-M66G2-64", "Filters <&>"), new("session", "second", "VB-OTHER-65", "Second card")]);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(repository.Object);
        builder.Services.AddSingleton(board.Object);
        builder.Services.AddSingleton(Mock.Of<ISessionTranscriptService>());
        builder.Services.AddSingleton(Mock.Of<ISummaryService>());
        builder.Services.AddSingleton(Mock.Of<ISessionDataExportService>());
        builder.Services.AddSingleton<IChatHistoryService, ChatHistoryService>();
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "session-token");
        auth.Setup(a => a.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "tab-token");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        ChatHistoryRoutes.Map(app);
        await app.StartAsync(ct);
        try
        {
            foreach (var path in new[] { "/api/v1/chatHistory", "/api/v1/chatHistory/session" })
            {
                foreach (var credential in new[] { "none", "session", "tab", "both" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(app.Urls.First()), path));
                    if (credential is "session" or "both") request.Headers.Add("viberails_session", "session-token");
                    if (credential is "tab" or "both") request.Headers.Add("viberails_tab", "tab-token");
                    using var response = await Client.SendAsync(request, ct);
                    if (credential != "both")
                    {
                        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    var row = path.EndsWith("/session") ? json.RootElement : json.RootElement.GetProperty("items")[0];
                    Assert.Equal("My renamed chat", row.GetProperty("sessionDisplayName").GetString());
                    Assert.Equal("Review", row.GetProperty("environmentName").GetString());
                    var cards = row.GetProperty("boardCards");
                    Assert.Equal(2, cards.GetArrayLength());
                    Assert.Equal("VB-M66G2-64", cards[0].GetProperty("key").GetString());
                    Assert.Equal("Filters <&>", cards[0].GetProperty("title").GetString());
                }
            }
            Assert.Null(await app.Services.GetRequiredService<IChatHistoryService>().GetSessionAsync("missing", ct));
            board.Verify(s => s.GetSessionCardsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}
