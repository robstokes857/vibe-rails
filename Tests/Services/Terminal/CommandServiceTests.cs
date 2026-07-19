using Moq;
using VibeRails.Interfaces;
using VibeRails.Services;
using TokenSaver;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Pins the env-var contract for Claude Code's DEC 2026 synchronized output.
/// claude-code 2.1.110+ gates BSU/ESU emission on a hardcoded TERM allowlist
/// our ConPTY child doesn't land on, so CLAUDE_CODE_FORCE_SYNC_OUTPUT=1 is the
/// only reliable way to make it bracket its post-resize redraws. xterm.js v6
/// in the browser then commits one atomic frame per BSU/ESU pair and the
/// resize-reprint flash disappears. See runbooks/terminal/TERMINAL.md
/// resize-reprint entry + anthropics/claude-code#49584, #55613.
///
/// Also pins the MCP behavior: VibeRails registers its stdio MCP server for every
/// managed agent CLI launch. Remove-first repairs stale registrations and add
/// restores the current server command.
/// </summary>
// Shares the process-global VIBERAILS_TEST_FAKE_CLI env var with LlmProxyClaudeConfigTests;
// the shared collection serializes the two classes so one can't clear/restore the flag while the
// other is mid-test (which would flake PrepareSession into the echo+sleep fake).
[Collection("ProcessEnvIsolation")]
public class CommandServiceTests : IDisposable
{
    private readonly string? _originalFakeCliFlag;
    private readonly string? _originalOpenCodeConfig;

    public CommandServiceTests()
    {
        // Isolate from any caller-set fake-CLI override that would short-circuit
        // PrepareSession before the LLM-specific env injection runs.
        _originalFakeCliFlag = Environment.GetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI");
        _originalOpenCodeConfig = Environment.GetEnvironmentVariable(
            LlmProxyZaiConfig.ConfigContentVariable);
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", null);
        Environment.SetEnvironmentVariable(LlmProxyZaiConfig.ConfigContentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", _originalFakeCliFlag);
        Environment.SetEnvironmentVariable(
            LlmProxyZaiConfig.ConfigContentVariable,
            _originalOpenCodeConfig);
    }

    [Fact]
    public async Task PrepareSession_Claude_SetsForceSyncOutputEnvVar()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);

