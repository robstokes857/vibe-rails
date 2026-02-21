using System.Collections.Concurrent;

namespace VibeRails.Services.Tracing;

/// <summary>
/// Singleton in-memory circular buffer for trace events.
/// Feeds the SSE endpoint and provides recent-event queries.
/// </summary>
public sealed class TraceEventBuffer
{
    private const int MaxEvents = 1000;

    private readonly ConcurrentQueue<TraceEvent> _events = new();
    private int _count;

    /// <summary>
    /// Fired (fire-and-forget) whenever a new event is added.
    /// SSE endpoint subscribes here to push events to connected clients.
    /// </summary>
    public event Action<TraceEvent>? OnEvent;

    public void Add(TraceEvent traceEvent)
    {
        _events.Enqueue(traceEvent);
        var current = Interlocked.Increment(ref _count);

        // Trim oldest events when over capacity
        while (current > MaxEvents && _events.TryDequeue(out _))
        {
            current = Interlocked.Decrement(ref _count);
        }

        OnEvent?.Invoke(traceEvent);
    }

    public List<TraceEvent> GetRecent(int count = 50)
    {
        return _events.Reverse().Take(count).Reverse().ToList();
    }
}
