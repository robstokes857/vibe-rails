using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Terminal.Consumers;
using VibeRails.Services.Tracing;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public class TerminalRunner
{
    private readonly ITerminalStateService _stateService;
    private readonly ICommandService _commandService;
    private readonly TraceEventBuffer? _traceBuffer;

    public TerminalRunner(ITerminalStateService stateService, ICommandService commandService, TraceEventBuffer? traceBuffer = null)
    {
        _stateService = stateService;
        _commandService = commandService;
        _traceBuffer = traceBuffer;
    }

    /// <summary>
    /// Create a Terminal + DB session with DbLoggingConsumer already wired.
    /// Used by both CLI and Web paths. Returns the remote connection if one was established.
    /// </summary>
    public async Task<(Terminal terminal, string sessionId, IRemoteTerminalConnection? remoteConnection)> CreateSessionAsync(
        LLM llm, string workDir, string? envName, string[]? extraArgs, CancellationToken ct, string? title = null, bool makeRemote = false)
    {
        var shouldEnableRemote = ShouldEnableRemote(makeRemote);
        var sessionId = await _stateService.CreateSessionAsync(llm.ToString(), workDir, envName, shouldEnableRemote, ct);
        var (command, environment) = _commandService.PrepareSession(llm, envName, extraArgs);
        EmitTerminalLaunchTrace(sessionId, llm, workDir, envName, extraArgs, title, shouldEnableRemote, command, environment);

        var terminal = await Terminal.CreateAsync(workDir, environment, title: title, ct: ct);

        // Always wire up DB logging
        terminal.Subscribe(new DbLoggingConsumer(_stateService, sessionId));

        // For now, any configured instance defaults to remote-enabled sessions.
        // Keep makeRemote in the signature so explicit per-session controls can be reintroduced later.
        IRemoteTerminalConnection? activeRemoteConn = null;
        if (shouldEnableRemote)
        {
            var remoteConn = new RemoteTerminalConnection();
            await remoteConn.ConnectAsync(sessionId, ct);

            if (remoteConn.IsConnected)
            {
                terminal.Subscribe(new RemoteOutputConsumer(remoteConn));
                remoteConn.OnInputReceived += bytes =>
                    _ = TerminalIoRouter.RouteInputAsync(
                        _stateService,
                        terminal,
                        sessionId,
                        bytes,
                        TerminalIoSource.RemoteWebUi,
                        CancellationToken.None);
                remoteConn.OnResizeRequested += (cols, rows) =>
                {
                    try
                    {
                        TerminalResizeCoordinator.ApplyResize(
                            terminal,
                            _stateService,
                            sessionId,
                            cols,
                            rows,
                            TerminalIoSource.RemoteWebUi);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Remote] Failed to resize PTY to {Cols}x{Rows}", cols, rows);
                    }
                };
                remoteConn.OnCommandReceived += (command, payload) =>
                {
                    try
                    {
                        _stateService.RecordRemoteCommand(sessionId, command, payload, TerminalIoSource.RemoteWebUi);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Remote] Failed to handle custom command {Command}", command);
                    }
                };
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

    private void EmitTerminalLaunchTrace(
        string sessionId,
        LLM llm,
        string workDir,
        string? envName,
        string[]? extraArgs,
        string? title,
        bool remoteEnabled,
        string command,
        Dictionary<string, string> environment)
    {
        if (_traceBuffer == null)
            return;

        var launchDetail = BuildTerminalLaunchDetail(
            sessionId,
            llm,
            workDir,
            envName,
            extraArgs,
            title,
            remoteEnabled,
            command,
            environment);

        _traceBuffer.Add(TraceEvent.Create(
            TraceEventType.TerminalLaunch,
            "Terminal.Runner",
            $"Terminal launch: {llm} ({sessionId[..8]})",
            launchDetail));
    }

    private static string BuildTerminalLaunchDetail(
        string sessionId,
        LLM llm,
        string workDir,
        string? envName,
        string[]? extraArgs,
        string? title,
        bool remoteEnabled,
        string command,
        Dictionary<string, string> environment)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"sessionId: {sessionId}");
        sb.AppendLine($"llm: {llm}");
        sb.AppendLine($"workDir: {workDir}");
        sb.AppendLine($"environmentName: {envName ?? "(default)"}");
        sb.AppendLine($"title: {title ?? "(none)"}");
        sb.AppendLine($"remoteEnabled: {remoteEnabled}");
        sb.AppendLine("cliArgs:");

        if (extraArgs is { Length: > 0 })
        {
            foreach (var arg in extraArgs)
                sb.AppendLine($"  - {arg}");
        }
        else
        {
            sb.AppendLine("  - (none)");
        }

        sb.AppendLine("command:");
        sb.AppendLine($"  {command}");
        sb.AppendLine("environment:");

        foreach (var kvp in environment.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.AppendLine($"  {kvp.Key}={kvp.Value}");

        return sb.ToString();
    }

    private static bool ShouldEnableRemote(bool makeRemoteRequested)
    {
        _ = makeRemoteRequested;
        return ParserConfigs.GetRemoteAccess() && !string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey());
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
                Log.Error(ex, "[Terminal] Terminal error");
                exitCode = 1;
            }

            try { exitCode = terminal.ExitCode; } catch { }
        }

        TerminalResizeCoordinator.ClearSession(sessionId);
        await _stateService.CompleteSessionAsync(sessionId, exitCode);
        return exitCode;
    }

    /// <summary>
    /// CLI + Web concurrent path: creates terminal, wires Console I/O,
    /// registers with TerminalSessionService so web viewers can connect.
    /// </summary>
    public async Task<int> RunCliWithWebAsync(
        LLM llm, string workDir, string? envName, string[]? extraArgs,
        ITerminalSessionService sessionService, bool makeRemote = false, CancellationToken ct = default)
    {
        var (terminal, sessionId, remoteConn) = await CreateSessionAsync(llm, workDir, envName, extraArgs, ct, makeRemote: makeRemote);
        var exitCode = 0;

        await using (terminal)
        {
            terminal.Subscribe(new ConsoleOutputConsumer());

            // When a remote browser connects, disconnect the local WebUI viewer
            // so only one viewer is active at a time.
            if (remoteConn != null)
            {
                Log.Information("[Terminal] Remote connection established — wiring disconnect handler");
                remoteConn.OnReplayRequested += () =>
                {
                    Log.Information("[Terminal] OnReplayRequested fired — disconnecting local viewer");
                    _ = sessionService.DisconnectLocalViewerAsync("Session taken over by remote viewer");
                };
            }
            else
            {
                Log.Information("[Terminal] No remote connection — local disconnect handler NOT wired");
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
                Log.Error(ex, "[Terminal] Terminal error");
                exitCode = 1;
            }
            finally
            {
                await sessionService.UnregisterTerminalAsync();
            }

            try { exitCode = terminal.ExitCode; } catch { }
        }

        TerminalResizeCoordinator.ClearSession(sessionId);
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
                await TerminalIoRouter.RouteInputAsync(
                    _stateService,
                    terminal,
                    sessionId,
                    input,
                    TerminalIoSource.LocalCli,
                    ct);
            }
        }
    }
}
