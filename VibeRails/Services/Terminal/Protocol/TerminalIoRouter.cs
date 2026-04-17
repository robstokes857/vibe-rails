using System.Text;
using VibeRails.Services.UserInOut;

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
    DateTimeOffset TimestampUtc)
{
    /// <summary>
    /// Text normalized for analysis/logging (ANSI/control codes removed).
    /// </summary>
    public string PlainText => TerminalTextSanitizer.ToPlainText(Text);

    /// <summary>
    /// Raw text tokenized into plain/control segments.
    /// </summary>
    public IReadOnlyList<TerminalTextWithControlPart> TextWithControl =>
        TerminalTextSanitizer.ToTextWithControl(Text);

    /// <summary>
    /// True when the raw terminal payload contains ANSI/control data.
    /// </summary>
    public bool HasControl => TerminalTextSanitizer.HasControl(Text);
}

public readonly record struct TerminalSessionStartEvent(
    string SessionId,
    string Cli,
    string WorkDir,
    string? EnvName,
    IReadOnlyList<string> SetupCommands,
    string LaunchCommand,
    DateTimeOffset TimestampUtc);

public readonly record struct TerminalResizeEvent(
    string SessionId,
    TerminalIoSource Source,
    int Cols,
    int Rows,
    DateTimeOffset TimestampUtc);

public readonly record struct TerminalIdleEvent(
    string SessionId,
    string Cli,
    TimeSpan IdleFor,
    TimeSpan IdleThreshold,
    DateTimeOffset LastInputUtc,
    DateTimeOffset LastOutputUtc,
    DateTimeOffset TimestampUtc);

public readonly record struct TerminalSessionBusyEvent(
    string SessionId,
    string Cli,
    DateTimeOffset TimestampUtc);

public readonly record struct TerminalWaitingForUserEvent(
    string SessionId,
    string Cli,
    DateTimeOffset TimestampUtc);

public readonly record struct TerminalSessionCompleteEvent(
    string SessionId,
    string Cli,
    int? ExitCode,
    DateTimeOffset TimestampUtc);

public readonly record struct TerminalRemoteCommandEvent(
    string SessionId,
    TerminalIoSource Source,
    string Command,
    string? Payload,
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

        TUI_Event_Watcher.Watch(sessionId, input);

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

        stateService.LogOutput(sessionId, outputBytes, source);
    }
}
