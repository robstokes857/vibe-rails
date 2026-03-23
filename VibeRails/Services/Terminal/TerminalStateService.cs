using System.Threading.Channels;
using Serilog;
using VibeRails.Interfaces;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public interface ITerminalStateService
{
    Task<string> CreateSessionAsync(string cli, string workDir, string? envName, bool makeRemote = false, CancellationToken ct = default);
    void PublishSessionStart(string sessionId, string cli, string workDir, string? envName, IReadOnlyList<string> setupCommands, string launchCommand);
    void LogOutput(string sessionId, ReadOnlyMemory<byte> data, TerminalIoSource source = TerminalIoSource.Pty);
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
    private static readonly Dictionary<string, SessionOutputWriter> s_outputWriters = new();
    private static readonly Dictionary<string, IRemoteTerminalConnection> s_remoteConnections = new();
    private static readonly Dictionary<string, SessionActivityState> s_sessionActivity = new();
    private static readonly Lock s_stateLock = new();
    private static readonly TimeSpan s_idleThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_idleCheckInterval = TimeSpan.FromSeconds(2);

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

        var now = DateTimeOffset.UtcNow;
        lock (s_stateLock)
        {
            s_inputAccumulators[sessionId] = new InputAccumulator(async inputText =>
            {
                await _dbService.RecordUserInputAsync(sessionId, inputText, _gitService, ct);
            });
            s_outputWriters[sessionId] = new SessionOutputWriter(sessionId, _dbService);
            s_sessionActivity[sessionId] = new SessionActivityState(now);
        }
        StartIdleMonitor(sessionId);

        // For now, any configured instance defaults to remote-enabled sessions.
        // Keep makeRemote in the signature so explicit per-session controls can be reintroduced later.
        if (ShouldRegisterRemoteSession(makeRemote))
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
        MarkOutputActivity(sessionId, now);

        SessionOutputWriter? outputWriter;
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
        SessionOutputWriter? outputWriter;
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

        await _dbService.CompleteSessionAsync(sessionId, exitCode);

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
        catch (Exception ex)
        {
            Log.Warning(ex, "[TerminalState] Idle monitor loop failed for session {SessionId}", sessionId);
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

    private sealed class SessionOutputWriter : IAsyncDisposable
    {
        private readonly string _sessionId;
        private readonly IDbService _dbService;
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly Task _worker;
        private int _disposed;

        public SessionOutputWriter(string sessionId, IDbService dbService)
        {
            _sessionId = sessionId;
            _dbService = dbService;
            _worker = Task.Run(DrainAsync);
        }

        public void Enqueue(byte[] payload)
        {
            if (payload.Length == 0 || Volatile.Read(ref _disposed) == 1)
                return;

            _channel.Writer.TryWrite(payload);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            _channel.Writer.TryComplete();

            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TerminalState] Output writer shutdown failed for session {SessionId}", _sessionId);
            }
        }

        private async Task DrainAsync()
        {
            await foreach (var payload in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await _dbService.LogSessionOutputAsync(_sessionId, payload, false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[TerminalState] Failed to persist terminal output for session {SessionId}", _sessionId);
                }
            }
        }
    }
}
