using System.Diagnostics;
using System.Text;
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

            Assert.Contains(
                $"$exe = $utf8.GetString([System.Convert]::FromBase64String('{EncodeUtf8Base64("claude")}'))",
                script);
            Assert.Contains(EncodeUtf8Base64("--model"), script);
            Assert.Contains(EncodeUtf8Base64("opus"), script);
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
        // Encoded as data and then passed as argv rather than interpolated into a command string,
        // so shell metacharacters are inert. A prompt is user text and routinely contains these.
        const string prompt = "it's a $test & `backtick` \"quoted\" | pipe";
        var (_, argv, script) = BuildWindows("claude", [prompt], []);
        try
        {
            Assert.DoesNotContain(prompt, script);
            Assert.Contains(EncodeUtf8Base64(prompt), script);
        }
        finally
        {
            File.Delete(argv[1]);
        }
    }

    [Fact]
    public async Task Windows_PromptContainingSmartApostropheAndLiteralSlashNRoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string prompt = "The wrapper stored the separators literally as \\n. I’m correcting that.\r\nPlease don't change the text.";
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"viberails-argv-roundtrip-{Guid.NewGuid():N}");
        var captureScript = Path.Combine(tempDirectory, "capture.ps1");
        var capturePath = Path.Combine(tempDirectory, "captured.txt");
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(
            captureScript,
            """
            param([string] $OutputPath, [string] $Prompt)
            [System.IO.File]::WriteAllText(
                $OutputPath,
                $Prompt,
                [System.Text.UTF8Encoding]::new($false))
            """,
            new UTF8Encoding(false));

        var (app, argv) = CliSpawnCommandBuilder.BuildWindows(
            "pwsh.exe",
            ["-NoProfile", "-File", captureScript, capturePath, prompt],
            []);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = app,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in argv)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var cancellationToken = TestContext.Current.CancellationToken;
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = await standardOutput;
            var error = await standardError;
            Assert.True(
                process.ExitCode == 0,
                $"Generated PowerShell wrapper failed with exit code {process.ExitCode}:\n{output}\n{error}");
            Assert.Equal(prompt, File.ReadAllText(capturePath));
        }
        finally
        {
            File.Delete(argv[1]);
            Directory.Delete(tempDirectory, recursive: true);
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
    [InlineData(LLM.Glm53)]
    public void ResolvedExecutableMatchesTheNativeLauncher(LLM llm)
    {
        var launchService = new LaunchLLMService(
            new ClaudeLlmCliLauncher(),
            new CodexLlmCliLauncher(),
            new AntigravityLlmCliLauncher(),
            new CopilotLlmCliLauncher(),
            new OpencodeLlmCliLauncher(),
            new GrokLlmCliLauncher());

        Assert.Equal(launchService.GetLauncher(llm).CliExecutable, CommandService.ResolveCliExecutable(llm));
    }

    [Theory]
    [InlineData(LLM.OpenCode, "opencode")]
    [InlineData(LLM.Glm52, "glm-5.2")]
    [InlineData(LLM.Grok46, "grok-4.6")]
    [InlineData(LLM.Glm53, "glm-5.3")]
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

    private static string EncodeUtf8Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
