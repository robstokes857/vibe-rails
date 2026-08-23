using Serilog;
using VibeRails.DB;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.UserInOut;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public class TerminalStateService : ITerminalStateService, IDisposable
{
    private readonly IRepository _repository;
    private readonly IGitService _gitService;
    private readonly IRemoteStateService _remoteStateService;
    private readonly ITerminalIoObserverService _ioObserverService;

    // Shared across scoped TerminalStateService instances so terminal state remains
    // consistent across start/WS/reconnect/stop requests.
    private static readonly Dictionary<string, InputAccumulator> s_inputAccumulators = new();
    private static readonly Dictionary<string, ISessionOutputWriter> s_outputWriters = new();
    private static readonly Dictionary<string, IRemoteTerminalConnection> s_remoteConnections = new();
    private static readonly Dictionary<string, SessionActivityState> s_sessionActivity = new();
    private static readonly Lock s_stateLock = new();
    private static readonly TimeSpan s_idleThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_idleCheckInterval = TimeSpan.FromSeconds(2);

    public TerminalStateService(
        IRepository repository,
        IGitService gitService,
        IRemoteStateService remoteStateService,
        ITerminalIoObserverService ioObserverService)
    {
        _repository = repository;
        _gitService = gitService;
        _remoteStateService = remoteStateService;
        _ioObserverService = ioObserverService;
    }

    public async Task<string> CreateSessionAsync(
        string cli,
        string workDir,
        string? envName,
        bool makeRemote = false,
        CancellationToken ct = default,
        string? initialUserInput = null,
        string? jobRunId = null,
        int initialCols = Terminal.DefaultCols,
        int initialRows = Terminal.DefaultRows)
    {
        var sessionId = Guid.NewGuid().ToString();
        await _repository.CreateSessionAsync(sessionId, cli, envName, workDir, Environment.ProcessId, jobRunId);

        // Record the env's initial message (if any) as the session's first user input,
        // so it shows up in UserInputs at sequence=1 alongside subsequent typed inputs
        // and anchors the git-diff capture window for file activity attribution.
        // Must run before InputAccumulator is wired so the sequence number is unambiguous.
        if (!string.IsNullOrWhiteSpace(initialUserInput))
        {
            await _repository.RecordUserInputAsync(sessionId, initialUserInput, _gitService, ct);
        }

        var now = DateTimeOffset.UtcNow;
        var outputWriter = new SessionOutputWriter(_repository);
        outputWriter.Initialize(sessionId, initialCols, initialRows);
        lock (s_stateLock)
        {
            s_inputAccumulators[sessionId] = new InputAccumulator(
                async inputText =>
                {
                    await _repository.RecordUserInputAsync(sessionId, inputText, _gitService, ct);
                },
                // VibeRails records Codex modified-Enter as LF and plain Enter as CR.
                // Keep LF buffered until CR so a multiline prompt is one history row.
                // This is session-specific; every other TUI retains its existing
                // input-recording semantics.
                lineFeedInsertsNewline: LlmParser.ParseValue(cli) == LLM.Codex);
            s_outputWriters[sessionId] = outputWriter;
            s_sessionActivity[sessionId] = new SessionActivityState(now, cli);
        }
        StartIdleMonitor(sessionId);

        // For now, any configured instance defaults to remote-enabled sessions.
        // Keep makeRemote in the signature so explicit per-session controls can be reintroduced later.
        // Automated Job sessions (jobRunId set) are NEVER exposed to the viberails.ai relay,
        // regardless of RemoteConfig — an unattended job must not be remotely attachable.
        if (ShouldRegisterRemoteSession(makeRemote, jobRunId))
        {
            _ = _remoteStateService.RegisterTerminalAsync(sessionId, cli, workDir, envName);
        }

        return sessionId;
    }

    public void PublishSessionStart(string sessionId, string cli, string workDir, string? envName, IReadOnlyList<string> setupCommands, string launchCommand)
    {
        _ioObserverService.PublishSessionStart(new TerminalSessionStartEvent(
            sessionId, cli, workDir, envName, setupCommands, launchCommand, DateTimeOffset.UtcNow));
    }

    public void LogOutput(string sessionId, ReadOnlyMemory<byte> data, TerminalIoSource source = TerminalIoSource.Pty)
    {
        var __lagSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
        var now = DateTimeOffset.UtcNow;
        // Observer gets a string (for PlainText/HasControl analysis) — UTF8 decode here is fine
        // because observers are for tracing/analysis, not for terminal state reconstruction.
        var text = System.Text.Encoding.UTF8.GetString(data.Span);
        _ioObserverService.Publish(new TerminalIoEvent(
            sessionId,
            TerminalIoDirection.Output,
            source,
            text,
            now));

        if (!Utils.TerminalOutputFilter.IsSpinnerNoise(text))
        {
            var busyOutputEvent = MarkOutputActivity(sessionId, now);
            if (busyOutputEvent.HasValue)
                _ioObserverService.PublishSessionBusy(busyOutputEvent.Value);
        }

        ISessionOutputWriter? outputWriter;
        lock (s_stateLock)
        {
            s_outputWriters.TryGetValue(sessionId, out outputWriter);
        }

        if (outputWriter == null)
        {
            Log.Warning("[TerminalState] Dropping PTY output for unknown or completed session {SessionId}", sessionId);
            return;
        }

        // DB gets the raw bytes — no encoding loss, no filtering. The per-session writer
        // serializes inserts so output order matches PTY order and shutdown can drain cleanly.
        outputWriter.Enqueue(data.ToArray());
        }
        finally
        {
            __lagSw.Stop();
            Log.Debug("[TypingLag] LogOutput bytes={Bytes} elapsedMs={ElapsedMs:F3}",
                data.Length, __lagSw.Elapsed.TotalMilliseconds);
        }
    }

    public void RecordInput(string sessionId, string input, TerminalIoSource source = TerminalIoSource.Unknown)
    {
        var __lagSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
        InputAccumulator? accumulator;
        string? cli;
        lock (s_stateLock)
        {
            s_inputAccumulators.TryGetValue(sessionId, out accumulator);
            cli = s_sessionActivity.TryGetValue(sessionId, out var activity)
                ? activity.Cli
                : null;
        }

        var now = DateTimeOffset.UtcNow;
        _ioObserverService.Publish(new TerminalIoEvent(
            sessionId,
            TerminalIoDirection.Input,
            source,
            input,
            now,
            cli));
        var busyInputEvent = MarkInputActivity(sessionId, now);
        if (busyInputEvent.HasValue)
            _ioObserverService.PublishSessionBusy(busyInputEvent.Value);

        if (accumulator != null)
            accumulator.Append(input);
        }
        finally
        {
            __lagSw.Stop();
            Log.Debug("[TypingLag] RecordInput chars={Chars} elapsedMs={ElapsedMs:F3}",
                input.Length, __lagSw.Elapsed.TotalMilliseconds);
        }
    }

    public void RecordResize(string sessionId, int cols, int rows, TerminalIoSource source)
    {
        var now = DateTimeOffset.UtcNow;
        MarkGenericActivity(sessionId, now);

        ISessionOutputWriter? outputWriter;
        lock (s_stateLock)
        {
            s_outputWriters.TryGetValue(sessionId, out outputWriter);
        }
        outputWriter?.NotifyResize(cols, rows);

        _ioObserverService.PublishResize(new TerminalResizeEvent(
            sessionId,
            source,
            cols,
            rows,
            now));
    }

    public void RecordRemoteCommand(string sessionId, string command, string? payload, TerminalIoSource source = TerminalIoSource.RemoteWebUi)
    {
        var now = DateTimeOffset.UtcNow;
        MarkGenericActivity(sessionId, now);
        _ioObserverService.PublishRemoteCommand(new TerminalRemoteCommandEvent(
            sessionId,
            source,
            command,
            payload,
            now));
    }

    public void TrackRemoteConnection(string sessionId, IRemoteTerminalConnection connection)
    {
        lock (s_stateLock)
        {
            s_remoteConnections[sessionId] = connection;
        }
    }

    public async Task<bool> SendRemoteCommandAsync(string sessionId, string command, string? payload = null, CancellationToken ct = default)
    {
        IRemoteTerminalConnection? remoteConn;
        lock (s_stateLock)
        {
            s_remoteConnections.TryGetValue(sessionId, out remoteConn);
        }

        if (remoteConn?.IsConnected != true)
            return false;

        await remoteConn.SendCommandAsync(command, payload);
        return true;
    }

    public async Task RequestRemoteViewerDisconnectAsync(string sessionId, string reason)
    {
        IRemoteTerminalConnection? remoteConn;
        lock (s_stateLock)
        {
            s_remoteConnections.TryGetValue(sessionId, out remoteConn);
        }

        if (remoteConn?.IsConnected != true)
            return;

        await remoteConn.SendControlAsync(TerminalControlProtocol.BuildDisconnectBrowserCommand(reason));
    }

    public async Task CompleteSessionAsync(string sessionId, int exitCode)
    {
        ISessionOutputWriter? outputWriter;
        InputAccumulator? accumulatorToDispose;
        SessionActivityState? activityState;
        lock (s_stateLock)
        {
            s_outputWriters.TryGetValue(sessionId, out outputWriter);
            s_outputWriters.Remove(sessionId);
            s_inputAccumulators.TryGetValue(sessionId, out accumulatorToDispose);
            s_inputAccumulators.Remove(sessionId);
            s_sessionActivity.TryGetValue(sessionId, out activityState);
            s_sessionActivity.Remove(sessionId);
        }

        if (outputWriter != null)
        {
            await outputWriter.DisposeAsync();
        }

        if (accumulatorToDispose != null)
        {
            await accumulatorToDispose.DisposeAsync();
        }

        await _repository.CompleteSessionAsync(sessionId, exitCode);

        activityState?.Dispose();

        // Disconnect remote WebSocket if active
        IRemoteTerminalConnection? remoteConn;
        lock (s_stateLock)
        {
            s_remoteConnections.TryGetValue(sessionId, out remoteConn);
            s_remoteConnections.Remove(sessionId);
        }

        if (remoteConn != null)
        {
            await remoteConn.DisposeAsync();
        }

        // Deregister terminal remotely if configured
        if (ParserConfigs.GetRemoteAccess() && !string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey()))
        {
            _ = _remoteStateService.DeregisterTerminalAsync(sessionId);
        }

        if (activityState != null)
        {
            _ioObserverService.PublishSessionComplete(new TerminalSessionCompleteEvent(
                sessionId, activityState.Cli, exitCode, DateTimeOffset.UtcNow));
        }
    }

    public void Dispose()
    {
        // Shared state is session-managed via CompleteSessionAsync.
    }

    private static bool ShouldRegisterRemoteSession(bool makeRemoteRequested, string? jobRunId = null)
    {
        _ = makeRemoteRequested;
        // Automated Job sessions are never remotely attachable, even when remote is fully configured.
        if (jobRunId is not null)
            return false;
        // Must match TerminalRunner.ShouldEnableRemote: only register a session remotely when
        // remote access is fully configured INCLUDING a PIN. (Deregistration below stays on the
        // broad check so cleanup still runs if the PIN was cleared mid-session.)
        return RemoteConfig.IsEnabled;
    }

    private void StartIdleMonitor(string sessionId)
    {
        CancellationToken token;
        lock (s_stateLock)
        {
            if (!s_sessionActivity.TryGetValue(sessionId, out var activity))
                return;

            token = activity.Token;
        }

        _ = Task.Run(async () => await IdleMonitorLoopAsync(sessionId, token));
    }

    private async Task IdleMonitorLoopAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(s_idleCheckInterval, ct);

                TerminalIdleEvent idleEvent = default;
                var shouldPublish = false;
                var now = DateTimeOffset.UtcNow;

                lock (s_stateLock)
                {
                    if (!s_sessionActivity.TryGetValue(sessionId, out var activity))
                        return;

                    var idleFor = now - activity.LastActivityUtc;
                    if (idleFor >= s_idleThreshold)
                    {
                        if (!activity.IdleNotified)
                        {
                            activity.IdleNotified = true;
                            shouldPublish = true;
                            idleEvent = new TerminalIdleEvent(
                                sessionId,
                                activity.Cli,
                                idleFor,
                                s_idleThreshold,
                                activity.LastInputUtc,
                                activity.LastOutputUtc,
                                now);
                        }
                    }
                    else if (activity.IdleNotified)
                    {
                        activity.IdleNotified = false;
                    }
                }

                if (shouldPublish)
                {
                    _ioObserverService.PublishIdle(idleEvent);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TerminalState] Idle monitor loop failed for session {SessionId}", sessionId);
        }
    }

    private static TerminalSessionBusyEvent? MarkInputActivity(string sessionId, DateTimeOffset now)
    {
        lock (s_stateLock)
        {
            if (!s_sessionActivity.TryGetValue(sessionId, out var activity))
                return null;

            activity.LastInputUtc = now;
            activity.LastActivityUtc = now;

            if (!activity.IdleNotified)
                return null;

            activity.IdleNotified = false;
            return new TerminalSessionBusyEvent(sessionId, activity.Cli, now);
        }
    }

    private static TerminalSessionBusyEvent? MarkOutputActivity(string sessionId, DateTimeOffset now)
    {
        lock (s_stateLock)
        {
            if (!s_sessionActivity.TryGetValue(sessionId, out var activity))
                return null;

            activity.LastOutputUtc = now;
            activity.LastActivityUtc = now;

            if (!activity.IdleNotified)
                return null;

            activity.IdleNotified = false;
            return new TerminalSessionBusyEvent(sessionId, activity.Cli, now);
        }
    }

    private static void MarkGenericActivity(string sessionId, DateTimeOffset now)
    {
        lock (s_stateLock)
        {
            if (!s_sessionActivity.TryGetValue(sessionId, out var activity))
                return;

            activity.LastActivityUtc = now;
            activity.IdleNotified = false;
        }
    }
}
