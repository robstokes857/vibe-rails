using VibeRails.DTOs;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Terminal.Consumers;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public class TerminalRunner
{
    private readonly ITerminalStateService _stateService;
    private readonly LlmCliEnvironmentService _envService;
    private readonly McpSettings _mcpSettings;

    public TerminalRunner(ITerminalStateService stateService, LlmCliEnvironmentService envService, McpSettings mcpSettings)
    {
        _stateService = stateService;
        _envService = envService;
        _mcpSettings = mcpSettings;
    }

    /// <summary>
    /// Build the CLI command string and environment dictionary.
    /// Shared by both CLI and Web paths.
    /// </summary>
    public (string command, Dictionary<string, string> environment) PrepareSession(
        LLM llm, string? envName, string[]? extraArgs)
    {
        var cli = llm.ToString().ToLower();
        var cliCommand = extraArgs?.Length > 0
            ? $"{cli} {string.Join(" ", extraArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}"
            : cli;

        var builder = new ShellCommandBuilder()
            .SetLaunchCommand(cliCommand);

        // Register MCP server before launch
        if (!string.IsNullOrEmpty(_mcpSettings.ServerPath) && File.Exists(_mcpSettings.ServerPath))
        {
            var mcpSetup = llm switch
            {
                LLM.Claude => $"claude mcp add viberails-mcp \"{_mcpSettings.ServerPath}\"",
                LLM.Codex => $"codex mcp add viberails-mcp -- \"{_mcpSettings.ServerPath}\"",
                LLM.Gemini => $"gemini mcp add --scope user viberails-mcp \"{_mcpSettings.ServerPath}\"",
                _ => null
            };

            if (mcpSetup != null)
                builder.AddSetup(mcpSetup);
        }

        var environment = new Dictionary<string, string>
        {
            ["LANG"] = "en_US.UTF-8",
            ["LC_ALL"] = "en_US.UTF-8",
            ["PYTHONIOENCODING"] = "utf-8"
        };

        if (!string.IsNullOrEmpty(envName))
        {
            var envVars = _envService.GetEnvironmentVariables(envName, llm);
            foreach (var kvp in envVars)
                environment[kvp.Key] = kvp.Value;
        }

        return (builder.Build(), environment);
    }

    /// <summary>
    /// Create a Terminal + DB session with DbLoggingConsumer already wired.
    /// Used by both CLI and Web paths. Returns the remote connection if one was established.
    /// </summary>
    public async Task<(Terminal terminal, string sessionId, IRemoteTerminalConnection? remoteConnection)> CreateSessionAsync(
        LLM llm, string workDir, string? envName, string[]? extraArgs, CancellationToken ct, string? title = null)
    {
        var sessionId = await _stateService.CreateSessionAsync(llm.ToString(), workDir, envName, ct);
        var (command, environment) = PrepareSession(llm, envName, extraArgs);

        var terminal = await Terminal.CreateAsync(workDir, environment, title: title, ct: ct);

        // Always wire up DB logging
        terminal.Subscribe(new DbLoggingConsumer(_stateService, sessionId));

        // Connect to remote server if remote access is enabled
        IRemoteTerminalConnection? activeRemoteConn = null;
        if (ParserConfigs.GetRemoteAccess() && !string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey()))
        {
            var remoteConn = new RemoteTerminalConnection();
            await remoteConn.ConnectAsync(sessionId, ct);

            if (remoteConn.IsConnected)
            {
                terminal.Subscribe(new RemoteOutputConsumer(remoteConn));
                remoteConn.OnInputReceived += bytes =>
                    _ = terminal.WriteBytesAsync(bytes, CancellationToken.None);
                remoteConn.OnReplayRequested += () =>
                {
                    var replay = terminal.GetReplayBuffer();
                    if (replay.Length > 0)
                        _ = remoteConn.SendOutputAsync(replay);
                    // Don't send Ctrl+L — the replay buffer is sufficient for the remote
                    // browser. Ctrl+L causes the shell to redraw the entire screen, and
                    // since the browser already got the replay, it would show doubled content.
                };
                _stateService.TrackRemoteConnection(sessionId, remoteConn);
                activeRemoteConn = remoteConn;
            }
            else
            {
                await remoteConn.DisposeAsync();
            }
        }

        // Send the CLI command to the shell
        await terminal.SendCommandAsync(command, ct);

        return (terminal, sessionId, activeRemoteConn);
    }

    /// <summary>
    /// CLI path: creates terminal, wires Console I/O, blocks until exit.
    /// </summary>
    public async Task<int> RunCliAsync(LLM llm, string workDir, string? envName, string[]? extraArgs, CancellationToken ct)
    {
        var (terminal, sessionId, _) = await CreateSessionAsync(llm, workDir, envName, extraArgs, ct);
        var exitCode = 0;

        await using (terminal)
        {
            // Wire up console output
            terminal.Subscribe(new ConsoleOutputConsumer());

            // Start the read loop
            terminal.StartReadLoop();

            // Console input loop (blocks until cancelled or PTY exits)
            try
            {
                await ConsoleInputLoopAsync(terminal, sessionId, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Terminal error: {ex.Message}");
                exitCode = 1;
            }

            try { exitCode = terminal.ExitCode; } catch { }
        }

        await _stateService.CompleteSessionAsync(sessionId, exitCode);
        return exitCode;
    }

    /// <summary>
    /// CLI + Web concurrent path: creates terminal, wires Console I/O,
    /// registers with TerminalSessionService so web viewers can connect.
    /// </summary>
    public async Task<int> RunCliWithWebAsync(
        LLM llm, string workDir, string? envName, string[]? extraArgs,
        ITerminalSessionService sessionService, CancellationToken ct)
    {
        var (terminal, sessionId, remoteConn) = await CreateSessionAsync(llm, workDir, envName, extraArgs, ct);
        var exitCode = 0;

        await using (terminal)
        {
            var consoleConsumer = new ConsoleOutputConsumer();
            terminal.Subscribe(consoleConsumer);

            // When a remote browser connects, mute local console output
            // so only the browser viewer sees terminal output (avoids double display).
            // When the browser disconnects, unmute so CLI resumes normal operation.
            if (remoteConn != null)
            {
                remoteConn.OnReplayRequested += () =>
                {
                    consoleConsumer.Muted = true;
                    Console.WriteLine("\r\n[Remote viewer connected — terminal output redirected to browser]");
                    _ = sessionService.DisconnectLocalViewerAsync("Session taken over by remote viewer");
                };
                remoteConn.OnBrowserDisconnected += () =>
                {
                    consoleConsumer.Muted = false;
                    Console.WriteLine("\r\n[Remote viewer disconnected — terminal output resumed]");
                };
            }

            terminal.StartReadLoop();

            // Register so web UI can find this terminal
            sessionService.RegisterExternalTerminal(terminal, sessionId);

            try
            {
                await ConsoleInputLoopAsync(terminal, sessionId, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Terminal error: {ex.Message}");
                exitCode = 1;
            }
            finally
            {
                await sessionService.UnregisterTerminalAsync();
            }

            try { exitCode = terminal.ExitCode; } catch { }
        }

        await _stateService.CompleteSessionAsync(sessionId, exitCode);
        return exitCode;
    }

    /// <summary>
    /// Console.ReadKey → PTY write loop for CLI path.
    /// </summary>
    private async Task ConsoleInputLoopAsync(Terminal terminal, string sessionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true);
            var input = KeyTranslator.TranslateKey(key);
            if (!string.IsNullOrEmpty(input))
            {
                _stateService.RecordInput(sessionId, input);
                await terminal.WriteAsync(input, ct);
            }
        }
    }
}
