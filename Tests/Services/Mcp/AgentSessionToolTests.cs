using System.Net;
using System.Net.Http.Json;
using Moq;
using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

public sealed class AgentSessionToolTests
{
    [Theory]
    [InlineData("http://example.com", true)]
    [InlineData("http://127.0.0.1:4321", false)]
    [InlineData("file:///tmp/control", true)]
    public async Task MissingIdentityOrNonLocalDestinationNeverSendsCredentials(string url, bool hasSession)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var environment = Environment(url);
        if (!hasSession) environment.Remove(LocalToolApiContext.CurrentSessionIdVariable);
        var tool = new AgentSessionTool(factory.Object, key => environment.GetValueOrDefault(key));
        Assert.StartsWith("FAIL:", await tool.EndAgentSession(TestContext.Current.CancellationToken));
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task SendsOnlyTheCallingSessionAndReportsAcceptanceTruthfully(HttpStatusCode status)
    {
        var environment = Environment("http://127.0.0.1:4321/llm/openai/v1");
        var handler = new Handler(request =>
        {
            Assert.Equal("http://127.0.0.1:4321" + AgentSessionControlRoutes.Path, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(environment[LocalToolApiContext.CurrentSessionIdVariable], request.Headers.GetValues(AgentSessionControlRoutes.SessionHeader).Single());
            Assert.Equal("session-secret", request.Headers.GetValues(LlmProxyCodexConfig.SessionHeaderName).Single());
            Assert.Equal("tab-secret", request.Headers.GetValues(LlmProxyCodexConfig.TabHeaderName).Single());
            Assert.Null(request.Content);
            return new(status) { Content = JsonContent.Create(new AgentSessionEndResponse(DateTimeOffset.UtcNow.AddSeconds(30)), AppJsonSerializerContext.Default.AgentSessionEndResponse) };
        });
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(AgentSessionTool.HttpClientName)).Returns(client);
        var result = await new AgentSessionTool(factory.Object, key => environment.GetValueOrDefault(key)).EndAgentSession(TestContext.Current.CancellationToken);
        Assert.Equal(status != HttpStatusCode.OK, result.StartsWith("FAIL:"));
        Assert.DoesNotContain("secret", result);
    }

    private static Dictionary<string, string> Environment(string url) => new()
    {
        [AgentSessionTool.BaseUrlVariable] = url,
        [AgentSessionTool.SessionTokenVariable] = "session-secret",
        [AgentSessionTool.TabTokenVariable] = "tab-secret",
        [LocalToolApiContext.CurrentSessionIdVariable] = "b47e0d56-d697-4675-81d9-aa3274dd0c9d"
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
