using System.Diagnostics;
using System.Text;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.LlmClis;

public class MacCliLaunchTests
{
    [Fact]
    public void PromptAndExecutableAreArgvNotShellProgram()
    {
        const string executable = "/Applications/Codex CLI/codex";
        string[] arguments = ["--config", "model_provider=\"enterprise\"", "--", "you're ready\nDon't expand $HOME; `date`"];
        var (_, argv) = CliSpawnCommandBuilder.BuildInteractivePosix(executable, arguments, []);

        Assert.Equal("-lic", argv[0]);
        Assert.Contains("\"$@\"", argv[1]);
        Assert.DoesNotContain(executable, argv[1]);
        Assert.DoesNotContain(arguments[^1], argv[1]);
        Assert.Equal("viberails-cli", argv[2]);
        Assert.Equal(new[] { executable }.Concat(arguments), argv.Skip(3));
        Assert.Contains("exec ", argv[1]); // returns to a usable shell after the CLI exits
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(23, 0, false)]
    [InlineData(0, 17, false)]
    [InlineData(0, 0, true)]
    [InlineData(23, 0, true)]
    [InlineData(0, 17, true)]
    public async Task StubReceivesExactArgvAndReportsFailures(int registrationExit, int cliExit, bool useMacPty)
    {
        if (useMacPty && !OperatingSystem.IsMacOS())
            Assert.Skip("The native macOS PTY probe runs on macOS.");
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")
            : ShellDefaults.GetUnixCommandShellPath();
        if (OperatingSystem.IsWindows() && !File.Exists(shell))
            Assert.Skip("A POSIX shell is required for the argv round-trip test.");

        var directory = Path.Combine(Path.GetTempPath(), "viberails mac argv ' " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stub = Path.Combine(directory, "codex stub 'quoted'");
        var capture = Path.Combine(directory, "capture");
        var setupCapture = Path.Combine(directory, "setup");
        var marker = Path.Combine(directory, "must not exist");
        await File.WriteAllTextAsync(stub, """
            #!/bin/sh
            if [ "$1" = mcp ]; then
              printf '%s\000' "$@" > "$VIBERAILS_SETUP_CAPTURE"
              exit "$VIBERAILS_REGISTRATION_EXIT"
            fi
            printf '%s\000' "$@" > "$VIBERAILS_ARGV_CAPTURE"
            printf 'STUB_CODEX_STARTED\n'
            exit "$VIBERAILS_CLI_EXIT"
            """.Replace("\r\n", "\n"), new UTF8Encoding(false), TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var stubPath = ToShellPath(stub);
        var serverPath = "/Applications/Vibe Rails/it's here/vb";
        string[] setupArgs = ["mcp", "add", "viberails-mcp", "--", serverPath, "mcp"];
        var setup = CommandService.ReportCodexMcpFailure(
            string.Join(" ", new[] { stubPath }.Concat(setupArgs).Select(ShellArgSanitizer.QuotePosixSingleQuoted)), false);
        string[] arguments = [
            "--config", "model_provider=\"enterprise\"",
            "--config", "model_providers.enterprise.supports_websockets=false",
            "--config", "mcp_servers.viberails-mcp.env_vars=[\"VIBERAILS_TOOL_SESSION_TOKEN\"]",
            "--", "  you're ready; Don't change ‘Unicode’ 日本語 🙂\t\r\nnext line\n"
                + "\"quotes\" C:\\path\\backslashes '' $HOME * ? & | < > # ! "
                + "$(touch '" + ToShellPath(marker) + "') `touch '" + ToShellPath(marker) + "'` "
                + new string('x', 6_000)
        ];
        var (_, argv) = CliSpawnCommandBuilder.BuildInteractivePosix(stubPath, arguments, [setup]);
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in argv) start.ArgumentList.Add(argument);
        start.Environment["VIBERAILS_ARGV_CAPTURE"] = ToShellPath(capture);
        start.Environment["VIBERAILS_SETUP_CAPTURE"] = ToShellPath(setupCapture);
        start.Environment["VIBERAILS_REGISTRATION_EXIT"] = registrationExit.ToString();
        start.Environment["VIBERAILS_CLI_EXIT"] = cliExit.ToString();
        // Prevent personal profiles from affecting the probe. Production loads the real profile.
        start.Environment["HOME"] = ToShellPath(directory);
        start.Environment["ZDOTDIR"] = ToShellPath(directory);
        start.Environment.Remove("BASH_ENV");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if (useMacPty)
            {
                // Force a slow profile: no command may be typed into the canonical tty while
                // zsh is loading it. The prompt marks the usable shell after the stub exits.
                await File.WriteAllTextAsync(Path.Combine(directory, ".zshrc"),
                    "sleep 0.2\nPROMPT='STUB_SHELL_READY> '\n", timeout.Token);
                await using var terminal = await TerminalPty.CreateAsync(directory,
                    start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value!),
                    app: shell, argv: argv, ct: timeout.Token);
                var consumer = new ShellReadyConsumer();
                terminal.Subscribe(consumer);
                terminal.StartReadLoop();
                await consumer.Ready.Task.WaitAsync(timeout.Token);
                Assert.Equal(arguments, ReadArgv(capture));
                Assert.Equal(setupArgs, ReadArgv(setupCapture));
                Assert.False(File.Exists(marker));
                Assert.Equal(registrationExit != 0, consumer.Output.Contains("Codex MCP registration failed (exit 23)"));
                Assert.Equal(cliExit != 0, consumer.Output.Contains("CLI exited (exit 17)"));
                await terminal.SendCommandAsync("exit", timeout.Token);
                return;
            }
            using var process = Process.Start(start)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                // The CLI never reads stdin; this exits only the replacement interactive shell.
                await process.StandardInput.WriteLineAsync("exit");
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var stdout = await output;
                var stderr = await error;
                Assert.Contains("STUB_CODEX_STARTED", stdout);
                Assert.Equal(arguments, ReadArgv(capture));
                Assert.Equal(setupArgs, ReadArgv(setupCapture));
                Assert.False(File.Exists(marker), "A prompt was evaluated by the shell.");
                Assert.Equal(registrationExit != 0, stderr.Contains("Codex MCP registration failed (exit 23)"));
                Assert.Equal(cliExit != 0, stderr.Contains("CLI exited (exit 17)"));
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string[] ReadArgv(string path) => File.ReadAllText(path, Encoding.UTF8).Split('\0')[..^1];

    private sealed class ShellReadyConsumer : ITerminalConsumer
    {
        private readonly StringBuilder _output = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Output { get { lock (_output) return _output.ToString(); } }
        public void OnOutput(ReadOnlyMemory<byte> data)
        {
            lock (_output)
            {
                _output.Append(Encoding.UTF8.GetString(data.Span));
                if (_output.ToString().Contains("STUB_SHELL_READY>")) Ready.TrySetResult();
            }
        }
    }

    private static string ToShellPath(string path) => OperatingSystem.IsWindows()
        ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
        : path;
}
