using TokenSaver;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class EnterpriseNetworkProxyTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData(" https://genai.example.test/v1/ ", "https://genai.example.test/v1")]
    [InlineData("http://localhost:8080/openai/v1", "http://localhost:8080/openai/v1")]
    public void Normalize_AllowsOptionalApiBases(string? value, string expected) =>
        Assert.Equal(expected, EnterpriseNetworkProxy.Normalize(value));

    [Theory]
    [InlineData("genai.example.test/v1")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("https://user:secret@example.com/v1")]
    [InlineData("https://example.com/v1?api_key=secret")]
    [InlineData("https://example.com/v1#fragment")]
    [InlineData("https://example.com:0/v1")]
    [InlineData("https://example.com/\nv1")]
    public void Normalize_RejectsInvalidOrCredentialBearingUrls(string value) =>
        Assert.Throws<ArgumentException>(() => EnterpriseNetworkProxy.Normalize(value));

    [Theory]
    [InlineData("https://api.openai.com/v1/responses", "/v1", "responses")]
    [InlineData("https://chatgpt.com/backend-api/codex/responses", "/backend-api/codex", "responses")]
    [InlineData("https://api.anthropic.com/v1/messages/count_tokens", "/v1", "messages/count_tokens")]
    [InlineData("https://api.z.ai/api/paas/v4/chat/completions", "/api/paas/v4", "chat/completions")]
    [InlineData("https://api.x.ai/v1/chat/completions", "/v1", "chat/completions")]
    [InlineData("https://cli-chat-proxy.grok.com/v1/responses", "/v1", "responses")]
    public void BuildTarget_ReplacesProviderBaseWithoutDuplicatingApiPath(string original, string apiPath, string endpoint)
    {
        var target = EnterpriseNetworkProxy.BuildTarget(new Uri(original + "?stream=true"),
            "https://genai.example.test/tenant/v1/", apiPath);
        Assert.Equal("https://genai.example.test/tenant/v1/" + endpoint + "?stream=true", target.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://genai.example.test", "https://genai.example.test/v1/messages")]
    [InlineData("https://genai.example.test/", "https://genai.example.test/v1/messages")]
    [InlineData("https://genai.example.test/v1", "https://genai.example.test/v1/messages")]
    [InlineData("https://gw.example/anthropic", "https://gw.example/anthropic/v1/messages")]
    [InlineData("https://gw.example/anthropic/V1/", "https://gw.example/anthropic/V1/messages")]
    public void ClaudeGatewayWorksWithOrWithoutV1(string gateway, string expected)
    {
        Assert.True(EnterpriseNetworkProxy.TryBuildTarget(new Uri("https://api.anthropic.com/v1/messages"),
            gateway, "/v1", out var target, addMissingApiPath: true));
        Assert.Equal(expected, target.AbsoluteUri);
    }

    [Fact]
    public void OpenAiStyleBasesAreUsedAsEntered()
    {
        // Matches OPENAI_BASE_URL: a base without /v1 is not second-guessed at request time.
        Assert.True(EnterpriseNetworkProxy.TryBuildTarget(new Uri("https://api.openai.com/v1/responses"),
            "https://gateway.example/openai", "/v1", out var target));
        Assert.Equal("https://gateway.example/openai/responses", target.AbsoluteUri);
    }

    [Fact]
    public void BuildTarget_BlankKeepsExactProviderUrl()
    {
        var original = new Uri("https://api.openai.com/v1/responses?stream=true");
        Assert.Same(original, EnterpriseNetworkProxy.BuildTarget(original, "", "/v1"));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1//attacker.example/request")]
    [InlineData("https://api.openai.com/v10/request")]
    [InlineData("https://api.openai.com/v1/https://attacker.example/request")]
    public void BuildTarget_RequestPathCannotReplaceConfiguredOrigin(string original)
    {
        var target = EnterpriseNetworkProxy.BuildTarget(new Uri(original), "https://gateway.example/v1", "/v1");
        Assert.Equal("gateway.example", target.Host);
        Assert.Equal("https", target.Scheme);
        Assert.StartsWith("/v1/", target.AbsolutePath);
    }
}
