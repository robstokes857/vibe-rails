using TokenSaver;
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
        // Pass the process-local proxy env-var names (as CommandService does) so this test pins
        // the header→env-var contract without coupling Codex proxy auth to the inherited root
        // agent-tool credentials.
        var args = LlmProxyCodexConfig.BuildCodexProxyArgs(
            "http://127.0.0.1:4321",
            mode,
            LocalLlmProxyContext.SessionTokenVariable,
            LocalLlmProxyContext.TabTokenVariable);
        var joined = string.Join(' ', args);

        Assert.Contains($"model_provider=\"{LlmProxyCodexConfig.OpenAiProviderName}\"", joined);
        Assert.Contains($"/llm/openai{expectedPath}", joined);
        Assert.Contains("requires_openai_auth=true", joined);
        Assert.Contains(LocalLlmProxyContext.SessionTokenVariable, joined);
        Assert.Contains(LocalLlmProxyContext.TabTokenVariable, joined);
        Assert.DoesNotContain("chatgpt_base_url", joined);
    }
}
