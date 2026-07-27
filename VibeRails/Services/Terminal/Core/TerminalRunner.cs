using Serilog;
using VibeRails.Services.Terminal.Consumers;
using VibeRails.Services.AgentTools;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmProxy;

using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public class TerminalRunner
{
    private static int s_emergencyShutdownRequested;
    private readonly ITerminalStateService _stateService;
    private readonly ICommandService _commandService;
    private readonly ILocalToolApiContext _toolApiContext;
    private readonly ILlmProxySessionState _llmProxySessionState;
    private readonly IHostApplicationLifetime? _appLifetime;

    public TerminalRunner(
        ITerminalStateService stateService,
        ICommandService commandService,
        ILocalToolApiContext toolApiContext,
        ILlmProxySessionState llmProxySessionState,
        IHostApplicationLifetime? appLifetime = null)
    {
        _stateService = stateService;
        _commandService = commandService;
        _toolApiContext = toolApiContext;
        _llmProxySessionState = llmProxySessionState;
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
        string summary = "",
        Func<string, Task>? onRemoteTakeoverAuthorized = null,
        bool isNativeCli = false,
        string? initialUserInput = null,
        string? jobRunId = null)
    {
        // Plain shell sessions are shared remotely like any other session when remote is
        // enabled. A remote viewer of an agent session can already Ctrl+C out of the prompt
        // into the underlying shell, so a dedicated shell tab is no more sensitive to share.
        var shouldEnableRemote = ShouldEnableRemote(makeRemote, isNativeCli);
        // Web flow passes the env's CustomPrompt as initialPrompt and we record the
        // same text. CLI flow bakes the prompt into extraArgs so initialPrompt is
        // null; that path passes initialUserInput explicitly to record without
        // double-encoding the prompt into the launch command.
        var userInputToRecord = initialUserInput ?? initialPrompt;
        var sessionId = await _stateService.CreateSessionAsync(LlmParser.ToWireName(llm), workDir, envName, shouldEnableRemote, ct, initialUserInput: userInputToRecord, jobRunId: jobRunId);
        Terminal? terminal = null;
        IRemoteTerminalConnection? activeRemoteConn = null;
        IDisposable? openCodeProxyLease = null;

        try
        {
            var sessionTitle = ResolveSessionTitle(workDir, title);
            var preparedSession = await _commandService.PrepareSessionAsync(llm, envName, extraArgs, initialPrompt, summary);
            foreach (var kvp in _toolApiContext.BuildEnvironment(sessionId))
            {
                preparedSession.Environment[kvp.Key] = kvp.Value;
            }
            _stateService.PublishSessionStart(sessionId, LlmParser.ToWireName(llm), workDir, envName, preparedSession.SetupCommands, preparedSession.LaunchCommand);

            // A Job's PTY runs the CLI itself rather than an interactive shell we type into. The run
            // is over when the CLI exits, and a shell would simply return to its prompt — keeping
            // the PTY, and therefore the run, alive with nothing left to end it. Falls back to the
            // shell path when there is no program to spawn (a plain Shell session, or the fake-CLI
            // test harness, both of which are shell programs rather than program + argv).
            var spawnCliDirectly = jobRunId is not null && preparedSession.Executable is not null;
            if (spawnCliDirectly)
            {
                var (app, argv) = CliSpawnCommandBuilder.Build(
                    preparedSession.Executable!,
                    preparedSession.Argv ?? [],
                    preparedSession.SetupCommands);
                terminal = await Terminal.CreateAsync(
                    workDir, preparedSession.Environment, title: sessionTitle, ct: ct, app: app, argv: argv);
            }
            else
            {
                terminal = await Terminal.CreateAsync(workDir, preparedSession.Environment, title: sessionTitle, ct: ct);
            }
            if (preparedSession.OpenCodeProxyActive)
            {
                var lease = _llmProxySessionState.ActivateOpenCodeProxy();
                openCodeProxyLease = lease;
                terminal.Exited += (_, _) => lease.Dispose();
            }

            // Always wire up DB logging
            terminal.Subscribe(new DbLoggingConsumer(_stateService, sessionId));

            // Publish title changes on the output path rather than PTY stdin.
            if (!string.IsNullOrWhiteSpace(sessionTitle))
            {
                terminal.PublishOutput(System.Text.Encoding.UTF8.GetBytes($"\u001b]0;{sessionTitle}\u0007"));
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
                    // Deliberately persists for the whole session and is reset ONLY on a
                    // successful PIN. The disconnect / reconnect handlers re-lock the viewer but
                    // must NOT reset this, or an attacker could refill the guess budget by forcing
                    // a reconnect (network blip, idle timeout, output-overflow resync) or by
                    // disconnect/reconnect cycling — defeating the 3-strikes lockout.
                    var failedPinAttempts = 0;
                    var remoteOutputConsumer = new RemoteOutputConsumer(
                        remoteConn,
                        canForward: () => System.Threading.Volatile.Read(ref remoteViewerAuthorized) == 1);
                    terminal.Subscribe(remoteOutputConsumer);

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
                                            // Atomic snapshot delivery: the remote consumer is already
                                            // subscribed, so PushSnapshotTo hands it a fresh state dump
                                            // under the subscriber lock — no live bytes interleave, no
                                            // TUI poking required.
                                            terminal.PushSnapshotTo(remoteOutputConsumer);
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

                                // The receive loop forwards __PIN__: frames so the verifier
                                // above can consume them while locked. Once authorized, a
                                // stray one must be dropped here — routing it would type the
                                // user's PIN into the TUI as keystrokes.
                                if (TerminalControlProtocol.IsPinResponseFrame(bytes))
                                {
                                    Log.Warning("[PIN] Dropped stray PIN frame after authorization");
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
                                // Atomic snapshot delivery: PushSnapshotTo captures emulator state
                                // and hands it to the already-subscribed remote consumer under the
                                // terminal's subscriber lock, so no live bytes can interleave. The
                                // snapshot begins with ED2 + ED3 + cursor home so whatever the remote
                                // had on screen is wiped and rebuilt from ground truth.
                                terminal.PushSnapshotTo(remoteOutputConsumer);
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
                                // failedPinAttempts is intentionally NOT reset here — the lockout
                                // must survive a disconnect/reconnect so it can't be brute-forced.
                            }
                            finally
                            {
                                takeoverGate.Release();
                            }

                            Log.Information("[VibeRails] Remote viewer disconnected");

                            // Restore the native terminal title bar now that the remote viewer is gone.
                            terminal.PublishOutput(
                                System.Text.Encoding.UTF8.GetBytes($"\u001b]0;{sessionTitle}\u0007"));
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[Remote] Failed to process remote browser disconnect");
                        }
                    }

                    async Task HandleRemoteReconnectedAsync()
                    {
                        try
                        {
                            // The relay socket dropped and was re-established. The browser
                            // viewer (if any) lost its pairing during the gap, so treat the
                            // reconnect as a brand-new viewer: re-lock behind the PIN and
                            // force a fresh lock prompt. A correct PIN re-pushes the snapshot
                            // via the normal input path.
                            await takeoverGate.WaitAsync(CancellationToken.None);
                            try
                            {
                                System.Threading.Volatile.Write(
                                    ref remoteViewerAuthorized,
                                    RemoteConfig.IsPinConfigured ? 0 : 1);
                                remoteTakeoverNotified = false;
                                lockPromptSent = false;
                                // failedPinAttempts is intentionally NOT reset here — the lockout
                                // must survive a reconnect so it can't be brute-forced by forcing
                                // the relay socket to drop and re-establish.
                            }
                            finally
                            {
                                takeoverGate.Release();
                            }

                            if (RemoteConfig.IsPinConfigured)
                                await SendLockedAsync(force: true);
                            else
                                terminal.PushSnapshotTo(remoteOutputConsumer);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[Remote] Failed to handle reconnect");
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
                    remoteConn.OnReconnected += () =>
                    {
                        _ = HandleRemoteReconnectedAsync();
                    };
                    activeRemoteConn = remoteConn;
                }
                else
                {
                    await remoteConn.DisposeAsync();
                }
            }

            // Send the CLI command to the shell. Plain shell sessions have no command,
            // so we leave the shell at its prompt rather than typing a stray newline.
            // Direct-spawn sessions already ARE the CLI — there is no shell to type into.
            if (!spawnCliDirectly && !string.IsNullOrWhiteSpace(preparedSession.Command))
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

            openCodeProxyLease?.Dispose();

            await _stateService.CompleteSessionAsync(sessionId, -1);
            throw;
        }
    }

    /// <summary>
    /// Finalizes a session created by <see cref="CreateSessionAsync"/> when the caller owns the
    /// terminal's lifetime (the CLI and Job paths, which never register with
    /// <c>TerminalSessionService</c>). Drains and disposes the session's output writer and input
    /// accumulator, stamps EndedUTC / exit code, drops the shared per-session state, and tears down
    /// remote bookkeeping. Skipping this leaks process-lifetime state for every session created.
    /// </summary>
    public async Task CompleteSessionAsync(string sessionId, int exitCode)
    {
        TerminalResizeCoordinator.ClearSession(sessionId);
        await _stateService.CompleteSessionAsync(sessionId, exitCode);
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
        // RemoteConfig.IsEnabled is the single source of truth: remote access on, an API key
        // set, AND a PIN configured. Gate session enablement on the PIN here too — not just at
        // settings-save time. Otherwise clearing the PIN (DELETE /settings/pin leaves
        // RemoteAccess on) would keep serving remote sessions with remoteViewerAuthorized == 1,
        // i.e. no lock screen at all.
        return RemoteConfig.IsEnabled;
    }

    private void RequestEmergencyShutdown(string message)
    {
        if (Interlocked.Exchange(ref s_emergencyShutdownRequested, 1) == 1)
            return;

        ShutdownDiagnostics.RecordStopRequest(
            "TerminalRunner.EmergencyShutdown",
            $"message={message}; processId={Environment.ProcessId}");
        Log.Error("[PIN] {Message}", message);

        if (_appLifetime != null)
        {
            Log.Error("[PIN] Requesting host shutdown for emergency stop. processId={ProcessId}", Environment.ProcessId);
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
                Log.Error("[PIN] Force-killing process tree after emergency shutdown grace period. processId={ProcessId}", currentProcess.Id);
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

        await CompleteSessionAsync(sessionId, exitCode);
        return exitCode;
    }

    /// <summary>
    /// CLI + Web concurrent path: creates terminal, wires Console I/O,
    /// registers with TerminalSessionService so web viewers can connect.
    /// </summary>
    public async Task<int> RunCliWithWebAsync(
        LLM llm, string workDir, string? envName, string[]? extraArgs,
        ITerminalSessionService sessionService, bool makeRemote = false, CancellationToken ct = default,
        string? initialUserInput = null, string? jobRunId = null, Action<string>? onSessionCreated = null)
    {
        var (terminal, sessionId, remoteConn) = await CreateSessionAsync(
            llm,
            workDir,
            envName,
            extraArgs,
            ct,
            makeRemote: makeRemote,
            isNativeCli: true,
            initialUserInput: initialUserInput,
            jobRunId: jobRunId,
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
        // Job runs need the session id to link the run to its recording; surfaced here rather than
        // via a return value so the CLI path's signature stays unchanged for every other caller.
        onSessionCreated?.Invoke(sessionId);

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
            sessionService.RegisterExternalTerminal(terminal, sessionId, workDir);

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

        await CompleteSessionAsync(sessionId, exitCode);
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

    private static string ResolveSessionTitle(string workingDirectory, string? requestedTitle)
    {
        var title = string.IsNullOrWhiteSpace(requestedTitle)
            ? GetFolderName(workingDirectory)
            : requestedTitle.Trim();

        return StripTitleControlChars(title);
    }

    private static string GetFolderName(string workingDirectory)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(workingDirectory);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? workingDirectory : name;
        }
        catch
        {
            return string.IsNullOrWhiteSpace(workingDirectory) ? "Terminal" : workingDirectory;
        }
    }

    private static string StripTitleControlChars(string title)
    {
        var clean = new string(title.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Terminal" : clean;
    }
}
