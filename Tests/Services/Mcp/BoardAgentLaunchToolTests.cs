using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Mcp;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

public sealed class BoardAgentLaunchToolTests
{
    private const string StartedSessionId = "8d9975e7-f5dd-4991-a183-a3adf86bb4f1";

    [Theory]
    [InlineData(LocalToolApiContext.SessionTokenVariable, null)]
    [InlineData(LocalToolApiContext.TabTokenVariable, " ")]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, null)]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, " ")]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "https://example.com")]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "http://127.0.0.1.evil.test")]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "file:///tmp/launch")]
    public async Task MissingCredentialsOrNonLoopbackDestinationNeverSends(string variable, string? value)
    {
        var environment = Environment();
        environment[variable] = value;
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var tool = new BoardAgentLaunchTool(clients.Object, name => environment.GetValueOrDefault(name));
        Assert.StartsWith("FAIL:", await tool.StartBoardAgent("VIBE-42", TestContext.Current.CancellationToken));
        clients.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../other/route")]
    [InlineData("card?selection=evil")]
    [InlineData("card\nname")]
    [InlineData("%2e%2e")]
    public async Task InvalidCardIdentifierNeverSends(string card)
    {
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var tool = new BoardAgentLaunchTool(clients.Object, _ => null);
        Assert.StartsWith("FAIL: Enter a card key", await tool.StartBoardAgent(card, TestContext.Current.CancellationToken));
        clients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UsesRootCredentialsAndSavedAssignmentThenReturnsSessionToPoll()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:4321/api/v1/board/cards/VIBE-42/launch", request.RequestUri!.AbsoluteUri);
            Assert.Equal("root-session-secret", request.Headers.GetValues(LlmProxyCodexConfig.SessionHeaderName).Single());
            Assert.Equal("root-tab-secret", request.Headers.GetValues(LlmProxyCodexConfig.TabHeaderName).Single());
            var body = await request.Content!.ReadFromJsonAsync(AppJsonSerializerContext.Default.LaunchBoardCardRequest, ct);
            Assert.NotNull(body);
            Assert.Equal("work", body.Intent);
            Assert.Null(body.Selection);
            Assert.Null(body.Review);
            Assert.Null(body.Question);
            return Response();
        }));

        var result = await Tool(client).StartBoardAgent("  VIBE-42  ", TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Contains("Started agent for card card_42.", result);
        Assert.Contains("Session ID: " + StartedSessionId, result);
        Assert.Contains($"get_board_agent_status(card: \"card_42\", sessionId: \"{StartedSessionId}\")", result);
        Assert.Contains("work is not yet complete", result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("sensitive-checkout", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailureNeverRetriesOrEchoesBodies(HttpStatusCode status)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("secret server details") });
        }));
        var result = await Tool(client).StartBoardAgent("VIBE-42", TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.StartsWith("FAIL:", result);
        Assert.Contains($"HTTP {(int)status}", result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("Started agent", result);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("oversized")]
    [InlineData("missing-session")]
    [InlineData("invalid-card")]
    [InlineData("timeout")]
    public async Task UncertainLaunchDirectsStatusCheckWithoutRetrying(string kind)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (kind == "timeout") throw new TaskCanceledException("secret error");
            return Task.FromResult(kind switch
            {
                "missing-session" => Response(sessionId: null),
                "invalid-card" => Response(cardId: "secret\ncard"),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(kind == "oversized" ? new string('x', 16385) : "secret not json")
                }
            });
        }));
        var result = await Tool(client).StartBoardAgent("VIBE-42", TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.StartsWith("FAIL: Could not confirm", result);
        Assert.Contains("get_board_agent_status", result);
        Assert.Contains("before retrying", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tool(client).StartBoardAgent("VIBE-42", cancellation.Token));
    }

    [Fact]
    public void ClientRegistrationDisablesRedirectsProxiesAndCookies()
    {
        var services = new ServiceCollection();
        services.AddBoardAgentLaunchMcp();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<BoardAgentLaunchTool>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<BoardAgentOptionsTool>());
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(BoardAgentLaunchTool.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(BoardAgentLaunchTool.HttpClientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        var transport = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseProxy);
        Assert.False(transport.UseCookies);
    }

    private static BoardAgentLaunchTool Tool(HttpClient client)
    {
        var environment = Environment();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(factory => factory.CreateClient(BoardAgentLaunchTool.HttpClientName)).Returns(client);
        return new(clients.Object, name => environment.GetValueOrDefault(name));
    }

    private static HttpResponseMessage Response(string? sessionId = StartedSessionId, string cardId = "card_42") => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new LaunchBoardCardResponse("tab_42", sessionId, "codex", "sensitive-checkout", cardId, "VIBE-42", "codex"),
            AppJsonSerializerContext.Default.LaunchBoardCardResponse)
    };

    private static Dictionary<string, string?> Environment() => new()
    {
        [LocalToolApiContext.ApiBaseUrlVariable] = "http://127.0.0.1:4321",
        [LocalToolApiContext.SessionTokenVariable] = "root-session-secret",
        [LocalToolApiContext.TabTokenVariable] = "root-tab-secret",
        [AgentSessionTool.BaseUrlVariable] = "http://127.0.0.1:9876",
        [AgentSessionTool.SessionTokenVariable] = "child-session-secret",
        [AgentSessionTool.TabTokenVariable] = "child-tab-secret"
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
