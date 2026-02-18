using Serilog;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Starter observer for terminal hooks.
/// Implement your enrichment, analytics, or routing logic in the stub handlers.
/// </summary>
public sealed class MyTerminalObserver : ITerminalIoObserver
{
    public ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default)
    {
        return ioEvent.Direction switch
        {
            TerminalIoDirection.Input => OnInputAsync(ioEvent, cancellationToken),
            TerminalIoDirection.Output => OnOutputAsync(ioEvent, cancellationToken),
            _ => ValueTask.CompletedTask
        };
    }

    public ValueTask OnTerminalResizeAsync(TerminalResizeEvent resizeEvent, CancellationToken cancellationToken = default)
    {
        return OnResizeAsync(resizeEvent, cancellationToken);
    }

    public ValueTask OnTerminalIdleAsync(TerminalIdleEvent idleEvent, CancellationToken cancellationToken = default)
    {
        return OnIdleAsync(idleEvent, cancellationToken);
    }

    public ValueTask OnTerminalRemoteCommandAsync(TerminalRemoteCommandEvent commandEvent, CancellationToken cancellationToken = default)
    {
        return OnRemoteCommandAsync(commandEvent, cancellationToken);
    }

    private static ValueTask OnInputAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // Example: Send ioEvent.PlainText to your context service.
        return ValueTask.CompletedTask;
    }

    private static ValueTask OnOutputAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken)
    {
        _ = ioEvent;
        _ = cancellationToken;
        return ValueTask.CompletedTask;
    }

    private static ValueTask OnResizeAsync(TerminalResizeEvent resizeEvent, CancellationToken cancellationToken)
    {
        _ = resizeEvent;
        _ = cancellationToken;
        return ValueTask.CompletedTask;
    }

    private static ValueTask OnIdleAsync(TerminalIdleEvent idleEvent, CancellationToken cancellationToken)
    {
        _ = idleEvent;
        _ = cancellationToken;
        return ValueTask.CompletedTask;
    }

    private static ValueTask OnRemoteCommandAsync(TerminalRemoteCommandEvent commandEvent, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        switch (commandEvent.Command)
        {
            case "ping":
                Log.Information(
                    "[RemoteCmd] ping received. session={SessionId} source={Source}",
                    commandEvent.SessionId,
                    commandEvent.Source);
                break;

            case "context_hint":
                Log.Information(
                    "[RemoteCmd] context_hint received. session={SessionId} payload={Payload}",
                    commandEvent.SessionId,
                    commandEvent.Payload ?? string.Empty);
                break;

            default:
                Log.Information(
                    "[RemoteCmd] command={Command} session={SessionId} payload={Payload}",
                    commandEvent.Command,
                    commandEvent.SessionId,
                    commandEvent.Payload ?? string.Empty);
                break;
        }

        return ValueTask.CompletedTask;
    }
}
