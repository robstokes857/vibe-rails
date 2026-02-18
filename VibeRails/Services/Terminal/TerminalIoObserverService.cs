using Serilog;

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
}

public interface ITerminalIoObserverService
{
    void Publish(TerminalIoEvent ioEvent);
    void PublishResize(TerminalResizeEvent resizeEvent);
    void PublishIdle(TerminalIdleEvent idleEvent);
    void PublishRemoteCommand(TerminalRemoteCommandEvent commandEvent);
}

/// <summary>
/// Dispatches terminal I/O events to all registered ITerminalIoObserver implementations.
/// Dispatch is fire-and-forget so terminal I/O flow is never blocked by observers.
/// </summary>
public sealed class TerminalIoObserverService : ITerminalIoObserverService
{
    private readonly IReadOnlyList<ITerminalIoObserver> _observers;

    public TerminalIoObserverService(IEnumerable<ITerminalIoObserver> observers)
    {
        _observers = observers.ToList();
    }

    public void Publish(TerminalIoEvent ioEvent)
    {
        if (_observers.Count == 0)
            return;

        foreach (var observer in _observers)
        {
            _ = NotifyAsync(observer, ioEvent);
        }
    }

    public void PublishResize(TerminalResizeEvent resizeEvent)
    {
        if (_observers.Count == 0)
            return;

        foreach (var observer in _observers)
        {
            _ = NotifyResizeAsync(observer, resizeEvent);
        }
    }

    public void PublishIdle(TerminalIdleEvent idleEvent)
    {
        if (_observers.Count == 0)
            return;

        foreach (var observer in _observers)
        {
            _ = NotifyIdleAsync(observer, idleEvent);
        }
    }

    public void PublishRemoteCommand(TerminalRemoteCommandEvent commandEvent)
    {
        if (_observers.Count == 0)
            return;

        foreach (var observer in _observers)
        {
            _ = NotifyRemoteCommandAsync(observer, commandEvent);
        }
    }

    private static async Task NotifyAsync(ITerminalIoObserver observer, TerminalIoEvent ioEvent)
    {
        try
        {
            await observer.OnTerminalIoAsync(ioEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[TerminalIoObserverService] Observer error");
        }
    }

    private static async Task NotifyResizeAsync(ITerminalIoObserver observer, TerminalResizeEvent resizeEvent)
    {
        try
        {
            await observer.OnTerminalResizeAsync(resizeEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[TerminalIoObserverService] Resize observer error");
        }
    }

    private static async Task NotifyIdleAsync(ITerminalIoObserver observer, TerminalIdleEvent idleEvent)
    {
        try
        {
            await observer.OnTerminalIdleAsync(idleEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[TerminalIoObserverService] Idle observer error");
        }
    }

    private static async Task NotifyRemoteCommandAsync(ITerminalIoObserver observer, TerminalRemoteCommandEvent commandEvent)
    {
        try
        {
            await observer.OnTerminalRemoteCommandAsync(commandEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[TerminalIoObserverService] Remote command observer error");
        }
    }
}
