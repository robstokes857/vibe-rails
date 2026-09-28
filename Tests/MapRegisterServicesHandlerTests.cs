using Microsoft.Extensions.DependencyInjection;
using VibeRails;
using VibeRails.Services.Board.Sync;
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
}
