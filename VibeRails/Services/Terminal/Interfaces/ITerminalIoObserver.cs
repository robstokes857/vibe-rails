namespace VibeRails.Services.Terminal;

/// <summary>
/// Implement this interface to receive terminal I/O events through DI.
/// Register implementations in DI, e.g. AddScoped&lt;ITerminalIoObserver, MyObserver&gt;().
/// </summary>
public interface ITerminalIoObserver
{
    ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default);

    ValueTask OnTerminalResizeAsync(TerminalResizeEvent resizeEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    ValueTask OnTerminalIdleAsync(TerminalIdleEvent idleEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    ValueTask OnTerminalRemoteCommandAsync(TerminalRemoteCommandEvent commandEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    ValueTask OnSessionStartAsync(TerminalSessionStartEvent startEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    ValueTask OnSessionBusyAsync(TerminalSessionBusyEvent busyEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    ValueTask OnSessionCompleteAsync(TerminalSessionCompleteEvent completeEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
