using Moq;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
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
/// </summary>
public class CommandServiceTests : IDisposable
{
    private readonly string? _originalFakeCliFlag;

    public CommandServiceTests()
    {
        // Isolate from any caller-set fake-CLI override that would short-circuit
        // PrepareSession before the LLM-specific env injection runs.
        _originalFakeCliFlag = Environment.GetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI");
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", _originalFakeCliFlag);
    }

    [Fact]
    public void PrepareSession_Claude_SetsForceSyncOutputEnvVar()
    {
        var service = CreateService();

        var prepared = service.PrepareSession(LLM.Claude, envName: null, extraArgs: null);

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
    public void PrepareSession_SupportedMcpClis_AddsVibeRailsMcpSetupCommand(
        LLM llm,
        string expectedRemoveCommand,
        string expectedAddPrefix)
    {
        var service = CreateService();

        var prepared = service.PrepareSession(llm, envName: null, extraArgs: null);

        Assert.Collection(
            prepared.SetupCommands,
            remove => Assert.Equal(expectedRemoveCommand, remove),
            add =>
            {
                Assert.StartsWith(expectedAddPrefix, add);
                Assert.Contains(" mcp", add);
            });

        Assert.StartsWith(expectedRemoveCommand + "; ", prepared.Command);
        Assert.Contains(expectedAddPrefix, prepared.Command);
        Assert.EndsWith(prepared.LaunchCommand, prepared.Command);
    }

    [Theory]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Antigravity)]
    [InlineData(LLM.Copilot)]
    public void PrepareSession_NonClaude_DoesNotSetForceSyncOutputEnvVar(LLM llm)
    {
        var service = CreateService();

        var prepared = service.PrepareSession(llm, envName: null, extraArgs: null);

        Assert.False(
            prepared.Environment.ContainsKey("CLAUDE_CODE_FORCE_SYNC_OUTPUT"),
            $"CLAUDE_CODE_FORCE_SYNC_OUTPUT must only be set for LLM.Claude, not {llm}.");
    }

    [Fact]
    public void PrepareSession_Antigravity_UsesAgyExecutable()
    {
        var service = CreateService();

        // Antigravity's binary is `agy`, not the lowercased enum name ("antigravity").
        // The in-app PTY must launch `agy`, or the session fails to start.
        var prepared = service.PrepareSession(LLM.Antigravity, envName: null, extraArgs: null);

        Assert.Equal("agy", prepared.LaunchCommand);
    }

    [Fact]
    public void PrepareSession_Antigravity_PassesPromptViaPromptInteractiveFlag()
    {
        var service = CreateService();

        // agy has no positional-prompt form; the initial prompt rides on
        // --prompt-interactive=<text> (mirrors Copilot's --interactive=).
        var prepared = service.PrepareSession(
            LLM.Antigravity, envName: null, extraArgs: null, initialPrompt: "hello world");

        Assert.StartsWith("agy --prompt-interactive=", prepared.LaunchCommand);
    }

    [Fact]
    public void PrepareSession_Shell_ReturnsEmptyCommandAndIgnoresEnvAndArgs()
    {
        var service = CreateService();

        // A plain shell must not type any agent command, and must ignore custom
        // environments / args / prompts entirely.
        var prepared = service.PrepareSession(
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

    private static CommandService CreateService()
    {
        var fileService = new Mock<IFileService>().Object;
        var envService = new LlmCliEnvironmentService(
            new ClaudeLlmCliEnvironment(fileService),
            new CodexLlmCliEnvironment(fileService),
            new AntigravityLlmCliEnvironment(fileService),
            new CopilotLlmCliEnvironment(fileService),
            fileService);
        return new CommandService(envService);
    }
}
