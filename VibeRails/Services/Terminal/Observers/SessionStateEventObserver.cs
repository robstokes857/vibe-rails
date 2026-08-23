using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Publishes session lifecycle state changes to IAppEventBus so connected browser
/// clients receive real-time session state updates. Only publishes metadata — no
/// raw terminal I/O content.
/// </summary>
public sealed class SessionStateEventObserver : ITerminalIoObserver
{
    private readonly IAppEventBus _eventBus;

    public SessionStateEventObserver(IAppEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default)
    {
        if (ioEvent.Direction != TerminalIoDirection.Input)
            return ValueTask.CompletedTask;

        var kind = ClassifyUserInput(ioEvent.Text, ioEvent.Cli);
        if (kind is null)
            return ValueTask.CompletedTask;

        // Metadata only: never publish raw terminal input. This lets tab status
        // leave WAITING for input that arrives through a CLI console, remote UI,
        // or another attached browser where the local xterm onData hook is not
        // the source of truth.
        _eventBus.Publish(
            "session_input",
            new SessionInputPayload(ioEvent.SessionId, kind, ioEvent.Source.ToString()),
            AppJsonSerializerContext.Default.SessionInputPayload);
        return ValueTask.CompletedTask;
    }

    private static string? ClassifyUserInput(string input, string? cli)
    {
        if (input is "\r" or "\r\n")
            return "submit";

        if (input == "\n" && !string.Equals(cli, "Codex", StringComparison.OrdinalIgnoreCase))
            return "submit";

        return null;
    }

    public ValueTask OnSessionStartAsync(TerminalSessionStartEvent startEvent, CancellationToken cancellationToken = default)
    {
        _eventBus.Publish(
            "session_started",
            new SessionStartedPayload(startEvent.SessionId, startEvent.Cli),
            AppJsonSerializerContext.Default.SessionStartedPayload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTerminalIdleAsync(TerminalIdleEvent idleEvent, CancellationToken cancellationToken = default)
    {
        _eventBus.Publish(
            "session_idle",
            new SessionIdlePayload(idleEvent.SessionId, idleEvent.Cli, idleEvent.IdleFor.TotalSeconds),
            AppJsonSerializerContext.Default.SessionIdlePayload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnSessionBusyAsync(TerminalSessionBusyEvent busyEvent, CancellationToken cancellationToken = default)
    {
        _eventBus.Publish(
            "session_busy",
            new SessionBusyPayload(busyEvent.SessionId, busyEvent.Cli),
            AppJsonSerializerContext.Default.SessionBusyPayload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnSessionCompleteAsync(TerminalSessionCompleteEvent completeEvent, CancellationToken cancellationToken = default)
    {
        _eventBus.Publish(
            "session_completed",
            new SessionCompletedPayload(completeEvent.SessionId, completeEvent.Cli, completeEvent.ExitCode),
            AppJsonSerializerContext.Default.SessionCompletedPayload);
        return ValueTask.CompletedTask;
    }
}
