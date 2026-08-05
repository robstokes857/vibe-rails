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
    public void BuildOpencodeConfigContent_EmitsOnlyZaiProxyOptionsAndEscapesTokens()
    {
        var json = LlmProxyZaiConfig.BuildOpencodeConfigContent(
            "http://127.0.0.1:4321/",
            "session-\"quoted",
            "tab\\slash");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Single(root.EnumerateObject());
        var zai = root.GetProperty("provider").GetProperty("zai");
        var options = zai.GetProperty("options");
        Assert.Equal(
            "http://127.0.0.1:4321/llm/zai/api/paas/v4",
            options.GetProperty("baseURL").GetString());
        var headers = options.GetProperty("headers");
        Assert.Equal("session-\"quoted", headers.GetProperty("viberails_session").GetString());
        Assert.Equal("tab\\slash", headers.GetProperty("viberails_tab").GetString());
    }
}
