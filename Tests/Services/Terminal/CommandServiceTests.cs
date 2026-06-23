using Moq;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
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
/// Also pins the MCP opt-in behavior: registration is gated on the MCP setting
/// (off by default), and while opted-out each CLI is cleaned up exactly once via
/// a per-CLI record in the global cache.
/// </summary>
public class CommandServiceTests : IDisposable
{
    private readonly string? _originalFakeCliFlag;
    private readonly bool _originalMcpEnabled;

    public CommandServiceTests()
    {
        // Isolate from any caller-set fake-CLI override that would short-circuit
        // PrepareSession before the LLM-specific env injection runs.
        _originalFakeCliFlag = Environment.GetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI");
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", null);

        // MCP is opt-in; default the runtime flag off so each test sets what it needs.
        _originalMcpEnabled = ParserConfigs.GetMcpEnabled();
        ParserConfigs.SetMcpEnabled(false);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI", _originalFakeCliFlag);
        ParserConfigs.SetMcpEnabled(_originalMcpEnabled);
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
    public async Task PrepareSession_McpEnabled_SupportedClis_AddsVibeRailsMcpSetupCommand(
        LLM llm,
        string expectedRemoveCommand,
        string expectedAddPrefix)
    {
        ParserConfigs.SetMcpEnabled(true);
        var service = CreateService();

        var prepared = await service.PrepareSessionAsync(llm, envName: null, extraArgs: null);

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
    [InlineData(LLM.Claude, "claude mcp remove viberails-mcp")]
    [InlineData(LLM.Codex, "codex mcp remove viberails-mcp")]
    [InlineData(LLM.Antigravity, "agy mcp remove viberails-mcp")]
    [InlineData(LLM.Copilot, "copilot mcp remove viberails-mcp")]
    public async Task PrepareSession_McpDisabled_RemovesOnceThenStops(LLM llm, string expectedRemoveCommand)
    {
        // Opted out (default). First launch must clean up any prior registration with a single
        // `mcp remove` — and never add the server.
        var service = CreateService();

        var first = await service.PrepareSessionAsync(llm, envName: null, extraArgs: null);

        Assert.Equal(new[] { expectedRemoveCommand }, first.SetupCommands);
        Assert.DoesNotContain(" mcp add", first.Command);

        // Second launch for the same CLI: the removal was already recorded, so we must not
        // re-issue `mcp remove` on every launch.
        var second = await service.PrepareSessionAsync(llm, envName: null, extraArgs: null);

        Assert.Empty(second.SetupCommands);
    }

    [Fact]
    public async Task PrepareSession_McpDisabled_TracksRemovalPerCli()
    {
        // Removing MCP from one CLI must not suppress the removal for another — each CLI is a
        // separate registration, so "off" has to clean every CLI independently.
        var service = CreateService();

        var claude = await service.PrepareSessionAsync(LLM.Claude, envName: null, extraArgs: null);
        Assert.Equal(new[] { "claude mcp remove viberails-mcp" }, claude.SetupCommands);

        // Claude is now recorded as removed, but Codex still gets its own one-time removal.
        var codex = await service.PrepareSessionAsync(LLM.Codex, envName: null, extraArgs: null);
        Assert.Equal(new[] { "codex mcp remove viberails-mcp" }, codex.SetupCommands);
    }

    [Theory]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Antigravity)]
    [InlineData(LLM.Copilot)]
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

    private static CommandService CreateService(IGlobalCache? globalCache = null)
    {
        var fileService = new Mock<IFileService>().Object;
        var envService = new LlmCliEnvironmentService(
            new ClaudeLlmCliEnvironment(fileService),
            new CodexLlmCliEnvironment(fileService),
            new AntigravityLlmCliEnvironment(fileService),
            new CopilotLlmCliEnvironment(fileService),
            fileService);
        return new CommandService(envService, globalCache ?? new InMemoryGlobalCache());
    }

    /// <summary>Minimal in-memory <see cref="IGlobalCache"/> so tests don't touch SQLite.</summary>
    private sealed class InMemoryGlobalCache : IGlobalCache
    {
        private readonly Dictionary<string, string> _store = new();

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string value)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> GetAsBoolAsync(string key, bool defaultValue = false) =>
            Task.FromResult(_store.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : defaultValue);

        public Task<int?> GetAsIntAsync(string key) =>
            Task.FromResult(_store.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : (int?)null);

        public Task<Dictionary<string, string>> GetAllAsync() =>
            Task.FromResult(new Dictionary<string, string>(_store));

        public Task RemoveAsync(string key)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }
    }
}
