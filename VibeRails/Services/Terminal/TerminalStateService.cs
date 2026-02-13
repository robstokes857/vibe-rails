using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public interface ITerminalStateService
{
    Task<string> CreateSessionAsync(string cli, string workDir, string? envName, CancellationToken ct = default);
    void LogOutput(string sessionId, string text);
    void RecordInput(string sessionId, string input);
    void TrackRemoteConnection(string sessionId, IRemoteTerminalConnection connection);
    Task CompleteSessionAsync(string sessionId, int exitCode);
}

public class TerminalStateService : ITerminalStateService, IDisposable
{
    private readonly IDbService _dbService;
    private readonly IGitService _gitService;
    private readonly IRemoteStateService _remoteStateService;
    private readonly Dictionary<string, InputAccumulator> _inputAccumulators = new();
    private readonly Dictionary<string, IRemoteTerminalConnection> _remoteConnections = new();

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

        _inputAccumulators[sessionId] = new InputAccumulator(async inputText =>
        {
            await _dbService.RecordUserInputAsync(sessionId, inputText, _gitService, ct);
        });

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
        if (_inputAccumulators.TryGetValue(sessionId, out var accumulator))
            accumulator.Append(input);
    }

    public void TrackRemoteConnection(string sessionId, IRemoteTerminalConnection connection)
    {
        _remoteConnections[sessionId] = connection;
    }

    public async Task CompleteSessionAsync(string sessionId, int exitCode)
    {
        await _dbService.CompleteSessionAsync(sessionId, exitCode);
        _inputAccumulators.Remove(sessionId);

        // Disconnect remote WebSocket if active
        if (_remoteConnections.Remove(sessionId, out var remoteConn))
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
        _inputAccumulators.Clear();
    }
}
