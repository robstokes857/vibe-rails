using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public interface ITerminalStateService
{
    Task<string> CreateSessionAsync(string cli, string workDir, string? envName, bool makeRemote = false, CancellationToken ct = default);
    void LogOutput(string sessionId, string text, TerminalIoSource source = TerminalIoSource.Pty);
    void RecordInput(string sessionId, string input, TerminalIoSource source = TerminalIoSource.Unknown);
    void RecordResize(string sessionId, int cols, int rows, TerminalIoSource source);
    void RecordRemoteCommand(string sessionId, string command, string? payload, TerminalIoSource source = TerminalIoSource.RemoteWebUi);
    void TrackRemoteConnection(string sessionId, IRemoteTerminalConnection connection);
    Task<bool> SendRemoteCommandAsync(string sessionId, string command, string? payload = null, CancellationToken ct = default);
    Task RequestRemoteViewerDisconnectAsync(string sessionId, string reason);
    Task CompleteSessionAsync(string sessionId, int exitCode);
}

public class TerminalStateService : ITerminalStateService, IDisposable
{
    private readonly IDbService _dbService;
    private readonly IGitService _gitService;
    private readonly IRemoteStateService _remoteStateService;
    private readonly ITerminalIoObserverService _ioObserverService;

    // Shared across scoped TerminalStateService instances so terminal state remains
    // consistent across start/WS/reconnect/stop requests.
    private static readonly Dictionary<string, InputAccumulator> s_inputAccumulators = new();
    private static readonly Dictionary<string, IRemoteTerminalConnection> s_remoteConnections = new();
    private static readonly Dictionary<string, SessionActivityState> s_sessionActivity = new();
    private static readonly Lock s_stateLock = new();
    private static readonly TimeSpan s_idleThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_idleCheckInterval = TimeSpan.FromSeconds(5);

    public TerminalStateService(
        IDbService dbService,
        IGitService gitService,
        IRemoteStateService remoteStateService,
        ITerminalIoObserverService ioObserverService)
    {
        _dbService = dbService;
        _gitService = gitService;
        _remoteStateService = remoteStateService;
        _ioObserverService = ioObserverService;
    }

    public async Task<string> CreateSessionAsync(string cli, string workDir, string? envName, bool makeRemote = false, CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString();
        await _dbService.CreateSessionAsync(sessionId, cli, envName, workDir);

        var commitHash = await _gitService.GetCurrentCommitHashAsync(ct);
        await _dbService.InsertUserInputAsync(sessionId, 0, "[SESSION_START]", commitHash);

        var now = DateTimeOffset.UtcNow;
        lock (s_stateLock)
        {
            s_inputAccumulators[sessionId] = new InputAccumulator(async inputText =>
            {
                await _dbService.RecordUserInputAsync(sessionId, inputText, _gitService, ct);
            });
            s_sessionActivity[sessionId] = new SessionActivityState(now);
        }
        StartIdleMonitor(sessionId);

        // For now, any configured instance defaults to remote-enabled sessions.
        // Keep makeRemote in the signature so explicit per-session controls can be reintroduced later.
        if (ShouldRegisterRemoteSession(makeRemote))
        {
            await _remoteStateService.RegisterTerminalAsync(sessionId, cli, workDir, envName);
        }

        return sessionId;
    }

    public void LogOutput(string sessionId, string text, TerminalIoSource source = TerminalIoSource.Pty)
    {
        var now = DateTimeOffset.UtcNow;
        _ioObserverService.Publish(new TerminalIoEvent(
            sessionId,
            TerminalIoDirection.Output,
            source,
            text,
            now));
        MarkOutputActivity(sessionId, now);
        // Intentionally do not persist terminal output to SessionLogs for now.
        // Keep observer hook only; output persistence can be re-enabled centrally here.
    }

    public void RecordInput(string sessionId, string input, TerminalIoSource source = TerminalIoSource.Unknown)
    {
        var now = DateTimeOffset.UtcNow;
        _ioObserverService.Publish(new TerminalIoEvent(
            sessionId,
            TerminalIoDirection.Input,
            source,
            input,
            now));
        MarkInputActivity(sessionId, now);

        InputAccumulator? accumulator;
        lock (s_stateLock)
        {
            s_inputAccumulators.TryGetValue(sessionId, out accumulator);
        }

        if (accumulator != null)
            accumulator.Append(input);
    }

    public void RecordResize(string sessionId, int cols, int rows, TerminalIoSource source)
    {
        var now = DateTimeOffset.UtcNow;
        MarkGenericActivity(sessionId, now);
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
        await _dbService.CompleteSessionAsync(sessionId, exitCode);

        InputAccumulator? accumulatorToDispose;
        SessionActivityState? activityState;
        lock (s_stateLock)
        {
            s_inputAccumulators.TryGetValue(sessionId, out accumulatorToDispose);
            s_inputAccumulators.Remove(sessionId);
            s_sessionActivity.TryGetValue(sessionId, out activityState);
            s_sessionActivity.Remove(sessionId);
        }

        if (accumulatorToDispose != null)
        {
            await accumulatorToDispose.DisposeAsync();
        }

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
            await _remoteStateService.DeregisterTerminalAsync(sessionId);
        }
    }

    public void Dispose()
    {
        // Shared state is session-managed via CompleteSessionAsync.
    }

    private static bool ShouldRegisterRemoteSession(bool makeRemoteRequested)
    {
        _ = makeRemoteRequested;
        return ParserConfigs.GetRemoteAccess() && !string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey());
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
    }

    private static void MarkInputActivity(string sessionId, DateTimeOffset now)
    {
        lock (s_stateLock)
        {
            if (!s_sessionActivity.TryGetValue(sessionId, out var activity))
                return;

            activity.LastInputUtc = now;
            activity.LastActivityUtc = now;
            activity.IdleNotified = false;
        }
    }

    private static void MarkOutputActivity(string sessionId, DateTimeOffset now)
    {
        lock (s_stateLock)
        {
            if (!s_sessionActivity.TryGetValue(sessionId, out var activity))
                return;

            activity.LastOutputUtc = now;
            activity.LastActivityUtc = now;
            activity.IdleNotified = false;
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

    private sealed class SessionActivityState : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public SessionActivityState(DateTimeOffset now)
        {
            LastInputUtc = now;
            LastOutputUtc = now;
            LastActivityUtc = now;
        }

        public DateTimeOffset LastInputUtc { get; set; }
        public DateTimeOffset LastOutputUtc { get; set; }
        public DateTimeOffset LastActivityUtc { get; set; }
        public bool IdleNotified { get; set; }
        public CancellationToken Token => _cts.Token;

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
