using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VibeRails;
using VibeRails.Services.Integrations.VibeCodeRemote;
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
    [InlineData("account-entitlements")]
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
    [InlineData("account-entitlements", false)]
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

    [Fact]
    public void TheEntitlementReadIsBoundedByItsNamedClient_AndResolvesInEveryProcessRole()
    {
        // The plan read rides the terminal start path in tab children too, so it must be one small,
        // bounded request: ten seconds, 16 KiB, and (above) no redirects or cookies.
        foreach (var args in new string[][] { [], ["--parent-pid", "1"] })
        {
            var services = new ServiceCollection();
            MapRegisterServices.Register(services, args, "http://127.0.0.1:12345");
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(AccountEntitlementsClient.HttpClientName);
            Assert.Equal(TimeSpan.FromSeconds(10), http.Timeout);
            Assert.Equal(16 * 1024, http.MaxResponseContentBufferSize);
            Assert.IsType<AccountEntitlementsClient>(provider.GetRequiredService<IAccountEntitlements>());
            Assert.Same(provider.GetRequiredService<IAccountEntitlements>(), provider.GetRequiredService<IAccountEntitlements>());
        }
    }

    [Fact]
    public async Task TheRealRegistrationHandsTheEntitlementSnapshotToTheHostedClients()
    {
        // The clients take the snapshot as an optional dependency. If the container left it out, a
        // pro_required answer would still be reported but never noted, and the loops would keep asking.
        var denied = new DenialHandler();
        var noted = new Tests.Services.Integrations.FakeAccountEntitlements();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VibeRails:FrontendUrl"] = "https://plans.example.test" }).Build());
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        services.AddSingleton<IAccountEntitlements>(noted);
        services.AddHttpClient(VibeRails.Services.Board.Sync.BoardSyncHttpClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => denied);
        services.AddHttpClient<VibeRails.Services.Board.Sharing.CardShareClient>().ConfigurePrimaryHttpMessageHandler(() => denied);
        var originalKey = VibeRails.Utils.ParserConfigs.GetApiKey();
        VibeRails.Utils.ParserConfigs.SetApiKey("registration-fixture-key");
        try
        {
            using var provider = services.BuildServiceProvider();

            // Built by a factory: the board sync client.
            var boards = provider.GetRequiredService<VibeRails.Services.Board.Sync.IBoardSyncClient>();
            var boardError = await Assert.ThrowsAsync<VibeRails.Services.Board.Sync.BoardSyncClientException>(() => boards.DiscoverAsync(Ct));
            Assert.Equal("pro_required", boardError.Code);

            // Built by the typed-client activator: the card sharing transport.
            var cards = provider.GetRequiredService<VibeRails.Services.Board.Sharing.CardShareClient>();
            var cardError = await Assert.ThrowsAsync<VibeRails.Services.Board.Sharing.CardShareTransportException>(
                () => cards.SupportsListedPeopleAsync("registration-fixture-key", Ct));
            Assert.Equal("pro_required", cardError.Code);

            Assert.Equal([ProFeatures.RemoteBoards, ProFeatures.CardSharing], noted.Noted);
        }
        finally { VibeRails.Utils.ParserConfigs.SetApiKey(originalKey); }
    }

    private sealed class DenialHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            response.Headers.Add("X-VibeRails-Denial", "pro_required");
            return Task.FromResult(response);
        }
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
