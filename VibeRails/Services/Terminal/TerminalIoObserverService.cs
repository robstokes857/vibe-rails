namespace VibeRails.Services.Terminal;

/// <summary>
/// Implement this interface to receive terminal I/O events through DI.
/// Register implementations in DI, e.g. AddScoped&lt;ITerminalIoObserver, MyObserver&gt;().
/// </summary>
public interface ITerminalIoObserver
{
    ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default);
}

public interface ITerminalIoObserverService
{
    void Publish(TerminalIoEvent ioEvent);
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

    private static async Task NotifyAsync(ITerminalIoObserver observer, TerminalIoEvent ioEvent)
    {
        try
        {
            await observer.OnTerminalIoAsync(ioEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TerminalIoObserverService] Observer error: {ex.Message}");
        }
    }
}

