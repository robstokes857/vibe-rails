using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public interface ITerminalStateService
{
    Task<string> CreateSessionAsync(string cli, string workDir, string? envName, CancellationToken ct = default);
    void LogOutput(string sessionId, string text);
    void RecordInput(string sessionId, string input);
    Task CompleteSessionAsync(string sessionId, int exitCode);
}

public class TerminalStateService : ITerminalStateService, IDisposable
{
    private readonly IDbService _dbService;
    private readonly IGitService _gitService;
    private readonly Dictionary<string, InputAccumulator> _inputAccumulators = new();

    public TerminalStateService(IDbService dbService, IGitService gitService)
    {
        _dbService = dbService;
        _gitService = gitService;
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

    public async Task CompleteSessionAsync(string sessionId, int exitCode)
    {
        await _dbService.CompleteSessionAsync(sessionId, exitCode);
        _inputAccumulators.Remove(sessionId);
    }

    public void Dispose()
    {
        _inputAccumulators.Clear();
    }
}
