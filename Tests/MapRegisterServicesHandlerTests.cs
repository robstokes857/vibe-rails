using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VibeRails;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests;

/// <summary>
/// The outbound HTTP clients that carry a credential in a header must not follow redirects: a
/// redirect would replay the header and body wherever the server pointed. The factory method
/// itself is pinned in <c>DataExportEndpointConfigurationTests</c>; this checks that the named
/// Board sync client (VB-51) is actually wired to it in the real registration.
/// </summary>
public sealed class MapRegisterServicesHandlerTests
{
    [Fact]
    public void BoardSyncNamedClient_UsesTheNoRedirectPrimaryHandler()
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var pipeline = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(BoardSyncHttpClient.HttpClientName);
        // The factory wraps the primary handler in its lifetime and logging handlers; walk to the primary.
        HttpMessageHandler handler = pipeline;
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
            handler = delegating.InnerHandler;

        var primary = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
    }

    [Theory]
    [InlineData("https://configured.example/")]
    [InlineData("http://127.0.0.1:8123/")]
    public async Task AccountLinkRegistration_UsesNestedFrontendUrlInsteadOfTopLevelValueOrProductionFallback(string endpoint)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["VibeRails:FrontendUrl"] = endpoint,
                ["FrontendUrl"] = "https://incorrect.example/"
            }).Build());
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        // Exercise the production service factory without reading real account settings or
        // sending traffic. Alternate origins here are transport fixtures, not UI destinations.
        services.AddSingleton<IRemoteAccountKeyStore, FixtureAccountKeyStore>();
        var handler = new AccountLinkHandler();
        services.AddHttpClient("remote-account-link").ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var linker = provider.GetRequiredService<RemoteAccountLinkService>();
        var status = await linker.StartAsync();

        Assert.Equal(new Uri(new Uri(endpoint), "/api/v1/device-links"), handler.RequestUri);
        Assert.Equal("unavailable", status.Status);
        Assert.Equal("old_server", status.Error);
    }

    [Fact]
    public void AccountLinkRegistration_MissingNestedFrontendUrlDoesNotFallBackToProductionOrTopLevelValue()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FrontendUrl"] = "https://incorrect.example/" }).Build());
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        services.AddSingleton<IRemoteAccountKeyStore, FixtureAccountKeyStore>();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<RemoteAccountLinkService>());

        Assert.Contains("VibeRails:FrontendUrl is not configured", exception.Message);
    }

    [Fact]
    public void AccountLinkNamedClient_DoesNotFollowRedirectsOrUseCookies()
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider();
        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("remote-account-link");
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
            handler = delegating.InnerHandler;

        var primary = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseCookies);
    }

    private sealed class FixtureAccountKeyStore : IRemoteAccountKeyStore
    {
        public string Read() => "";
        public string ComputerName => "Registration fixture";
        public bool TrySave(string apiKey, string expectedApiKey, string? accountEmail = null) => throw new InvalidOperationException("The fixture must not save credentials.");
    }

    private sealed class AccountLinkHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
