using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using VibeRails;
using VibeRails.Services.Mcp;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services;

[Collection("ProcessEnvIsolation")]
public sealed class OutboundHttpRegistrationTests
{
    [Theory]
    [InlineData("ISummaryService")]
    [InlineData("llm-proxy")]
    [InlineData("board-sync")]
    [InlineData("conversation-titles")]
    [InlineData("token-savings")]
    [InlineData("IRemoteStateService")]
    [InlineData("ISessionDataExportService")]
    [InlineData("complete-backups")]
    [InlineData("IPushNotificationService")]
    [InlineData("UpdateService")]
    [InlineData("jira-cloud")]
    [InlineData("azure-devops")]
    public void OutboundClientsDisableRedirectsAndCookies(string name)
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider();
        var handler = PrimaryHandler(provider, name);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Theory]
    [InlineData("llm-proxy", true)]
    [InlineData("ISummaryService", false)]
    [InlineData("board-sync", false)]
    [InlineData("conversation-titles", false)]
    [InlineData("token-savings", false)]
    [InlineData("IRemoteStateService", false)]
    [InlineData("ISessionDataExportService", false)]
    [InlineData("complete-backups", false)]
    [InlineData("IPushNotificationService", false)]
    [InlineData("UpdateService", false)]
    [InlineData("jira-cloud", false)]
    [InlineData("azure-devops", false)]
    public void OnlyTheLlmRelayOffersWindowsSignInToAnAuthenticatingProxy(string name, bool signsIn)
    {
        // VIBE-244 / API_SEC.md "Proxy sign-in": the relay (and the gateway Test, which resolves
        // the same client) answers a 407; every other outbound client keeps offering nothing.
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider();
        var handler = PrimaryHandler(provider, name);
        Assert.Equal(signsIn ? CredentialCache.DefaultCredentials : null, handler.DefaultProxyCredentials);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TokenSaverHasTheSameHardenedHandlerInBothTransports(bool stdio)
    {
        var services = new ServiceCollection();
        if (stdio)
            McpStdioHost.ConfigureServices(services);
        else
            MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider();
        var handler = PrimaryHandler(provider, TokenSaverTool.HttpClientName);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
    }

    [Theory]
    [InlineData("jira-cloud")]
    [InlineData("azure-devops")]
    [InlineData("UpdateService")]
    [InlineData(TokenSaverTool.HttpClientName)]
    public async Task ProductionHandlersDoNotFollowRedirectsOrReplayCookies(string name)
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();
        app.Urls.Add("http://127.0.0.1:0");
        var targetCalls = 0;
        string? observedCookie = null;
        app.MapGet("/redirect", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = "/target";
            context.Response.Headers.SetCookie = "tracker=value; Path=/";
        });
        app.MapGet("/target", () => { Interlocked.Increment(ref targetCalls); });
        app.MapGet("/observe", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            observedCookie = context.Request.Headers.Cookie.ToString();
        });
        await app.StartAsync(Ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        var services = new ServiceCollection();
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var redirect = await http.GetAsync(address + "/redirect", Ct);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal(0, targetCalls);
        using var observe = await http.GetAsync(address + "/observe", Ct);
        observe.EnsureSuccessStatusCode();
        Assert.Equal("", observedCookie);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClientHandler PrimaryHandler(IServiceProvider provider, string name)
    {
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler is DelegatingHandler wrapper)
            handler = wrapper.InnerHandler!;
        return Assert.IsType<HttpClientHandler>(handler);
    }
}
