using System.Text.Json;
using TokenSaver;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class LlmProxyZaiConfigTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4321", "http://127.0.0.1:4321/llm/zai/api/paas/v4")]
    [InlineData("http://127.0.0.1:4321/", "http://127.0.0.1:4321/llm/zai/api/paas/v4")]
    [InlineData("http://127.0.0.1:4321///", "http://127.0.0.1:4321/llm/zai/api/paas/v4")]
    public void BuildZaiBaseUrl_NormalizesTrailingSlashes(string input, string expected)
    {
        Assert.Equal(expected, LlmProxyZaiConfig.BuildZaiBaseUrl(input));
    }

    [Fact]
    public void BuildOpencodeConfigContent_EmitsZaiAndXaiProxyOptionsAndEscapesTokens()
    {
        var json = LlmProxyZaiConfig.BuildOpencodeConfigContent(
            "http://127.0.0.1:4321/",
            "session-\"quoted",
            "tab\\slash");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Single(root.EnumerateObject());
        var provider = root.GetProperty("provider");
        Assert.Equal(2, provider.EnumerateObject().Count());

        var zai = provider.GetProperty("zai").GetProperty("options");
        Assert.Equal(
            "http://127.0.0.1:4321/llm/zai/api/paas/v4",
            zai.GetProperty("baseURL").GetString());
        var zaiHeaders = zai.GetProperty("headers");
        Assert.Equal("session-\"quoted", zaiHeaders.GetProperty("viberails_session").GetString());
        Assert.Equal("tab\\slash", zaiHeaders.GetProperty("viberails_tab").GetString());

        var xai = provider.GetProperty("xai").GetProperty("options");
        Assert.Equal(
            "http://127.0.0.1:4321/llm/xai/v1",
            xai.GetProperty("baseURL").GetString());
        var xaiHeaders = xai.GetProperty("headers");
        Assert.Equal("session-\"quoted", xaiHeaders.GetProperty("viberails_session").GetString());
        Assert.Equal("tab\\slash", xaiHeaders.GetProperty("viberails_tab").GetString());
    }
}
