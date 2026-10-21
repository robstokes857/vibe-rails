using Microsoft.Extensions.DependencyInjection;
using VibeRails;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class LlmProxyClientRegistrationTests
{
    [Fact]
    public void ProductionRelayClientNeverFollowsProviderRedirects()
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, [], "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("llm-proxy");
        while (handler is DelegatingHandler wrapper) handler = wrapper.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }
}
