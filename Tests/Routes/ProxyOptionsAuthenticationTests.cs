using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using TokenSaver;
using Xunit;

namespace Tests.Routes;

public sealed class ProxyOptionsAuthenticationTests
{
    [Fact]
    public async Task EveryProxyAuthenticatesOptionsBeforeInspectingFeatureState()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ILlmProxyAuthGate>(new Denied());
        await using var app = builder.Build();
        LlmProxyRoutes.Map(app); LlmAnthropicProxyRoutes.Map(app); LlmZaiProxyRoutes.Map(app);
        LlmXaiProxyRoutes.Map(app); LlmCliChatProxyRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient();
        foreach (var provider in new[] { "openai", "anthropic", "zai", "xai", "cli-chat" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, app.Urls.First() + "/llm/" + provider + "/probe");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class Denied : ILlmProxyAuthGate
    {
        public bool ValidateSessionToken(string? token) => false;
        public bool ValidateTabToken(string? token) => false;
    }
}
