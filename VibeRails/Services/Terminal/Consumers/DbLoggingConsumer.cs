namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Logs PTY output to the database via TerminalStateService. Used by both CLI and Web paths.
/// </summary>
public sealed class DbLoggingConsumer : ITerminalConsumer
{
    private readonly ITerminalStateService _stateService;
    private readonly string _sessionId;

    public DbLoggingConsumer(ITerminalStateService stateService, string sessionId)
    {
        _stateService = stateService;
        _sessionId = sessionId;
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        var text = System.Text.Encoding.UTF8.GetString(data.Span);
        _stateService.LogOutput(_sessionId, text);
    }
}
