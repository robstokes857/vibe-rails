using TokenSaver;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class LlmProxyUpstreamTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://user:secret@corporate.example/v1")]
    [InlineData("http://localhost:1000/llm/anthropic")]
    [InlineData("not a URL")]
    public void ParentRelayWithoutValidCapturedDestinationNeverSelectsThePublicProvider(string? captured)
    {
        Assert.Equal(LlmProxyUpstream.Unavailable,
            LlmProxyUpstream.Resolve("", "http://localhost:2000/llm/anthropic", captured));
    }

    [Theory]
    [InlineData("https://user:secret@corporate.example/v1")]
    [InlineData("http://localhost:1000/llm/anthropic")]
    [InlineData("invalid")]
    public void CaptureNeverPropagatesInvalidDestinationsOrUrlCredentials(string value) =>
        Assert.Equal(LlmProxyUpstream.Unavailable, LlmProxyUpstream.Capture(value));

    [Fact]
    public void ADefaultMarkerIsExplicitAndStaleMetadataCannotOverrideADirectDestination()
    {
        Assert.Equal(LlmProxyUpstream.ProviderDefault, LlmProxyUpstream.Capture(""));
        Assert.Equal("", LlmProxyUpstream.Resolve("", "http://localhost:2000/llm/anthropic", LlmProxyUpstream.ProviderDefault));
        Assert.Equal("https://new.example/v1", LlmProxyUpstream.Resolve("", "https://new.example/v1", "https://old.example/v1"));
        Assert.Equal("https://saved.example/v1", LlmProxyUpstream.Resolve("https://saved.example/v1", "http://localhost:2000/llm/anthropic", null));
    }
}
