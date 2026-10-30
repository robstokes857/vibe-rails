using TokenSaver;
using VibeRails.Services;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

// Routing is always on, so a base URL the user already gave the CLI (a corporate gateway) must
// not be silently replaced by the public provider default.
public partial class CommandServiceTests
{
    [Fact]
    public void LoopbackBypassesCorporateHttpProxyWithoutDroppingExistingExclusions()
    {
        Assert.Equal(".company.test,localhost,internal.test,127.0.0.1,::1,[::1]",
            CommandService.MergeLoopbackNoProxy(".company.test,localhost", "internal.test,LOCALHOST", null));
    }

    [Fact]
    public async Task PrepareSession_Codex_RoutesInheritedGatewayThroughTokenSaver()
    {
        Environment.SetEnvironmentVariable("OPENAI_BASE_URL", "https://genai.example.test/v1");

        var prepared = await CreateService(codexLlmProxyEnabled: true)
            .PrepareSessionAsync(LLM.Codex, envName: null, extraArgs: null);

        Assert.Contains(LlmProxyCodexConfig.OpenAiProviderName, prepared.LaunchCommand);
        Assert.True(prepared.Environment.ContainsKey(LocalLlmProxyContext.SessionTokenVariable));
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
    public async Task PrepareSession_Claude_RoutesInheritedGatewayThroughTokenSaver()
    {
        Environment.SetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable, "https://genai.example.test/anthropic");

        var prepared = await CreateService(claudeLlmProxyEnabled: true)
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.StartsWith("http://127.0.0.1:4321/", prepared.Environment[LlmProxyClaudeConfig.BaseUrlVariable]);
        Assert.True(prepared.Environment.ContainsKey(LlmProxyClaudeConfig.CustomHeadersVariable));
        Assert.False(prepared.Environment.ContainsKey(LlmProxyClaudeConfig.ToolSearchVariable));
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
        // A translating gateway may reject tool_reference blocks; Claude keeps its own default.
        Assert.False(prepared.Environment.ContainsKey(LlmProxyClaudeConfig.ToolSearchVariable));
    }

    [Fact]
    public async Task PrepareSession_Claude_KeepsToolSearchWithoutAGateway()
    {
        var prepared = await CreateService(claudeLlmProxyEnabled: true)
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.Equal("true", prepared.Environment[LlmProxyClaudeConfig.ToolSearchVariable]);
    }

    [Fact]
    public async Task PrepareSession_Claude_KeepsToolSearchBehindAGatewayMarkedAsSupportingIt()
    {
        var prepared = await CreateService(claudeLlmProxyEnabled: true,
                claudeEnterpriseNetworkProxy: "https://gateway.example/v1", claudeGatewayBearerToken: "secret",
                claudeGatewaySupportsToolSearch: true)
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.Equal("true", prepared.Environment[LlmProxyClaudeConfig.ToolSearchVariable]);
    }

    [Fact]
    public async Task PrepareSession_Claude_InheritedGatewayFollowsTheToolSearchSwitch()
    {
        Environment.SetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable, "https://genai.example.test/anthropic");

