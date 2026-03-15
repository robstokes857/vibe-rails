using Microsoft.Extensions.Hosting;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Terminal.Consumers;
using VibeRails.Services.Tracing;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public class TerminalRunner
{
    private static int s_emergencyShutdownRequested;
    private readonly ITerminalStateService _stateService;
    private readonly ICommandService _commandService;
    private readonly TraceEventBuffer? _traceBuffer;
    private readonly IHostApplicationLifetime? _appLifetime;

    public TerminalRunner(
        ITerminalStateService stateService,
        ICommandService commandService,
        TraceEventBuffer? traceBuffer = null,
        IHostApplicationLifetime? appLifetime = null)
    {
        _stateService = stateService;
        _commandService = commandService;
        _traceBuffer = traceBuffer;
        _appLifetime = appLifetime;
    }

    /// <summary>
    /// Create a Terminal + DB session with DbLoggingConsumer already wired.
    /// Used by both CLI and Web paths. Returns the remote connection if one was established.
    /// </summary>
    public async Task<(Terminal terminal, string sessionId, IRemoteTerminalConnection? remoteConnection)> CreateSessionAsync(
        LLM llm,
        string workDir,
        string? envName,
        string[]? extraArgs,
        CancellationToken ct,
        string? title = null,
        bool makeRemote = false,
        string? initialPrompt = null,
        Func<string, Task>? onRemoteTakeoverAuthorized = null)
    {
        var shouldEnableRemote = ShouldEnableRemote(makeRemote);
        var sessionId = await _stateService.CreateSessionAsync(llm.ToString(), workDir, envName, shouldEnableRemote, ct);
        var preparedSession = _commandService.PrepareSession(llm, envName, extraArgs, initialPrompt);
        _stateService.PublishSessionStart(sessionId, llm.ToString(), workDir, envName, preparedSession.SetupCommands, preparedSession.LaunchCommand);
        EmitTerminalLaunchTrace(sessionId, llm, workDir, envName, extraArgs, title, shouldEnableRemote, initialPrompt, preparedSession);

        var terminal = await Terminal.CreateAsync(workDir, preparedSession.Environment, title: title, ct: ct);

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
                var takeoverGate = new SemaphoreSlim(1, 1);
                var remoteViewerAuthorized = RemoteConfig.IsPinConfigured ? 0 : 1;
                var remoteTakeoverNotified = false;
                var lockPromptSent = false;
                var failedPinAttempts = 0;
                terminal.Subscribe(new RemoteOutputConsumer(
                    remoteConn,
                    canForward: () => System.Threading.Volatile.Read(ref remoteViewerAuthorized) == 1));

                async Task NotifyRemoteTakeoverAsync(string trigger)
                {
                    if (remoteTakeoverNotified)
                        return;

                    remoteTakeoverNotified = true;

                    Log.Information("[VibeRails] Remote viewer connected");

                    if (onRemoteTakeoverAuthorized == null)
                        return;

                    try
                    {
                        await onRemoteTakeoverAuthorized(trigger);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Terminal] Remote takeover callback failed");
                    }
                }

                async Task SendLockedAsync(bool force = false)
                {
                    if (!force && lockPromptSent)
                        return;

                    lockPromptSent = true;
                    await remoteConn.SendControlAsync(TerminalControlProtocol.Locked);
                }

                async Task HandleLockoutAsync()
                {
                    await remoteConn.SendControlAsync(
                        TerminalControlProtocol.BuildDisconnectBrowserCommand("Too many failed PIN attempts"));

                    RequestEmergencyShutdown("3 failed pins.. Killing app.");
                }

                async Task HandleRemoteInputAsync(byte[] bytes)
                {
                    try
                    {
                        await takeoverGate.WaitAsync(CancellationToken.None);
                        try
                        {
                            var pinRequired = RemoteConfig.IsPinConfigured;
                            if (!pinRequired)
                                System.Threading.Volatile.Write(ref remoteViewerAuthorized, 1);

                            if (pinRequired && System.Threading.Volatile.Read(ref remoteViewerAuthorized) == 0)
                            {
                                var text = System.Text.Encoding.UTF8.GetString(bytes).Trim();
                                if (text.StartsWith(TerminalControlProtocol.PinResponse, StringComparison.Ordinal))
                                {
                                    var pin = text[TerminalControlProtocol.PinResponse.Length..].Trim();
                                    var isPinFormatValid = pin.Length is >= 4 and <= 6 && pin.All(char.IsDigit);
                                    var verified = isPinFormatValid && RemoteConfig.VerifyPin(pin);

                                    if (verified)
                                    {
                                        System.Threading.Volatile.Write(ref remoteViewerAuthorized, 1);
                                        failedPinAttempts = 0;
                                        lockPromptSent = false;
                                        Log.Information("[PIN] Remote PIN verified successfully");

                                        await remoteConn.SendControlAsync(TerminalControlProtocol.Unlocked);
                                        await NotifyRemoteTakeoverAsync("pin");
                                        await terminal.WriteBytesAsync(new byte[] { 0x0C }, CancellationToken.None); // Ctrl+L
                                        return;
                                    }

                                    failedPinAttempts++;
                                    Log.Warning(
                                        "[PIN] Remote PIN rejected, attempt {Attempt}/{Max}",
                                        failedPinAttempts,
                                        3);

                                    if (failedPinAttempts >= 3)
                                    {
                                        await HandleLockoutAsync();
                                        return;
                                    }

                                    await SendLockedAsync(force: true);
                                    return;
                                }

                                await SendLockedAsync();
                                return;
                            }

                            await NotifyRemoteTakeoverAsync("input");
                            await TerminalIoRouter.RouteInputAsync(
                                _stateService,
                                terminal,
                                sessionId,
                                bytes,
                                TerminalIoSource.RemoteWebUi,
                                CancellationToken.None);
                        }
                        finally
                        {
                            takeoverGate.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Remote] Failed to handle remote input");
                    }
                }

                async Task HandleRemoteReplayRequestAsync()
                {
                    try
                    {
                        await takeoverGate.WaitAsync(CancellationToken.None);
                        try
                        {
                            var pinRequired = RemoteConfig.IsPinConfigured;
                            if (!pinRequired)
                                System.Threading.Volatile.Write(ref remoteViewerAuthorized, 1);

                            if (pinRequired && System.Threading.Volatile.Read(ref remoteViewerAuthorized) == 0)
                            {
                                await SendLockedAsync();
                                return;
                            }

                            await NotifyRemoteTakeoverAsync("replay");
                            var replay = terminal.GetGridReplay();
                            if (replay.Length > 0)
                                await remoteConn.SendOutputAsync(replay);
                        }
                        finally
                        {
                            takeoverGate.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Remote] Failed to handle replay request");
                    }
                }

                async Task HandleRemoteBrowserDisconnectedAsync()
                {
                    try
                    {
                        await takeoverGate.WaitAsync(CancellationToken.None);
                        try
                        {
                            System.Threading.Volatile.Write(
                                ref remoteViewerAuthorized,
                                RemoteConfig.IsPinConfigured ? 0 : 1);
                            remoteTakeoverNotified = false;
                            lockPromptSent = false;
                            failedPinAttempts = 0;
                        }
                        finally
                        {
                            takeoverGate.Release();
                        }

                        Log.Information("[VibeRails] Remote viewer disconnected");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Remote] Failed to process remote browser disconnect");
                    }
                }

                remoteConn.OnInputReceived += bytes =>
                    _ = HandleRemoteInputAsync(bytes);
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
                    _ = HandleRemoteReplayRequestAsync();
                };
                remoteConn.OnBrowserDisconnected += () =>
                {
                    _ = HandleRemoteBrowserDisconnectedAsync();
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
        await terminal.SendCommandAsync(preparedSession.Command, ct);

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
        string? initialPrompt,
        PreparedTerminalSession preparedSession)
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
            initialPrompt,
            preparedSession);

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
        string? initialPrompt,
        PreparedTerminalSession preparedSession)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"sessionId: {sessionId}");
        sb.AppendLine($"llm: {llm}");
        sb.AppendLine($"workDir: {workDir}");
        sb.AppendLine($"environmentName: {envName ?? "(default)"}");
        sb.AppendLine($"title: {title ?? "(none)"}");
        sb.AppendLine($"remoteEnabled: {remoteEnabled}");
        sb.AppendLine($"initialPrompt: {initialPrompt ?? "(none)"}");
        sb.AppendLine($"shell: {Terminal.GetDefaultShellPath()}");
        sb.AppendLine($"ptyCols: {Terminal.DefaultCols}");
        sb.AppendLine($"ptyRows: {Terminal.DefaultRows}");
        sb.AppendLine($"emulatorScrollback: 1000 rows");
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

        sb.AppendLine("setupCommands:");

        if (preparedSession.SetupCommands.Count > 0)
        {
            foreach (var setupCommand in preparedSession.SetupCommands)
                sb.AppendLine($"  - {setupCommand}");
        }
        else
        {
            sb.AppendLine("  - (none)");
        }

        sb.AppendLine("launchCommand:");
        sb.AppendLine($"  {preparedSession.LaunchCommand}");
        sb.AppendLine("fullCommand:");
        sb.AppendLine($"  {preparedSession.Command}");
        sb.AppendLine("environment:");

        foreach (var kvp in preparedSession.Environment.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.AppendLine($"  {kvp.Key}={kvp.Value}");

        return sb.ToString();
    }

    private static bool ShouldEnableRemote(bool makeRemoteRequested)
    {
        _ = makeRemoteRequested;
        return ParserConfigs.GetRemoteAccess() && !string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey());
    }

    private void RequestEmergencyShutdown(string message)
    {
        if (Interlocked.Exchange(ref s_emergencyShutdownRequested, 1) == 1)
            return;

        Log.Error("[PIN] {Message}", message);

        if (_appLifetime != null)
        {
            _appLifetime.StopApplication();
        }
        else
        {
            Log.Error("[PIN] IHostApplicationLifetime unavailable; app shutdown could not be requested.");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Give graceful shutdown a brief chance, then force-kill process tree.
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                try
                {
                    currentProcess.Kill(entireProcessTree: true);
                }
                catch (PlatformNotSupportedException)
                {
                    currentProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[PIN] Failed to force-kill process tree after lockout");
                Environment.FailFast("Emergency shutdown failed after repeated PIN lockout.", ex);
            }
        });
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
        var (terminal, sessionId, remoteConn) = await CreateSessionAsync(
            llm,
            workDir,
            envName,
            extraArgs,
            ct,
            makeRemote: makeRemote,
            onRemoteTakeoverAuthorized: trigger =>
            {
                // Native CLI coexists with remote viewer — both can run concurrently.
                // The connect/disconnect notifications are published to the PTY via
                // NotifyRemoteTakeoverAsync / HandleRemoteBrowserDisconnectedAsync.
                // Only the local web UI viewer (if any) is disconnected so the remote
                // browser gets exclusive web access.
                Log.Information(
                    "[Terminal] Remote viewer authorized via {Trigger} — disconnecting local web viewer",
                    trigger);
                return sessionService.DisconnectLocalViewerAsync("Session taken over by remote viewer");
            });
        var exitCode = 0;

        await using (terminal)
        {
            terminal.Subscribe(new ConsoleOutputConsumer());

            if (remoteConn != null)
            {
                Log.Information("[Terminal] Remote connection established — native CLI coexists with remote viewer");
            }
            else
            {
                Log.Information("[Terminal] No remote connection — local disconnect handler NOT wired");
            }

            terminal.StartReadLoop();

            // Register so web UI can find this terminal
            sessionService.RegisterExternalTerminal(terminal, sessionId, llm.ToString());

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
