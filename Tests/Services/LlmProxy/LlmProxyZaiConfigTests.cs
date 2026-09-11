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
            "http://127.0.0.1:4321/llm/zai/api/paas/v4",
            zai.GetProperty("baseURL").GetString());
        var xaiHeaders = xai.GetProperty("headers");
        Assert.Equal("session-\"quoted", xaiHeaders.GetProperty("viberails_session").GetString());
        Assert.Equal("tab\\slash", xaiHeaders.GetProperty("viberails_tab").GetString());
    }

    [Fact]
    public void BuildOpencodeConfigContent_IncludesTerminalSessionHeaderWhenProvided()
    {
        var json = LlmProxyZaiConfig.BuildOpencodeConfigContent(
            "http://127.0.0.1:4321/",
            "session-abc",
            "tab-xyz",
            " sess-42 ");

        using var document = JsonDocument.Parse(json);
        var provider = document.RootElement.GetProperty("provider");
        foreach (var name in new[] { "zai", "xai" })
        {
            var headers = provider.GetProperty(name).GetProperty("options").GetProperty("headers");
            Assert.Equal("sess-42", headers.GetProperty("viberails_terminal_session").GetString());
        }
    }

    [Fact]
    public void BuildOpencodeConfigContent_OmitsTerminalSessionHeaderWhenUnknown()
    {
        var json = LlmProxyZaiConfig.BuildOpencodeConfigContent(
            "http://127.0.0.1:4321/",
            "session-abc",
            "tab-xyz");

        using var document = JsonDocument.Parse(json);
        var provider = document.RootElement.GetProperty("provider");
        foreach (var name in new[] { "zai", "xai" })
        {
            var headers = provider.GetProperty(name).GetProperty("options").GetProperty("headers");
            Assert.Equal(2, headers.EnumerateObject().Count());
        }
    }
}
