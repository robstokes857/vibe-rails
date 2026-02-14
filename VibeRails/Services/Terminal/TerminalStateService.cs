using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public interface ITerminalStateService
{
    Task<string> CreateSessionAsync(string cli, string workDir, string? envName, CancellationToken ct = default);
    void LogOutput(string sessionId, string text);
    void RecordInput(string sessionId, string input);
    void TrackRemoteConnection(string sessionId, IRemoteTerminalConnection connection);
    Task RequestRemoteViewerDisconnectAsync(string sessionId, string reason);
    Task CompleteSessionAsync(string sessionId, int exitCode);
}

public class TerminalStateService : ITerminalStateService, IDisposable
{
    private readonly IDbService _dbService;
    private readonly IGitService _gitService;
    private readonly IRemoteStateService _remoteStateService;

    // Shared across scoped TerminalStateService instances so terminal state remains
    // consistent across start/WS/reconnect/stop requests.
    private static readonly Dictionary<string, InputAccumulator> s_inputAccumulators = new();
    private static readonly Dictionary<string, IRemoteTerminalConnection> s_remoteConnections = new();
    private static readonly Lock s_stateLock = new();

    public TerminalStateService(IDbService dbService, IGitService gitService, IRemoteStateService remoteStateService)
    {
        _dbService = dbService;
        _gitService = gitService;
        _remoteStateService = remoteStateService;
    }

    public async Task<string> CreateSessionAsync(string cli, string workDir, string? envName, CancellationToken ct = default)
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

        // Register terminal remotely if configured
        if (ParserConfigs.GetRemoteAccess() && !string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey()))
        {
            await _remoteStateService.RegisterTerminalAsync(sessionId, cli, workDir, envName);
        }

        return sessionId;
    }

    public void LogOutput(string sessionId, string text)
    {
        if (!TerminalOutputFilter.IsTransient(text))
            _ = _dbService.LogSessionOutputAsync(sessionId, text, false);
    }

    public void RecordInput(string sessionId, string input)
    {
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

        await remoteConn.SendControlAsync(RemoteTerminalConnection.DisconnectBrowserControlMessage(reason));
    }

    public async Task CompleteSessionAsync(string sessionId, int exitCode)
    {
        await _dbService.CompleteSessionAsync(sessionId, exitCode);

        lock (s_stateLock)
        {
            s_inputAccumulators.Remove(sessionId);
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
