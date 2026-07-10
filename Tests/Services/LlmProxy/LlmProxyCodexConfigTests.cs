using VibeRails.Services.AgentTools;
using VibeRails.Services.LlmProxy;
using Xunit;

namespace Tests.Services.LlmProxy;

public class LlmProxyCodexConfigTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4321", "http://127.0.0.1:4321/llm/openai/v1")]
    [InlineData("http://127.0.0.1:4321///", "http://127.0.0.1:4321/llm/openai/v1")]
    [InlineData("", "http://127.0.0.1:0/llm/openai/v1")]
    public void BuildOpenAiBaseUrl_UsesOpenAiApiPath(string apiBaseUrl, string expected)
    {
        Assert.Equal(expected, LlmProxyCodexConfig.BuildOpenAiBaseUrl(apiBaseUrl));
    }

    [Theory]
    [InlineData("http://127.0.0.1:4321", "http://127.0.0.1:4321/llm/openai/backend-api/codex")]
    [InlineData("http://127.0.0.1:4321///", "http://127.0.0.1:4321/llm/openai/backend-api/codex")]
    [InlineData("", "http://127.0.0.1:0/llm/openai/backend-api/codex")]
    public void BuildChatGptBaseUrl_UsesCodexModelApiPath(string apiBaseUrl, string expected)
    {
        Assert.Equal(expected, LlmProxyCodexConfig.BuildChatGptBaseUrl(apiBaseUrl));
    }

    [Theory]
    [InlineData(CodexLlmProxySettings.ModeSubscription, "/backend-api/codex")]
    [InlineData(CodexLlmProxySettings.ModeApi, "/v1")]
    public void BuildCodexProxyArgs_ConfiguresOnlyTheModelProvider(string mode, string expectedPath)
    {
        var args = LlmProxyCodexConfig.BuildCodexProxyArgs("http://127.0.0.1:4321", mode);
        var joined = string.Join(' ', args);

        Assert.Contains($"model_provider=\"{LlmProxyCodexConfig.OpenAiProviderName}\"", joined);
        Assert.Contains($"/llm/openai{expectedPath}", joined);
        Assert.Contains("requires_openai_auth=true", joined);
        Assert.Contains(LocalToolApiContext.SessionTokenVariable, joined);
        Assert.Contains(LocalToolApiContext.TabTokenVariable, joined);
        Assert.DoesNotContain("chatgpt_base_url", joined);
    }

    [Theory]
    [InlineData("/llm/openai", true)]
    [InlineData("/LLM/OPENAI/v1/responses", true)]
    [InlineData("/llm/openaiish/v1/responses", false)]
    [InlineData("/.well-known/oauth-protected-resource/llm/openai/v1/responses", false)]
    public void IsOpenAiProxyPath_MatchesOnlyTheProxyTree(string path, bool expected)
    {
        Assert.Equal(expected, LlmProxyCodexConfig.IsOpenAiProxyPath(path));
    }
}
