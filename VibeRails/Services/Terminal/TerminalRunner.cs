using Microsoft.Extensions.Hosting;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Terminal.Consumers;

using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public class TerminalRunner
{
    private static int s_emergencyShutdownRequested;
    private readonly ITerminalStateService _stateService;
    private readonly ICommandService _commandService;
    private readonly IHostApplicationLifetime? _appLifetime;

    public TerminalRunner(
        ITerminalStateService stateService,
        ICommandService commandService,
        IHostApplicationLifetime? appLifetime = null)
    {
        _stateService = stateService;
        _commandService = commandService;
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
        Func<string, Task>? onRemoteTakeoverAuthorized = null,
        bool isNativeCli = false)
    {
        var shouldEnableRemote = ShouldEnableRemote(makeRemote, isNativeCli);
        var sessionId = await _stateService.CreateSessionAsync(llm.ToString(), workDir, envName, shouldEnableRemote, ct);
        Terminal? terminal = null;
        IRemoteTerminalConnection? activeRemoteConn = null;

        try
        {
            var preparedSession = _commandService.PrepareSession(llm, envName, extraArgs, initialPrompt);
            _stateService.PublishSessionStart(sessionId, llm.ToString(), workDir, envName, preparedSession.SetupCommands, preparedSession.LaunchCommand);

            terminal = await Terminal.CreateAsync(workDir, preparedSession.Environment, title: title, ct: ct);

            // Always wire up DB logging
            terminal.Subscribe(new DbLoggingConsumer(_stateService, sessionId));

            // Publish title changes on the output path rather than PTY stdin.
            if (!string.IsNullOrWhiteSpace(title))
            {
                terminal.PublishOutput(System.Text.Encoding.UTF8.GetBytes($"\u001b]0;{title}\u0007"));
            }

            // For now, any configured instance defaults to remote-enabled sessions.
            // Keep makeRemote in the signature so explicit per-session controls can be reintroduced later.
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
                    var replayInProgress = 0; // 1 while GetGridReplay snapshot is being sent; live forward paused
                    terminal.Subscribe(new RemoteOutputConsumer(
                        remoteConn,
                        canForward: () =>
                            System.Threading.Volatile.Read(ref remoteViewerAuthorized) == 1 &&
                            System.Threading.Volatile.Read(ref replayInProgress) == 0));

                    async Task NotifyRemoteTakeoverAsync(string trigger)
                    {
                        if (remoteTakeoverNotified)
                            return;

                        remoteTakeoverNotified = true;

                        Log.Information("[VibeRails] Remote viewer connected");

                        // Update the native terminal's title bar so the local user knows someone is watching.
                        // Published via the output path (not PTY stdin) so ConPTY/the terminal emulator
                        // interprets the OSC title sequence rather than passing it to the shell as input.
                        terminal.PublishOutput(
                            System.Text.Encoding.UTF8.GetBytes("\u001b]0;\u26a0 REMOTE USER CONNECTED TO THIS SESSION\u0007"));

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

                    Task RequestTerminalRedrawAsync()
                        => terminal.WriteBytesAsync(new byte[] { 0x0C }, CancellationToken.None);

                    bool ShouldUseRedrawAttach()
                        => TerminalReplayPolicy.ShouldUseRedrawAttach(llm.ToString(), terminal.IsSyncOutputActive);

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
                                            if (ShouldUseRedrawAttach())
                                            {
                                                Log.Information(
                                                    "[Remote] Using redraw-first attach for {Cli} ({Reason})",
                                                    llm,
                                                    TerminalReplayPolicy.DescribeReason(llm.ToString(), terminal.IsSyncOutputActive));
                                            }
                                            else
                                            {
                                                System.Threading.Volatile.Write(ref replayInProgress, 1);
                                                try
                                                {
                                                    var pinReplay = terminal.GetGridReplay();
                                                    if (pinReplay.Length > 0)
                                                        await remoteConn.SendOutputAsync(pinReplay);
                                                }
                                                finally
                                                {
                                                    System.Threading.Volatile.Write(ref replayInProgress, 0);
                                                }
                                            }

                                            await RequestTerminalRedrawAsync();
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
                                if (ShouldUseRedrawAttach())
                                {
                                    Log.Information(
                                        "[Remote] Using redraw-first attach for {Cli} ({Reason})",
                                        llm,
                                        TerminalReplayPolicy.DescribeReason(llm.ToString(), terminal.IsSyncOutputActive));
                                }
                                else
                                {
                                    // Pause live forwarding so the replay snapshot is not interleaved
                                    // with PTY output that arrived after the snapshot was taken.
                                    System.Threading.Volatile.Write(ref replayInProgress, 1);
                                    try
                                    {
                                        var replay = terminal.GetGridReplay();
                                        if (replay.Length > 0)
                                            await remoteConn.SendOutputAsync(replay);
                                    }
                                    finally
                                    {
                                        System.Threading.Volatile.Write(ref replayInProgress, 0);
                                    }
                                }

                                await RequestTerminalRedrawAsync();
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

                    async Task HandleRemoteResizeAsync(int cols, int rows)
                    {
                        try
                        {
                            await takeoverGate.WaitAsync(CancellationToken.None);
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
                            finally
                            {
                                takeoverGate.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[Remote] Failed to resize PTY to {Cols}x{Rows}", cols, rows);
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

                            // Restore the native terminal title bar now that the remote viewer is gone.
                            terminal.PublishOutput(
                                System.Text.Encoding.UTF8.GetBytes("\u001b]0;VibeRails Terminal\u0007"));
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[Remote] Failed to process remote browser disconnect");
                        }
                    }

                    remoteConn.OnInputReceived += bytes =>
                        _ = HandleRemoteInputAsync(bytes);
                    remoteConn.OnResizeRequested += (cols, rows) =>
                        _ = HandleRemoteResizeAsync(cols, rows);
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
                    activeRemoteConn = remoteConn;
                }
                else
                {
                    await remoteConn.DisposeAsync();
                }
            }

            // Send the CLI command to the shell
            await terminal.SendCommandAsync(preparedSession.Command, ct);

            if (activeRemoteConn != null)
            {
                _stateService.TrackRemoteConnection(sessionId, activeRemoteConn);
            }

            return (terminal, sessionId, activeRemoteConn);
        }
        catch
        {
            if (activeRemoteConn != null)
            {
                try
                {
                    await activeRemoteConn.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Terminal] Failed to dispose remote connection during startup rollback");
                }
            }

            if (terminal != null)
            {
                try
                {
                    await terminal.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Terminal] Failed to dispose terminal during startup rollback");
                }
            }

            await _stateService.CompleteSessionAsync(sessionId, -1);
            throw;
        }
    }

    // TODO: re-enable once the interactive alerting layer is in place to notify
    // the local user when a remote viewer connects. Until then, remote access
    // for native CLI sessions is disabled regardless of config.
    // Web terminal sessions are unaffected — they pass isNativeCli: false.
    private const bool _nativeRemoteEnabled = false;

    private static bool ShouldEnableRemote(bool makeRemoteRequested, bool isNativeCli)
    {
        if (isNativeCli && !_nativeRemoteEnabled)
            return false;

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
        var (terminal, sessionId, _) = await CreateSessionAsync(llm, workDir, envName, extraArgs, ct, isNativeCli: true);
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
            isNativeCli: true,
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
        while (!ct.IsCancellationRequested && !terminal.HasExited)
        {
            if (Console.IsInputRedirected || !Console.KeyAvailable)
            {
                await Task.Delay(50, ct);
                continue;
            }

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