        var prepared = await CreateService(claudeLlmProxyEnabled: true, claudeGatewaySupportsToolSearch: true)
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.Equal("true", prepared.Environment[LlmProxyClaudeConfig.ToolSearchVariable]);
    }

    [Theory]
    [InlineData("https://api.anthropic.com")]
    [InlineData("https://API.anthropic.com/v1")]
    public async Task PrepareSession_Claude_AnthropicApiKeyChoiceKeepsToolSearchWithoutTheSwitch(string gateway)
    {
        // Settings' "Anthropic API key" saves the first-party API as the gateway: nothing translates.
        var prepared = await CreateService(claudeLlmProxyEnabled: true,
                claudeEnterpriseNetworkProxy: gateway, claudeGatewayBearerToken: "sk-ant-secret")
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.Equal("true", prepared.Environment[LlmProxyClaudeConfig.ToolSearchVariable]);
        Assert.Equal(LlmProxyCodexConfig.GatewayAuthPlaceholder, prepared.Environment["ANTHROPIC_AUTH_TOKEN"]);
        Assert.DoesNotContain("sk-ant-secret", prepared.Environment.Values);
    }

    [Fact]
    public async Task GatewayBearerLaunchesDoNotNeedProviderLoginOrExposeRealTokens()
    {
        var codex = await CreateService(codexLlmProxyEnabled: true,
            codexEnterpriseNetworkProxy: "https://gateway.example/v1", codexGatewayBearerToken: "real-secret")
            .PrepareSessionAsync(LLM.Codex, envName: null, extraArgs: null);
        Assert.Contains("requires_openai_auth=false", codex.LaunchCommand);
        Assert.Contains("env_key=", codex.LaunchCommand);
        Assert.Equal(LlmProxyCodexConfig.GatewayAuthPlaceholder, codex.Environment[LlmProxyCodexConfig.GatewayAuthVariable]);
        Assert.DoesNotContain("real-secret", codex.LaunchCommand);
        Assert.DoesNotContain("real-secret", codex.Environment.Values);

        var claude = await CreateService(claudeLlmProxyEnabled: true,
            claudeEnterpriseNetworkProxy: "https://gateway.example/v1", claudeGatewayBearerToken: "real-secret")
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);
        Assert.Equal(LlmProxyCodexConfig.GatewayAuthPlaceholder, claude.Environment["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("", claude.Environment["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("real-secret", claude.LaunchCommand);
        Assert.DoesNotContain("real-secret", claude.Environment.Values);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5001/llm/anthropic")]
    [InlineData("http://localhost:5001/llm/anthropic/")]
    public async Task PrepareSession_Claude_InheritedRelayWithoutUpstreamMetadataFailsClosed(string enclosingRelay)
    {
        // A root started from inside a managed Claude tab inherits that tab's relay URL.
        Environment.SetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable, enclosingRelay);

        var prepared = await CreateService(claudeLlmProxyEnabled: true)
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.StartsWith("http://127.0.0.1:4321/", prepared.Environment[LlmProxyClaudeConfig.BaseUrlVariable]);
        Assert.Equal(LlmProxyUpstream.Unavailable, prepared.Environment[LlmProxyUpstream.ClaudeVariable]);
    }

    [Fact]
    public async Task NestedClaudeLaunchesPreserveTheirOriginalCorporateDestination()
    {
        const string gateway = "https://corporate.example/anthropic";
        Environment.SetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable, gateway);
        for (var depth = 0; depth < 3; depth++)
        {
            var prepared = await CreateService(claudeLlmProxyEnabled: true)
                .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);
            Assert.Equal(gateway, prepared.Environment[LlmProxyUpstream.ClaudeVariable]);
            Assert.Equal("http://127.0.0.1:4321/llm/anthropic", prepared.Environment[LlmProxyClaudeConfig.BaseUrlVariable]);
            Environment.SetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable, prepared.Environment[LlmProxyClaudeConfig.BaseUrlVariable]);
            Environment.SetEnvironmentVariable(LlmProxyUpstream.ClaudeVariable, prepared.Environment[LlmProxyUpstream.ClaudeVariable]);
        }
    }

    [Fact]
    public async Task NestedClaudeDefaultLaunchExplicitlyPreservesThePublicProviderChoice()
    {
        var prepared = await CreateService(claudeLlmProxyEnabled: true)
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);
        Assert.Equal(LlmProxyUpstream.ProviderDefault, prepared.Environment[LlmProxyUpstream.ClaudeVariable]);
        Environment.SetEnvironmentVariable(LlmProxyClaudeConfig.BaseUrlVariable, prepared.Environment[LlmProxyClaudeConfig.BaseUrlVariable]);
        Environment.SetEnvironmentVariable(LlmProxyUpstream.ClaudeVariable, prepared.Environment[LlmProxyUpstream.ClaudeVariable]);
        var nested = await CreateService(claudeLlmProxyEnabled: true)
            .PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);
        Assert.Equal(LlmProxyUpstream.ProviderDefault, nested.Environment[LlmProxyUpstream.ClaudeVariable]);
        Assert.Equal("true", nested.Environment[LlmProxyClaudeConfig.ToolSearchVariable]);
    }
}
