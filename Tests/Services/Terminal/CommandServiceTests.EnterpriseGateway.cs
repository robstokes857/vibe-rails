using TokenSaver;
using VibeRails.Services;
using VibeRails.Services.LlmProxy;
using Xunit;

namespace Tests.Services.Terminal;

// Routing is always on, so a base URL the user already gave the CLI (a corporate gateway) must
// not be silently replaced by the public provider default.
public partial class CommandServiceTests
{
    [Fact]
    public async Task PrepareSession_Codex_KeepsInheritedOpenAiBaseUrlWithoutGateway()
    {
        Environment.SetEnvironmentVariable("OPENAI_BASE_URL", "https://genai.example.test/v1");

        var prepared = await CreateService(codexLlmProxyEnabled: true)
            .PrepareSessionAsync(LLM.Codex, envName: null, extraArgs: null);

        Assert.DoesNotContain(LlmProxyCodexConfig.OpenAiProviderName, prepared.LaunchCommand);
        Assert.False(prepared.Environment.ContainsKey(LocalLlmProxyContext.SessionTokenVariable));
    }

    [Fact]
    public async Task PrepareSession_Codex_ConfiguredGatewayRoutesDespiteInheritedOpenAiBaseUrl()
    {
        Environment.SetEnvironmentVariable("OPENAI_BASE_URL", "https://genai.example.test/v1");

        var prepared = await CreateService(
                codexLlmProxyEnabled: true,
                codexEnterpriseNetworkProxy: "https://genai.example.test/v1")
            .PrepareSessionAsync(LLM.Codex, envName: null, extraArgs: null);

        Assert.Contains(LlmProxyCodexConfig.OpenAiProviderName, prepared.LaunchCommand);
        Assert.Contains("http://127.0.0.1:4321/llm/openai/", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Claude_KeepsInheritedAnthropicBaseUrlWithoutGateway()
    {
        Environment.SetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable, "https://genai.example.test/anthropic");

        var prepared = await CreateService(claudeLlmProxyEnabled: true)
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.False(prepared.Environment.ContainsKey(LlmProxyClaudeConfig.BaseUrlVariable));
        Assert.False(prepared.Environment.ContainsKey(LlmProxyClaudeConfig.CustomHeadersVariable));
    }

    [Fact]
    public async Task PrepareSession_Claude_ConfiguredGatewayRoutesDespiteInheritedAnthropicBaseUrl()
    {
        Environment.SetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable, "https://genai.example.test/anthropic");

        var prepared = await CreateService(
                claudeLlmProxyEnabled: true,
                claudeEnterpriseNetworkProxy: "https://genai.example.test/anthropic/v1")
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.StartsWith("http://127.0.0.1:4321/", prepared.Environment[LlmProxyClaudeConfig.BaseUrlVariable]);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5001/llm/anthropic")]
    [InlineData("http://localhost:5001/llm/anthropic/")]
    public async Task PrepareSession_Claude_InheritedVibeRailsRelayDoesNotDisableRouting(string enclosingRelay)
    {
        // A root started from inside a managed Claude tab inherits that tab's relay URL.
        Environment.SetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable, enclosingRelay);

        var prepared = await CreateService(claudeLlmProxyEnabled: true)
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.StartsWith("http://127.0.0.1:4321/", prepared.Environment[LlmProxyClaudeConfig.BaseUrlVariable]);
    }
}
