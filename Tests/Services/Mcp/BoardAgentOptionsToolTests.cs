using System.Net;
using System.Net.Http.Json;
using Moq;
using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Services.AgentTools;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

public sealed class BoardAgentOptionsToolTests
{
    [Theory]
    [InlineData(LocalToolApiContext.SessionTokenVariable, null)]
    [InlineData(LocalToolApiContext.TabTokenVariable, null)]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, null)]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "")]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "   ")]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "https://example.com")]
    public async Task MissingCredentialsOrUnsafeOriginNeverSends(string variable, string? value)
    {
        var environment = Environment();
        environment[variable] = value;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var tool = new BoardAgentOptionsTool(factory.Object, key => environment.GetValueOrDefault(key));

        Assert.StartsWith("FAIL:", await tool.ListBoardAgentOptions(TestContext.Current.CancellationToken));
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UsesExistingRootPickerAndShowsEnabledAgentsWithSharedCodexCapabilities()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://127.0.0.1:4321/api/v1/llm-picker/preferences", request.RequestUri!.AbsoluteUri);
            Assert.Equal("root-session-secret", request.Headers.GetValues(LlmProxyCodexConfig.SessionHeaderName).Single());
            Assert.Equal("root-tab-secret", request.Headers.GetValues(LlmProxyCodexConfig.TabHeaderName).Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new LlmPickerPreferencesResponse([
                    new("base:codex", "base", "Base CLIs", "Codex", "codex", null, true, 0),
                    new("base:claude", "base", "Base CLIs", "Claude hidden", "claude", null, false, 1),
                    new("base:shell", "base", "Base CLIs", "Terminal", "shell", null, true, 2),
                    new("env:7:codex", "environment", "Custom Environments", "Astra worker (codex)", "codex", 7, true, 0),
                    new("env:8:claude", "environment", "Custom Environments", "Hidden environment", "claude", 8, false, 1)
                ]), AppJsonSerializerContext.Default.LlmPickerPreferencesResponse)
            });
        }));

        var result = await Tool(client).ListBoardAgentOptions(TestContext.Current.CancellationToken);

        Assert.Contains("- base:codex: Codex", result);
        Assert.Contains("- env:7:codex: Astra worker (codex)", result);
        Assert.DoesNotContain("base:claude", result);
        Assert.DoesNotContain("base:shell", result);
        Assert.DoesNotContain("env:8", result);
        Assert.DoesNotContain("secret", result);
        Assert.Contains("Effort: default, minimal, low, medium, high, xhigh, max, ultra", result);
        Assert.DoesNotContain("Start mode:", result);
        Assert.Contains("gpt-6-astra: default, fast, ultrafast", result);
        Assert.Contains("gpt-6.1-sol: default, fast\n", result);
        foreach (var model in CodexModelCapabilities.Models)
            Assert.Contains($"- {model}:", result);
        Assert.Contains("baseLlmOptions cannot override it", result);
        Assert.Contains("not an already-running agent", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ServerErrorsNeverEchoResponseBodies(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("secret response body")
        })));

        var result = await Tool(client).ListBoardAgentOptions(TestContext.Current.CancellationToken);

        Assert.StartsWith("FAIL:", result);
        Assert.Contains(((int)status).ToString(), result);
        Assert.DoesNotContain("secret", result);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("missing-items")]
    [InlineData("oversized")]
    public async Task InvalidResponsesFailSafely(string kind)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(kind switch
            {
                "oversized" => new string('x', 512 * 1024 + 1),
                "missing-items" => "{}",
                _ => "secret invalid JSON"
            })
        })));

        var result = await Tool(client).ListBoardAgentOptions(TestContext.Current.CancellationToken);

        Assert.StartsWith("FAIL:", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new Handler((_, token) => Task.FromCanceled<HttpResponseMessage>(token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tool(client).ListBoardAgentOptions(cancellation.Token));
    }

    private static BoardAgentOptionsTool Tool(HttpClient client)
    {
        var environment = Environment();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(BoardAgentLaunchTool.HttpClientName)).Returns(client);
        return new BoardAgentOptionsTool(factory.Object, key => environment.GetValueOrDefault(key));
    }

    private static Dictionary<string, string?> Environment() => new()
    {
        [LocalToolApiContext.ApiBaseUrlVariable] = "http://127.0.0.1:4321",
        [LocalToolApiContext.SessionTokenVariable] = "root-session-secret",
        [LocalToolApiContext.TabTokenVariable] = "root-tab-secret"
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
