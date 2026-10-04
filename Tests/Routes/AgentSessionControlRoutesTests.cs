using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.Services.Terminal;
using TokenSaver;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Routes;

public sealed class AgentSessionControlRoutesTests
{
    private static readonly HttpClient Client = new();

    [Theory]
    [InlineData(false, false, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, false, HttpStatusCode.Unauthorized)]
    [InlineData(false, true, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, true, false, HttpStatusCode.Conflict)]
    [InlineData(true, true, true, HttpStatusCode.OK)]
    public async Task RequiresBothCredentialsAndTheExactCurrentSession(bool sessionToken, bool tabToken,
        bool ownSession, HttpStatusCode expected)
    {
        var own = Guid.NewGuid().ToString();
        var clock = new AgentSessionEndSchedulerTests.TimerClock();
        var stopped = new List<string>();
        using var scheduler = new AgentSessionEndScheduler(id => id == own,
            id => { stopped.Add(id); return Task.FromResult(true); }, clock);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        var auth = new Mock<IAuthService>();
        auth.Setup(service => service.ValidateToken("session-secret")).Returns(true);
        auth.Setup(service => service.ValidateTabToken("tab-secret")).Returns(true);
        builder.Services.AddSingleton(auth.Object);
        builder.Services.AddSingleton<ILlmProxyAuthGate>(new LlmProxyAuthGateAdapter(auth.Object));
        builder.Services.AddSingleton(scheduler);
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        AgentSessionControlRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(app.Urls.First()), AgentSessionControlRoutes.Path));
        if (sessionToken) request.Headers.Add(LlmProxyCodexConfig.SessionHeaderName, "session-secret");
        if (tabToken) request.Headers.Add(LlmProxyCodexConfig.TabHeaderName, "tab-secret");
        request.Headers.Add(AgentSessionControlRoutes.SessionHeader, ownSession ? own : Guid.NewGuid().ToString());
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.AgentSessionEndResponse, TestContext.Current.CancellationToken);
            Assert.Equal(clock.GetUtcNow().AddSeconds(30), result!.ClosesAtUtc);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        clock.Advance(29);
        Assert.Empty(stopped);
        clock.Advance(1);
        Assert.Equal(expected == HttpStatusCode.OK ? [own] : Array.Empty<string>(), stopped);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}
