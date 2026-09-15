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
    Pty = 4,
    AgentTool = 5
}

public readonly record struct TerminalIoEvent(
    string SessionId,
    TerminalIoDirection Direction,
    TerminalIoSource Source,
    string Text,
    DateTimeOffset TimestampUtc,
    string? Cli = null)
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
    /// <summary>
    /// Routes a known physical Escape key, retaining the logical ESC for input
    /// observers while Terminal selects the platform-specific encoding.
    /// </summary>
    public static async Task RouteEscapeKeyAsync(
        ITerminalStateService stateService,
        Terminal terminal,
        string sessionId,
        TerminalIoSource source,
        CancellationToken ct = default)
    {
        await terminal.RunInputBatchAsync(async (writer, token) =>
        {
            stateService.RecordInput(sessionId, "\u001b", source);
            await writer.WriteEscapeKeyAsync(token);
        }, ct);
    }

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

        // History/observer analysis above uses decoded text, but the PTY path must
        // retain the caller's original bytes. Terminal owns both the stateful Codex
        // rewrite and write serialization across local, remote, and tool sources.
        await terminal.RunInputBatchAsync(async (writer, token) =>
        {
            stateService.RecordInput(sessionId, input, source);
            await writer.WriteInputBytesAsync(inputBytes, token);
        }, ct);
    }

    /// <summary>Routes/logs a sequence under the same stdin gate every input source uses.</summary>
    internal static Task RouteInputBatchAsync(ITerminalStateService stateService, Terminal terminal,
        string sessionId, TerminalIoSource source,
        Func<Func<string, CancellationToken, Task>, Func<CancellationToken, Task>, CancellationToken, Task> action,
        CancellationToken ct = default) =>
        terminal.RunInputBatchAsync((writer, token) => action(
            async (text, writeToken) =>
            {
                if (string.IsNullOrEmpty(text)) return;
                stateService.RecordInput(sessionId, text, source);
                await writer.WriteInputBytesAsync(Encoding.UTF8.GetBytes(text), writeToken);
            },
            async writeToken =>
            {
                stateService.RecordInput(sessionId, "\u001b", source);
                await writer.WriteEscapeKeyAsync(writeToken);
            }, token), ct);

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
