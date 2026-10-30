using System.Net;
using VibeRails;
using Xunit;

namespace Tests.Services.LlmProxy;

/// <summary>
/// The relay's outbound transport. A corporate proxy that needs Windows sign-in answers 407 to
/// every relayed request unless the handler offers the signed-in account; see API_SEC.md
/// (Enterprise Network Proxy) for why that is limited to this one client;
/// OutboundHttpRegistrationTests pins which registered clients get it.
/// </summary>
public sealed class LlmProxyHttpHandlerTests
{
    [Fact]
    public void RelayTransportSignsInOnlyToTheDefaultProxyAndFollowsNoRedirects()
    {
        using var handler = MapRegisterServices.CreateLlmProxyHttpMessageHandler();
        var http = Assert.IsType<HttpClientHandler>(handler);

        Assert.False(http.AllowAutoRedirect);
        Assert.False(http.UseCookies);
        // DefaultProxyCredentials apply only to the default (system or environment) proxy.
        Assert.True(http.UseProxy);
        Assert.Null(http.Proxy);
        Assert.Same(CredentialCache.DefaultCredentials, http.DefaultProxyCredentials);
        // The gateway itself never receives Windows credentials: it gets the saved token or nothing.
        Assert.False(http.UseDefaultCredentials);
        Assert.Null(http.Credentials);
    }

    [Fact]
    public void SharedApiKeyTransportOffersNoCredentialsToAnyProxy()
    {
        using var handler = MapRegisterServices.CreateNoRedirectHttpMessageHandler();
        var http = Assert.IsType<HttpClientHandler>(handler);

        Assert.False(http.AllowAutoRedirect);
        Assert.Null(http.DefaultProxyCredentials);
        Assert.False(http.UseDefaultCredentials);
    }
}
