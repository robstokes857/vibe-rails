using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public interface ITerminalStateService
{
    Task<string> CreateSessionAsync(string cli, string workDir, string? envName, bool makeRemote = false, CancellationToken ct = default);
    void LogOutput(string sessionId, string text, TerminalIoSource source = TerminalIoSource.Pty);
    void RecordInput(string sessionId, string input, TerminalIoSource source = TerminalIoSource.Unknown);
    void TrackRemoteConnection(string sessionId, IRemoteTerminalConnection connection);
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
    private static readonly Lock s_stateLock = new();

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

        lock (s_stateLock)
        {
            s_inputAccumulators[sessionId] = new InputAccumulator(async inputText =>
            {
                await _dbService.RecordUserInputAsync(sessionId, inputText, _gitService, ct);
            });
        }

        // Register terminal remotely if configured AND user wants remote
        if (ParserConfigs.GetRemoteAccess() && !string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey()) && makeRemote)
        {
            await _remoteStateService.RegisterTerminalAsync(sessionId, cli, workDir, envName);
        }

        return sessionId;
    }

    public void LogOutput(string sessionId, string text, TerminalIoSource source = TerminalIoSource.Pty)
    {
        _ioObserverService.Publish(new TerminalIoEvent(
            sessionId,
            TerminalIoDirection.Output,
            source,
            text,
            DateTimeOffset.UtcNow));
        // Intentionally do not persist terminal output to SessionLogs for now.
        // Keep observer hook only; output persistence can be re-enabled centrally here.
    }

    public void RecordInput(string sessionId, string input, TerminalIoSource source = TerminalIoSource.Unknown)
    {
        _ioObserverService.Publish(new TerminalIoEvent(
            sessionId,
            TerminalIoDirection.Input,
            source,
            input,
            DateTimeOffset.UtcNow));

        InputAccumulator? accumulator;
        lock (s_stateLock)
        {
            s_inputAccumulators.TryGetValue(sessionId, out accumulator);
        }

        if (accumulator != null)
            accumulator.Append(input);
    }

    public void TrackRemoteConnection(string sessionId, IRemoteTerminalConnection connection)
    {
        lock (s_stateLock)
        {
            s_remoteConnections[sessionId] = connection;
        }
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
        lock (s_stateLock)
        {
            s_inputAccumulators.TryGetValue(sessionId, out accumulatorToDispose);
            s_inputAccumulators.Remove(sessionId);
        }

        if (accumulatorToDispose != null)
        {
            await accumulatorToDispose.DisposeAsync();
        }

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
}
