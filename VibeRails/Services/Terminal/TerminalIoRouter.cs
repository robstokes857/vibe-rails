using System.Text;

namespace VibeRails.Services.Terminal;

public enum TerminalIoDirection
{
    Input = 0,
    Output = 1
}

public enum TerminalIoSource
{
    Unknown = 0,
    LocalCli = 1,
    LocalWebUi = 2,
    RemoteWebUi = 3,
    Pty = 4
}

public readonly record struct TerminalIoEvent(
    string SessionId,
    TerminalIoDirection Direction,
    TerminalIoSource Source,
    string Text,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Centralized terminal I/O routing point. All user input and PTY output can be
/// funneled through this class so future hooks only need one integration point.
/// </summary>
public static class TerminalIoRouter
{
    public static async Task RouteInputAsync(
        ITerminalStateService stateService,
        Terminal terminal,
        string sessionId,
        string input,
        TerminalIoSource source,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(input))
            return;

        var bytes = Encoding.UTF8.GetBytes(input);
        await RouteInputAsync(stateService, terminal, sessionId, bytes, source, ct);
    }

    public static async Task RouteInputAsync(
        ITerminalStateService stateService,
        Terminal terminal,
        string sessionId,
        ReadOnlyMemory<byte> inputBytes,
        TerminalIoSource source,
        CancellationToken ct = default)
    {
        if (inputBytes.IsEmpty)
            return;

        var input = Encoding.UTF8.GetString(inputBytes.Span);
        if (input.Length == 0)
            return;

        stateService.RecordInput(sessionId, input, source);
        await terminal.WriteBytesAsync(inputBytes, ct);
    }

    public static void RouteOutput(
        ITerminalStateService stateService,
        string sessionId,
        ReadOnlyMemory<byte> outputBytes,
        TerminalIoSource source = TerminalIoSource.Pty)
    {
        if (outputBytes.IsEmpty)
            return;

        var output = Encoding.UTF8.GetString(outputBytes.Span);
        if (output.Length == 0)
            return;

        stateService.LogOutput(sessionId, output, source);
    }
}
