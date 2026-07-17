using Moq;
using VibeRails.Interfaces;
using VibeRails.Services;
using TokenSaver;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.LlmProxy;

/// <summary>
/// Pins the Claude Code side of the local LLM proxy. Claude routes its Anthropic API traffic
/// through us via ANTHROPIC_BASE_URL (proxy endpoint) + ANTHROPIC_CUSTOM_HEADERS (the session/tab
/// tokens the proxy validates). Two invariants matter most:
///  - the base URL must NOT include "/v1" — Claude Code appends "/v1/messages" itself, so a base
///    ending in "/v1" would double it to "/v1/v1/messages";
///  - the custom-headers value is newline-separated "Name: Value" pairs with the exact header
///    names the proxy auth-gate checks.
/// See VibeRails/Routes/LlmAnthropicProxyRoutes.cs and TokenSaver/README.md.
/// </summary>
// Shares the process-global VIBERAILS_TEST_FAKE_CLI env var with CommandServiceTests; the shared
// collection serializes the two classes so their ctor/Dispose set+restore of the flag can't race.
[Collection("ProcessEnvIsolation")]
public class LlmProxyClaudeConfigTests : IDisposable
{
    private readonly string? _originalFakeCliFlag;

    public LlmProxyClaudeConfigTests()
    {
        // Isolate from any caller-set fake-CLI override that would short-circuit PrepareSession
        // before the LLM-specific env injection runs.
        _originalFakeCliFlag = Environment.GetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI");
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", _originalFakeCliFlag);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4321", "http://127.0.0.1:4321/llm/anthropic")]
    [InlineData("http://127.0.0.1:4321/", "http://127.0.0.1:4321/llm/anthropic")]
    [InlineData("http://127.0.0.1:4321///", "http://127.0.0.1:4321/llm/anthropic")]
    [InlineData("", "http://127.0.0.1:0/llm/anthropic")]
    [InlineData("   ", "http://127.0.0.1:0/llm/anthropic")]
    public void BuildAnthropicBaseUrl_NormalizesAndAppendsProxyPath(string apiBaseUrl, string expected)
    {
        Assert.Equal(expected, LlmProxyClaudeConfig.BuildAnthropicBaseUrl(apiBaseUrl));
    }

    [Fact]
    public void BuildAnthropicBaseUrl_DoesNotIncludeV1()
    {
        // Claude Code appends "/v1/messages" to the base URL. If the base already ended in "/v1",
        // requests would hit "/v1/v1/messages" and 404. Guard the invariant explicitly.
        var baseUrl = LlmProxyClaudeConfig.BuildAnthropicBaseUrl("http://127.0.0.1:4321");

        Assert.EndsWith("/llm/anthropic", baseUrl);
        Assert.DoesNotContain("/v1", baseUrl);
    }

    [Fact]
    public void BuildCustomHeaders_IsNewlineSeparatedNameValuePairs()
    {
        var headers = LlmProxyClaudeConfig.BuildCustomHeaders("session-abc", "tab-xyz");

        Assert.Equal("viberails_session: session-abc\nviberails_tab: tab-xyz", headers);
    }

    [Fact]
    public void BuildCustomHeaders_UsesTheHeaderNamesTheProxyValidates()
    {
        // The proxy auth-gate (LlmAnthropicProxyRoutes.IsProxyHeaderAuthenticated) reads these
        // exact header names, shared with the Codex path. Keep them in lockstep.
        Assert.Equal(LlmProxyCodexConfig.SessionHeaderName, LlmProxyClaudeConfig.SessionHeaderName);
        Assert.Equal(LlmProxyCodexConfig.TabHeaderName, LlmProxyClaudeConfig.TabHeaderName);

        var headers = LlmProxyClaudeConfig.BuildCustomHeaders("s", "t");
        Assert.Contains($"{LlmProxyClaudeConfig.SessionHeaderName}: s", headers);
        Assert.Contains($"{LlmProxyClaudeConfig.TabHeaderName}: t", headers);
    }

    [Fact]
    public void BuildClaudeProxyEnvironment_SetsBaseUrlAndCustomHeaders()
    {
        var env = LlmProxyClaudeConfig.BuildClaudeProxyEnvironment(
            "http://127.0.0.1:4321", "session-abc", "tab-xyz");

        Assert.Equal("http://127.0.0.1:4321/llm/anthropic", env[LlmProxyClaudeConfig.BaseUrlVariable]);
        Assert.Equal("viberails_session: session-abc\nviberails_tab: tab-xyz", env[LlmProxyClaudeConfig.CustomHeadersVariable]);
    }

    [Fact]
    public async Task PrepareSession_Claude_RoutesThroughAnthropicProxy()
    {
        var service = CreateService(claudeLlmProxyEnabled: true);

        var prepared = await service.PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.True(
            prepared.Environment.TryGetValue("ANTHROPIC_BASE_URL", out var baseUrl),
            "Expected ANTHROPIC_BASE_URL to point Claude at the local proxy.");
        Assert.Equal("http://127.0.0.1:4321/llm/anthropic", baseUrl);

        Assert.True(
            prepared.Environment.TryGetValue("ANTHROPIC_CUSTOM_HEADERS", out var customHeaders),
            "Expected ANTHROPIC_CUSTOM_HEADERS to carry the proxy session/tab tokens.");
        Assert.Equal("viberails_session: test-session-token\nviberails_tab: test-tab-token", customHeaders);

        // The launch command itself stays clean — the proxy is env-based, tokens never hit argv.
        Assert.StartsWith("claude", prepared.LaunchCommand);
        Assert.DoesNotContain("ANTHROPIC_BASE_URL", prepared.LaunchCommand);
        Assert.DoesNotContain("test-session-token", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Claude_DoesNotRouteThroughAnthropicProxyWhenClaudeLlmProxyDisabled()
    {
        var service = CreateService(claudeLlmProxyEnabled: false);

        var prepared = await service.PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.False(prepared.Environment.ContainsKey("ANTHROPIC_BASE_URL"));
        Assert.False(prepared.Environment.ContainsKey("ANTHROPIC_CUSTOM_HEADERS"));
    }

    [Fact]
    public async Task PrepareSession_Claude_DoesNotRouteThroughAnthropicProxyWhenOnlyCodexLlmProxyEnabled()
    {
        var service = CreateService(codexLlmProxyEnabled: true, claudeLlmProxyEnabled: false);

        var prepared = await service.PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.False(prepared.Environment.ContainsKey("ANTHROPIC_BASE_URL"));
        Assert.False(prepared.Environment.ContainsKey("ANTHROPIC_CUSTOM_HEADERS"));
    }

    [Theory]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Antigravity)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.OpenCode)]
    public async Task PrepareSession_NonClaude_DoesNotSetAnthropicProxyEnv(LLM llm)
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(llm, envName: null, extraArgs: null);

        Assert.False(
            prepared.Environment.ContainsKey("ANTHROPIC_BASE_URL"),
            $"ANTHROPIC_BASE_URL must only be set for LLM.Claude, not {llm}.");
        Assert.False(
            prepared.Environment.ContainsKey("ANTHROPIC_CUSTOM_HEADERS"),
            $"ANTHROPIC_CUSTOM_HEADERS must only be set for LLM.Claude, not {llm}.");
    }

    private static CommandService CreateService(bool codexLlmProxyEnabled = false, bool claudeLlmProxyEnabled = false)
    {
        var fileService = new Mock<IFileService>().Object;
        var envService = new LlmCliEnvironmentService(
            new ClaudeLlmCliEnvironment(fileService),
            new CodexLlmCliEnvironment(fileService),
            new AntigravityLlmCliEnvironment(fileService),
            new CopilotLlmCliEnvironment(fileService),
            new OpencodeLlmCliEnvironment(fileService),
            fileService);
        var proxyContext = new Mock<ILocalLlmProxyContext>();
        proxyContext.Setup(x => x.ApiBaseUrl).Returns("http://127.0.0.1:4321");
        proxyContext.Setup(x => x.SessionToken).Returns("test-session-token");
        proxyContext.Setup(x => x.TabToken).Returns("test-tab-token");
        var proxySettings = new Mock<ILlmProxySettingsService>();
        proxySettings.Setup(x => x.GetSettings())
            .Returns(new LlmProxySettings(codexLlmProxyEnabled, CodexLlmProxySettings.ModeSubscription, claudeLlmProxyEnabled));
        return new CommandService(envService, proxyContext.Object, proxySettings.Object);
    }
}
