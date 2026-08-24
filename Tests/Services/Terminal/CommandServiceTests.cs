using Moq;
using VibeRails.Interfaces;
using VibeRails.Services;
using TokenSaver;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
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
    private readonly string? _originalGrokProxyUrl;
    private readonly string _originalEnvPath;

    public CommandServiceTests()
    {
        // Isolate from any caller-set fake-CLI override that would short-circuit
        // PrepareSession before the LLM-specific env injection runs.
        _originalFakeCliFlag = Environment.GetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI");
        _originalOpenCodeConfig = Environment.GetEnvironmentVariable(
            LlmProxyZaiConfig.ConfigContentVariable);
        _originalGrokProxyUrl = Environment.GetEnvironmentVariable(
            LlmProxyGrokConfig.ChatProxyBaseUrlVariable);
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", null);
        Environment.SetEnvironmentVariable(LlmProxyZaiConfig.ConfigContentVariable, null);
        Environment.SetEnvironmentVariable(LlmProxyGrokConfig.ChatProxyBaseUrlVariable, null);

        // The env-name launch tests resolve GetEnvironmentVariables -> ParserConfigs.GetEnvPath(),
        // a process-global that is empty unless startup set it. Pin it to a temp root here so those
        // tests don't depend on another parallel collection having initialized it first
        // (Path.GetFullPath("") throws otherwise). Same set/restore pattern as
        // LlmCliEnvironmentServiceTests.
        _originalEnvPath = ParserConfigs.GetEnvPath();
        ParserConfigs.SetEnvPath(Path.Combine(Path.GetTempPath(), "viberails-cmdsvc-tests-envs"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", _originalFakeCliFlag);
        Environment.SetEnvironmentVariable(
            LlmProxyZaiConfig.ConfigContentVariable,
            _originalOpenCodeConfig);
        Environment.SetEnvironmentVariable(
            LlmProxyGrokConfig.ChatProxyBaseUrlVariable,
            _originalGrokProxyUrl);
        ParserConfigs.SetEnvPath(_originalEnvPath);
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
    [InlineData(LLM.Grok46)]
    [InlineData(LLM.Glm53)]
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
    public async Task PrepareSession_Opencode_UsesOpencodeExecutable()
    {
        var service = CreateService();

        // OpenCode's binary is `opencode` (== enum name lowercased, so no remap like agy).
        var prepared = await service.PrepareSessionAsync(LLM.OpenCode, envName: null, extraArgs: null);

        Assert.Equal("opencode", prepared.LaunchCommand);
    }

    [Theory]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Glm52)]
    [InlineData(LLM.Glm53)]
    public async Task PrepareSession_OpenCodeBackedClis_AddVibeRailsMcpBeforeLaunch(LLM llm)
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(llm, envName: null, extraArgs: null);

        var setup = Assert.Single(prepared.SetupCommands);
        var expectedExecutable = OperatingSystem.IsWindows() ? "opencode.cmd" : "opencode";
        Assert.StartsWith(
            $"{expectedExecutable} mcp add viberails-mcp -- ",
            setup);
        Assert.Contains(" mcp", setup);
        Assert.StartsWith(setup + "; ", prepared.Command);
        Assert.EndsWith(prepared.LaunchCommand, prepared.Command);
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
        Assert.Contains("http://127.0.0.1:4321/llm/xai/v1", config);
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
    public async Task PrepareSession_Grok46_UsesGrokExecutableWithPinnedModel()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(LLM.Grok46, envName: null, extraArgs: null);

        Assert.Equal("grok --model=grok-4.6", prepared.LaunchCommand);
        Assert.Equal("grok", prepared.Executable);
    }

    [Fact]
    public async Task PrepareSession_Grok46_PassesPromptAsTrailingPositional()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(
            LLM.Grok46, envName: null, extraArgs: null, initialPrompt: "hello world");

        Assert.DoesNotContain("--prompt=", prepared.LaunchCommand);
        Assert.DoesNotContain(" -p ", prepared.LaunchCommand);
        Assert.StartsWith("grok --model=grok-4.6 ", prepared.LaunchCommand);
        Assert.Contains("hello world", prepared.LaunchCommand);
        Assert.Equal("hello world", prepared.Argv?[^1]);
    }

    [Fact]
    public async Task PrepareSession_Grok46_AddsVibeRailsMcpBeforeLaunch()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(LLM.Grok46, envName: null, extraArgs: null);

        Assert.Equal(2, prepared.SetupCommands.Count);
        Assert.Contains("grok mcp remove viberails-mcp", prepared.SetupCommands[0]);
        Assert.StartsWith("grok mcp add --scope user viberails-mcp -- ", prepared.SetupCommands[1]);
    }

    [Fact]
    public async Task PrepareSession_Grok46_InjectsXaiProxyEnvAndMergesHeaders()
    {
        var service = CreateService(grokLlmProxyEnabled: true);

        var prepared = await service.PrepareSessionAsync(LLM.Grok46, envName: null, extraArgs: null);

        Assert.True(prepared.OpenCodeProxyActive);
        Assert.False(prepared.Environment.ContainsKey(LlmProxyZaiConfig.ConfigContentVariable));
        Assert.Equal(
            "http://127.0.0.1:4321/llm/cli-chat/v1",
            prepared.Environment[LlmProxyGrokConfig.ChatProxyBaseUrlVariable]);
        Assert.False(prepared.Environment.ContainsKey(LlmProxyGrokConfig.ModelsBaseUrlVariable));
        Assert.Equal(
            "test-session-token",
            prepared.Environment[LocalLlmProxyContext.SessionTokenVariable]);
        Assert.Equal(
            "test-tab-token",
            prepared.Environment[LocalLlmProxyContext.TabTokenVariable]);
        Assert.DoesNotContain("test-session-token", prepared.LaunchCommand);
        Assert.DoesNotContain("GROK_HOME", prepared.Environment.Keys);
    }

    [Fact]
    public async Task PrepareSession_Grok46_ApiModeAlsoSetsModelsBaseUrl()
    {
        var service = CreateService(
            grokLlmProxyEnabled: true,
            grokLlmProxyMode: CodexLlmProxySettings.ModeApi);

        var prepared = await service.PrepareSessionAsync(LLM.Grok46, envName: null, extraArgs: null);

        Assert.Equal(
            "http://127.0.0.1:4321/llm/cli-chat/v1",
            prepared.Environment[LlmProxyGrokConfig.ChatProxyBaseUrlVariable]);
        Assert.Equal(
            "http://127.0.0.1:4321/llm/cli-chat/v1",
            prepared.Environment[LlmProxyGrokConfig.ModelsBaseUrlVariable]);
    }

    [Fact]
    public async Task PrepareSession_Grok46_DoesNotInjectModelWhenEnvIsSet()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(
            LLM.Grok46,
            envName: "my-grok-env",
            extraArgs: ["-m=grok-4.6", "--yolo"]);

        Assert.Equal("grok -m=grok-4.6 --yolo", prepared.LaunchCommand);
        Assert.Equal(
            1,
            prepared.LaunchCommand.Split(' ').Count(tok => tok.StartsWith("-m") || tok.StartsWith("--model")));
    }

    [Fact]
    public async Task PrepareSession_Grok46_DoesNotSetGrokHomeOrXdgConfigHome()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(
            LLM.Grok46, envName: "my-grok-env", extraArgs: null);

        Assert.False(prepared.Environment.ContainsKey("XDG_CONFIG_HOME"));
        Assert.False(prepared.Environment.ContainsKey("GROK_HOME"));
    }

    [Fact]
    public async Task PrepareSession_Grok46_SkipsProxyWhenChatProxyUrlAlreadySet()
    {
        Environment.SetEnvironmentVariable(LlmProxyGrokConfig.ChatProxyBaseUrlVariable, "http://example.test/v1");
        try
        {
            var service = CreateService(grokLlmProxyEnabled: true);

            var prepared = await service.PrepareSessionAsync(LLM.Grok46, envName: null, extraArgs: null);

            Assert.False(prepared.OpenCodeProxyActive);
            Assert.False(prepared.Environment.ContainsKey(LlmProxyGrokConfig.ChatProxyBaseUrlVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmProxyGrokConfig.ChatProxyBaseUrlVariable, null);
        }
    }

    [Fact]
    public async Task PrepareSession_Glm53_UsesOpencodeExecutableWithPinnedModel()
    {
        var service = CreateService();

        // GLM 5.3 is a pseudo-CLI backed by OpenCode. The binary is `opencode` (not `glm53`),
        // and base CLI launches inject --model=zai-coding-plan/glm-5.3 — GLM 5.3 ships under
        // the zai-coding-plan provider in the live OpenCode catalog, not plain zai.
        var prepared = await service.PrepareSessionAsync(LLM.Glm53, envName: null, extraArgs: null);

        Assert.Equal("opencode --model=zai-coding-plan/glm-5.3", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Glm53_PassesPromptViaPromptFlag()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(
            LLM.Glm53, envName: null, extraArgs: null, initialPrompt: "hello world");

        Assert.StartsWith("opencode --model=zai-coding-plan/glm-5.3 --prompt=", prepared.LaunchCommand);
    }

    [Fact]
    public async Task PrepareSession_Glm53_InjectsOpenCodeProxyConfigWithoutRemappingCodingPlanProvider()
    {
        // GLM 5.3 is OpenCode-backed, so the OpenCode proxy config is injected — but it only
        // remaps the zai/xai providers. The pinned zai-coding-plan provider must NOT appear in
        // the injected config: its traffic deliberately goes direct to Z.AI (no token saver).
        var service = CreateService(openCodeLlmProxyEnabled: true);

        var prepared = await service.PrepareSessionAsync(LLM.Glm53, envName: null, extraArgs: null);

        Assert.True(prepared.OpenCodeProxyActive);
        Assert.Equal(
            "test-session-token",
            prepared.Environment[LocalLlmProxyContext.SessionTokenVariable]);
        Assert.Equal(
            "test-tab-token",
            prepared.Environment[LocalLlmProxyContext.TabTokenVariable]);
        var config = prepared.Environment[LlmProxyZaiConfig.ConfigContentVariable];
        Assert.Contains("http://127.0.0.1:4321/llm/zai/api/paas/v4", config);
        Assert.Contains("http://127.0.0.1:4321/llm/xai/v1", config);
        Assert.DoesNotContain("zai-coding-plan", config);
    }

    [Fact]
    public async Task PrepareSession_Glm53_DoesNotInjectModelWhenEnvIsSet()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(
            LLM.Glm53,
            envName: "my-glm53-env",
            extraArgs: ["--model=zai-coding-plan/glm-5.3", "--auto"]);

        Assert.Equal("opencode --model=zai-coding-plan/glm-5.3 --auto", prepared.LaunchCommand);
        Assert.Equal(
            1,
            prepared.LaunchCommand.Split(' ').Count(tok => tok.StartsWith("--model")));
    }

    [Fact]
    public async Task PrepareSession_Glm53_SetsXdgConfigHomeForEnvIsolation()
    {
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(
            LLM.Glm53, envName: "my-glm53-env", extraArgs: null);

        Assert.True(
            prepared.Environment.ContainsKey("XDG_CONFIG_HOME"),
            "GLM 5.3 must share OpenCode's XDG_CONFIG_HOME env isolation.");
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
        bool grokLlmProxyEnabled = false,
        string grokLlmProxyMode = CodexLlmProxySettings.ModeSubscription,
        string codexLlmProxyMode = CodexLlmProxySettings.ModeSubscription,
        ILlmProxySessionState? sessionState = null)
    {
        var fileService = new Mock<IFileService>();
        fileService.Setup(x => x.GetUserProfilePath()).Returns(Path.Combine(Path.GetTempPath(), "viberails-grok-tests"));
        fileService.Setup(x => x.FileExists(It.IsAny<string>())).Returns(false);
        fileService.Setup(x => x.DirectoryExists(It.IsAny<string>())).Returns(false);
        fileService
            .Setup(x => x.WriteAllTextAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<FileMode>(),
                It.IsAny<FileShare>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        fileService
            .Setup(x => x.ReadAllTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        var envService = new LlmCliEnvironmentService(
            new ClaudeLlmCliEnvironment(fileService.Object),
            new CodexLlmCliEnvironment(fileService.Object),
            new AntigravityLlmCliEnvironment(fileService.Object),
            new CopilotLlmCliEnvironment(fileService.Object),
            new OpencodeLlmCliEnvironment(fileService.Object),
            new GrokLlmCliEnvironment(fileService.Object),
            fileService.Object);
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
                OpenCodeLlmProxyEnabled: openCodeLlmProxyEnabled,
                GrokLlmProxyEnabled: grokLlmProxyEnabled,
                GrokLlmProxyMode: grokLlmProxyMode)
            {
                GrokLlmProxyLaunchEnabled = grokLlmProxyEnabled
            });
        return new CommandService(
            envService,
            proxyContext.Object,
            proxySettings.Object,
            sessionState ?? new LlmProxySessionState(),
            fileService.Object);
    }

    private static string ExpectedQuietRedirect() =>
        OperatingSystem.IsWindows() ? "*> $null" : ">/dev/null 2>&1";
}
