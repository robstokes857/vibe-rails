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