        Assert.True(
            prepared.Environment.TryGetValue("CLAUDE_CODE_FORCE_SYNC_OUTPUT", out var value),
            "Expected CLAUDE_CODE_FORCE_SYNC_OUTPUT to be set for LLM.Claude.");
        Assert.Equal("1", value);
    }

    [Theory]
    [InlineData(LLM.Claude, "claude mcp remove viberails-mcp", "claude mcp add --scope user viberails-mcp -- ")]
    [InlineData(LLM.Codex, "codex mcp remove viberails-mcp", "codex mcp add viberails-mcp -- ")]
    [InlineData(LLM.Antigravity, "agy mcp remove viberails-mcp", "agy mcp add viberails-mcp -- ")]
    [InlineData(LLM.Copilot, "copilot mcp remove viberails-mcp", "copilot mcp add viberails-mcp -- ")]
    public async Task PrepareSession_SupportedClis_AddsVibeRailsMcpSetupCommand(
        LLM llm,
        string expectedRemoveCommand,
        string expectedAddPrefix)
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(llm, envName: null, extraArgs: null);

        Assert.True(prepared.SetupCommands.Count >= 2);
        var remove = prepared.SetupCommands[0];
        Assert.StartsWith(expectedRemoveCommand, remove);
        Assert.EndsWith(ExpectedQuietRedirect(), remove);

        var add = prepared.SetupCommands[1];
        Assert.StartsWith(expectedAddPrefix, add);
        Assert.Contains(" mcp", add);

        Assert.StartsWith(prepared.SetupCommands[0] + "; ", prepared.Command);
        Assert.Contains(expectedAddPrefix, prepared.Command);
        Assert.EndsWith(prepared.LaunchCommand, prepared.Command);
    }

    [Fact]
    public async Task PrepareSession_Codex_SubscriptionModeAddsVibeRailsChatGptProvider()
    {
        var service = CreateService(
            codexLlmProxyEnabled: true,
            codexLlmProxyMode: CodexLlmProxySettings.ModeSubscription);

        var prepared = await service.PrepareSessionAsync(LLM.Codex, envName: null, extraArgs: null);

        Assert.StartsWith("codex ", prepared.LaunchCommand);
        Assert.Contains(LlmProxyCodexConfig.OpenAiProviderName, prepared.LaunchCommand);
        Assert.Contains("http://127.0.0.1:4321/llm/openai/backend-api/codex", prepared.LaunchCommand);
        Assert.Contains("requires_openai_auth=true", prepared.LaunchCommand);
        Assert.Contains($"env_http_headers.{LlmProxyCodexConfig.SessionHeaderName}", prepared.LaunchCommand);
        Assert.Contains($"env_http_headers.{LlmProxyCodexConfig.TabHeaderName}", prepared.LaunchCommand);
        Assert.DoesNotContain("chatgpt_base_url", prepared.LaunchCommand);
        Assert.DoesNotContain("test-session-token", prepared.LaunchCommand);
        Assert.DoesNotContain("test-tab-token", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Codex_ApiModeAddsVibeRailsOpenAiProxyProvider()
    {
        var service = CreateService(
            codexLlmProxyEnabled: true,
            codexLlmProxyMode: CodexLlmProxySettings.ModeApi);

        var prepared = await service.PrepareSessionAsync(LLM.Codex, envName: null, extraArgs: null);

        Assert.StartsWith("codex ", prepared.LaunchCommand);
        Assert.Contains(LlmProxyCodexConfig.OpenAiProviderName, prepared.LaunchCommand);
        Assert.Contains("http://127.0.0.1:4321/llm/openai/v1", prepared.LaunchCommand);
        Assert.Contains("requires_openai_auth=true", prepared.LaunchCommand);
        Assert.Contains($"env_http_headers.{LlmProxyCodexConfig.SessionHeaderName}", prepared.LaunchCommand);
        Assert.Contains(LocalLlmProxyContext.SessionTokenVariable, prepared.LaunchCommand);
        Assert.Contains($"env_http_headers.{LlmProxyCodexConfig.TabHeaderName}", prepared.LaunchCommand);
        Assert.Contains(LocalLlmProxyContext.TabTokenVariable, prepared.LaunchCommand);
        Assert.DoesNotContain("test-session-token", prepared.LaunchCommand);
        Assert.DoesNotContain("test-tab-token", prepared.LaunchCommand);
        Assert.Equal(
            "test-session-token",
            prepared.Environment[LocalLlmProxyContext.SessionTokenVariable]);
        Assert.Equal(
            "test-tab-token",
            prepared.Environment[LocalLlmProxyContext.TabTokenVariable]);
    }

    [Fact]
    public async Task PrepareSession_Codex_DoesNotAddProxyProviderWhenCodexLlmProxyDisabled()
    {
        var service = CreateService(codexLlmProxyEnabled: false);

        var prepared = await service.PrepareSessionAsync(LLM.Codex, envName: null, extraArgs: null);

        Assert.Equal("codex", prepared.LaunchCommand);
        Assert.DoesNotContain(LlmProxyCodexConfig.OpenAiProviderName, prepared.Command);
        Assert.DoesNotContain("llm/openai", prepared.Command);
    }

    [Fact]
    public async Task PrepareSession_Codex_SkipsProxyWhenProviderIsExplicit()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(
            LLM.Codex,
            envName: null,
            extraArgs: ["--config", "model_provider=\"other\""]);

        Assert.DoesNotContain(LlmProxyCodexConfig.OpenAiProviderName, prepared.LaunchCommand);
        Assert.Contains("model_provider", prepared.LaunchCommand);
        Assert.Contains("other", prepared.LaunchCommand);
        Assert.False(prepared.Environment.ContainsKey(LocalLlmProxyContext.SessionTokenVariable));
        Assert.False(prepared.Environment.ContainsKey(LocalLlmProxyContext.TabTokenVariable));
    }

    [Theory]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Antigravity)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Glm52)]
    [InlineData(LLM.KimiK3)]
    public async Task PrepareSession_NonClaude_DoesNotSetForceSyncOutputEnvVar(LLM llm)
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(llm, envName: null, extraArgs: null);

        Assert.False(
            prepared.Environment.ContainsKey("CLAUDE_CODE_FORCE_SYNC_OUTPUT"),
            $"CLAUDE_CODE_FORCE_SYNC_OUTPUT must only be set for LLM.Claude, not {llm}.");
    }

    [Fact]
    public async Task PrepareSession_Antigravity_UsesAgyExecutable()
    {
        var service = CreateService();

        // Antigravity's binary is `agy`, not the lowercased enum name ("antigravity").
        // The in-app PTY must launch `agy`, or the session fails to start.
        var prepared = await service.PrepareSessionAsync(LLM.Antigravity, envName: null, extraArgs: null);

        Assert.Equal("agy", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Antigravity_PassesPromptViaPromptInteractiveFlag()
    {
        var service = CreateService();

        // agy has no positional-prompt form; the initial prompt rides on
        // --prompt-interactive=<text> (mirrors Copilot's --interactive=).
        var prepared = await service.PrepareSessionAsync(
            LLM.Antigravity, envName: null, extraArgs: null, initialPrompt: "hello world");

        Assert.StartsWith("agy --prompt-interactive=", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Opencode_UsesOpencodeExecutableAndNoMcpSetup()
    {
        var service = CreateService();

        // OpenCode's binary is `opencode` (== enum name lowercased, so no remap like agy), and
        // it is launch-flag-only with no MCP auto-registration (`opencode mcp add` is
        // interactive), so the launch command is bare `opencode` with no mcp setup prefix.
        var prepared = await service.PrepareSessionAsync(LLM.OpenCode, envName: null, extraArgs: null);

        Assert.Equal("opencode", prepared.LaunchCommand);
        Assert.DoesNotContain("mcp", prepared.Command);
    }

    [Fact]
    public async Task PrepareSession_Opencode_PassesPromptViaPromptFlag()
    {
        var service = CreateService();

        // OpenCode's TUI treats a positional arg as the [project] path, not a prompt, so the
        // initial prompt must ride on --prompt (never the default positional branch).
        var prepared = await service.PrepareSessionAsync(
            LLM.OpenCode, envName: null, extraArgs: null, initialPrompt: "hello world");

        Assert.StartsWith("opencode --prompt=", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Opencode_InjectsZaiProxyConfigAndMarksSessionActive()
    {
        var service = CreateService(openCodeLlmProxyEnabled: true);

        var prepared = await service.PrepareSessionAsync(
            LLM.OpenCode, envName: null, extraArgs: null);

        Assert.True(prepared.OpenCodeProxyActive);
        Assert.Equal(
            "test-session-token",
            prepared.Environment[LocalLlmProxyContext.SessionTokenVariable]);
        Assert.Equal(
            "test-tab-token",
            prepared.Environment[LocalLlmProxyContext.TabTokenVariable]);
        var config = prepared.Environment[LlmProxyZaiConfig.ConfigContentVariable];
        Assert.Contains("http://127.0.0.1:4321/llm/zai/api/paas/v4", config);
        Assert.Contains("test-session-token", config);
        Assert.Contains("test-tab-token", config);
    }

    [Fact]
    public async Task PrepareSession_Opencode_DoesNotInjectProxyWhenDisabled()
    {
        var service = CreateService(openCodeLlmProxyEnabled: false);

        var prepared = await service.PrepareSessionAsync(
            LLM.OpenCode, envName: null, extraArgs: null);

        Assert.False(prepared.OpenCodeProxyActive);
        Assert.False(prepared.Environment.ContainsKey(LlmProxyZaiConfig.ConfigContentVariable));
    }

    [Fact]
    public async Task PrepareSession_Opencode_PreservesInheritedConfigContent()
    {
        const string inherited = "{\"plugin\":[\"caller-plugin\"]}";
        Environment.SetEnvironmentVariable(LlmProxyZaiConfig.ConfigContentVariable, inherited);
        var service = CreateService(openCodeLlmProxyEnabled: true);

        var prepared = await service.PrepareSessionAsync(
            LLM.OpenCode, envName: null, extraArgs: null);

        Assert.False(prepared.OpenCodeProxyActive);
        Assert.False(prepared.Environment.ContainsKey(LlmProxyZaiConfig.ConfigContentVariable));
        Assert.Equal(
            inherited,
            Environment.GetEnvironmentVariable(LlmProxyZaiConfig.ConfigContentVariable));
    }

    [Fact]
    public async Task PrepareSession_Glm52_UsesOpencodeExecutableWithPinnedModel()
    {
        var service = CreateService();

        // GLM 5.2 is a pseudo-CLI backed by OpenCode. The binary is `opencode` (not `glm52`),
        // and base CLI launches inject --model=zai/glm-5.2 so the session picks the right model.
        var prepared = await service.PrepareSessionAsync(LLM.Glm52, envName: null, extraArgs: null);

        Assert.Equal("opencode --model=zai/glm-5.2", prepared.LaunchCommand);
        Assert.DoesNotContain("mcp", prepared.Command);
    }

    [Fact]
    public async Task PrepareSession_KimiK3_UsesOpencodeExecutableWithPinnedModel()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(LLM.KimiK3, envName: null, extraArgs: null);

        Assert.Equal("opencode --model=moonshotai/kimi-k3", prepared.LaunchCommand);
        Assert.DoesNotContain("mcp", prepared.Command);
    }

    [Fact]
    public async Task PrepareSession_Glm52_PassesPromptViaPromptFlag()
    {
        var service = CreateService();

        // GLM 5.2 shares OpenCode's --prompt=<text> convention (a positional arg is the
        // project path in OpenCode's TUI, not a prompt).
        var prepared = await service.PrepareSessionAsync(
            LLM.Glm52, envName: null, extraArgs: null, initialPrompt: "hello world");

        Assert.StartsWith("opencode --model=zai/glm-5.2 --prompt=", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Glm52_InjectsZaiProxyConfig()
    {
        // GLM 5.2 IS the zai provider model, so the Z.AI proxy applies (same as plain OpenCode).
        var service = CreateService(openCodeLlmProxyEnabled: true);

        var prepared = await service.PrepareSessionAsync(LLM.Glm52, envName: null, extraArgs: null);

        Assert.True(prepared.OpenCodeProxyActive);
        Assert.Equal(
            "test-session-token",
            prepared.Environment[LocalLlmProxyContext.SessionTokenVariable]);
        Assert.Equal(
            "test-tab-token",
            prepared.Environment[LocalLlmProxyContext.TabTokenVariable]);
        Assert.True(prepared.Environment.ContainsKey(LlmProxyZaiConfig.ConfigContentVariable));
    }

    [Fact]
    public async Task PrepareSession_KimiK3_DoesNotInjectZaiProxy()
    {
        // Kimi K3 runs through the moonshotai provider, not zai, so the Z.AI proxy config
        // is irrelevant and must NOT be injected even when the proxy is enabled.
        var service = CreateService(openCodeLlmProxyEnabled: true);

        var prepared = await service.PrepareSessionAsync(LLM.KimiK3, envName: null, extraArgs: null);

        Assert.False(prepared.OpenCodeProxyActive);
        Assert.False(prepared.Environment.ContainsKey(LlmProxyZaiConfig.ConfigContentVariable));
    }

    [Fact]
    public async Task PrepareSession_Glm52_DoesNotInjectModelWhenEnvIsSet()
    {
        var service = CreateService();

        // For custom env launches, the model is already in CustomArgs (built by the env
        // settings form). CommandService must NOT inject --model again — that would duplicate
        // the flag. The env's CustomArgs arrive as extraArgs.
        var prepared = await service.PrepareSessionAsync(
            LLM.Glm52,
            envName: "my-glm-env",
            extraArgs: ["--model=zai/glm-5.2", "--auto"]);

        Assert.Equal("opencode --model=zai/glm-5.2 --auto", prepared.LaunchCommand);
        // Exactly one --model arg — no duplicate injection.
        Assert.Equal(
            1,
            prepared.LaunchCommand.Split(' ').Count(tok => tok.StartsWith("--model")));
    }

    [Fact]
    public async Task PrepareSession_Glm52_SetsXdgConfigHomeForEnvIsolation()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(
            LLM.Glm52, envName: "my-glm-env", extraArgs: null);

        Assert.True(
            prepared.Environment.ContainsKey("XDG_CONFIG_HOME"),
            "GLM 5.2 must share OpenCode's XDG_CONFIG_HOME env isolation.");
    }

    [Fact]
    public async Task PrepareSession_Shell_ReturnsEmptyCommandAndIgnoresEnvAndArgs()
    {
        var service = CreateService();

        // A plain shell must not type any agent command, and must ignore custom
        // environments / args / prompts entirely.
        var prepared = await service.PrepareSessionAsync(
            LLM.Shell,
            envName: "some-env",
            extraArgs: new[] { "--dangerous" },
            initialPrompt: "do something",
            summary: "a summary");

        Assert.Equal(string.Empty, prepared.Command);
        Assert.Equal(string.Empty, prepared.LaunchCommand);
        Assert.Empty(prepared.SetupCommands);
        Assert.False(prepared.Environment.ContainsKey("CLAUDE_CODE_FORCE_SYNC_OUTPUT"));
    }

    private static CommandService CreateService(
        bool codexLlmProxyEnabled = false,
        bool claudeLlmProxyEnabled = false,
        bool openCodeLlmProxyEnabled = false,
        string codexLlmProxyMode = CodexLlmProxySettings.ModeSubscription)
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
            .Returns(new LlmProxySettings(
                CodexLlmProxyEnabled: codexLlmProxyEnabled,
                CodexLlmProxyMode: codexLlmProxyMode,
                ClaudeLlmProxyEnabled: claudeLlmProxyEnabled,
                OpenCodeLlmProxyEnabled: openCodeLlmProxyEnabled));
        return new CommandService(envService, proxyContext.Object, proxySettings.Object);
    }

    private static string ExpectedQuietRedirect() =>
        OperatingSystem.IsWindows() ? "*> $null" : ">/dev/null 2>&1";
}
