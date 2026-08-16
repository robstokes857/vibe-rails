using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.LlmClis;

/// <summary>
/// The Job spawn command exists to guarantee one property: the PTY's process ends when the CLI
/// ends. These assert the two things that would silently break it — a shell left sitting at a
/// prompt, and an exit code that doesn't come from the CLI.
/// </summary>
public class CliSpawnCommandBuilderTests
{
    [Fact]
    public void Windows_RunsTheCliAndExitsWithItsCode()
    {
        var (app, argv, script) = BuildWindows("claude", ["--model", "opus"], []);
        try
        {
            Assert.Equal("pwsh.exe", app);
            Assert.Equal(["-File", argv[1]], argv);

            Assert.Contains("$exe = 'claude'", script);
            Assert.Contains("$argv = @('--model', 'opus')", script);
            Assert.Contains("& $exe @argv", script);

            // The PTY waits on this script, so its exit code IS the run's exit code.
            Assert.Contains("exit $LASTEXITCODE", script);

            // No interactive shell is left behind — that is the whole bug this replaces.
            Assert.DoesNotContain("-NoExit", script);
        }
        finally
        {
            File.Delete(argv[1]);
        }
    }

    [Fact]
    public void Windows_FailureToStartTheCliIsNotReportedAsSuccess()
    {
        var (_, argv, script) = BuildWindows("claude", [], []);
        try
        {
            // $LASTEXITCODE is null when the CLI never ran, and `exit $null` exits 0 — which would
            // record a Job that never started as Succeeded.
            Assert.Contains("if ($null -eq $LASTEXITCODE) { exit 1 }", script);
        }
        finally
        {
            File.Delete(argv[1]);
        }
    }

    [Fact]
    public void Windows_RunsMcpSetupBeforeTheCli()
    {
        var (_, argv, script) = BuildWindows("claude", [], ["claude mcp remove viberails-mcp *> $null", "claude mcp add viberails-mcp -- vb mcp"]);
        try
        {
            var setupIndex = script.IndexOf("claude mcp add viberails-mcp", StringComparison.Ordinal);
            var launchIndex = script.IndexOf("& $exe @argv", StringComparison.Ordinal);

            Assert.True(setupIndex >= 0, "MCP setup must survive the move off the shell path");
            Assert.True(setupIndex < launchIndex, "MCP setup must run before the CLI launches");
        }
        finally
        {
            File.Delete(argv[1]);
        }
    }

    [Fact]
    public void Windows_PromptContainingQuotesSurvivesIntact()
    {
        // Passed as argv rather than interpolated into a command string, so shell metacharacters
        // are inert. A prompt is user text and routinely contains all of these.
        const string prompt = "it's a $test & `backtick` \"quoted\" | pipe";
        var (_, argv, script) = BuildWindows("claude", [prompt], []);
        try
        {
            Assert.Contains("$argv = @('it''s a $test & `backtick` \"quoted\" | pipe')", script);
        }
        finally
        {
            File.Delete(argv[1]);
        }
    }

    [Fact]
    public void Posix_ExecsTheCliSoNoShellSurvives()
    {
        var (app, argv) = CliSpawnCommandBuilder.BuildPosix("codex", ["--model", "gpt"], []);

        Assert.Contains(app, new[] { "bash", "/bin/zsh" });
        Assert.Equal("-c", argv[0]);

        // exec replaces the shell with the CLI: the PTY's process becomes the CLI itself.
        Assert.Equal("exec 'codex' '--model' 'gpt'", argv[1]);
    }

    [Fact]
    public void Posix_RunsMcpSetupBeforeExec()
    {
        var (_, argv) = CliSpawnCommandBuilder.BuildPosix("codex", [], ["codex mcp remove viberails-mcp >/dev/null 2>&1"]);

        Assert.Equal("codex mcp remove viberails-mcp >/dev/null 2>&1; exec 'codex'", argv[1]);
    }

    [Fact]
    public void Posix_PromptContainingSingleQuotesIsEscaped()
    {
        var (_, argv) = CliSpawnCommandBuilder.BuildPosix("claude", ["it's fine"], []);

        Assert.Equal(@"exec 'claude' 'it'\''s fine'", argv[1]);
    }

    /// <summary>
    /// The executable a Job spawns must be the same one the native terminal launcher would run.
    /// Antigravity is the trap: enum "Antigravity", env name "antigravity", binary "agy" — and the
    /// OpenCode-backed pseudo-CLIs are a second one.
    /// </summary>
    [Theory]
    [InlineData(LLM.Claude)]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Antigravity)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Glm52)]
    [InlineData(LLM.Grok46)]
    public void ResolvedExecutableMatchesTheNativeLauncher(LLM llm)
    {
        var launchService = new LaunchLLMService(
            new ClaudeLlmCliLauncher(),
            new CodexLlmCliLauncher(),
            new AntigravityLlmCliLauncher(),
            new CopilotLlmCliLauncher(),
            new OpencodeLlmCliLauncher());

        Assert.Equal(launchService.GetLauncher(llm).CliExecutable, CommandService.ResolveCliExecutable(llm));
    }

    [Theory]
    [InlineData(LLM.OpenCode, "opencode")]
    [InlineData(LLM.Glm52, "glm-5.2")]
    [InlineData(LLM.Grok46, "grok-4.6")]
    public void NativeLauncherBootstrapPreservesRequestedLlmWireName(LLM llm, string expectedEnv)
    {
        var argv = BaseLlmCliLauncher.BuildVbArgv(
            llm,
            "C:\\project",
            [],
            envName: null);

        Assert.Equal(["--env", expectedEnv, "--workdir", "C:\\project"], argv);
    }

    private static (string App, string[] Argv, string Script) BuildWindows(
        string cli, string[] argv, string[] setupCommands)
    {
        var (app, builtArgv) = CliSpawnCommandBuilder.BuildWindows(cli, argv, setupCommands);
        return (app, builtArgv, File.ReadAllText(builtArgv[1]));
    }
}
