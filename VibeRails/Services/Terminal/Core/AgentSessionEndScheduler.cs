using Serilog;

namespace VibeRails.Services.Terminal;

/// <summary>A process-local, one-shot grace period for the current agent's explicit completion.</summary>
public sealed class AgentSessionEndScheduler : IDisposable
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly Func<string, bool> isCurrent;
    private readonly Func<string, Task<bool>> stop;
    private readonly TimeProvider time;
    private ITimer? timer;
    private string? scheduledSession;
    private DateTimeOffset due;
    private bool disposed;

    public AgentSessionEndScheduler(IServiceScopeFactory scopes) : this(
        id => { using var scope = scopes.CreateScope(); return scope.ServiceProvider.GetRequiredService<ITerminalSessionService>().ActiveSessionId == id; },
        async id => { using var scope = scopes.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ITerminalSessionService>().CompleteAgentSessionAsync(id); },
        TimeProvider.System) { }

    internal AgentSessionEndScheduler(Func<string, bool> isCurrent, Func<string, Task<bool>> stop, TimeProvider time)
    {
        this.isCurrent = isCurrent; this.stop = stop; this.time = time;
    }

    /// <summary>Repeat calls keep the first deadline. A replacement session never inherits a stop.</summary>
    public DateTimeOffset? Schedule(string sessionId)
    {
        lock (gate)
        {
            if (disposed || !isCurrent(sessionId)) return null;
            if (scheduledSession == sessionId) return due;
            timer?.Dispose();
            scheduledSession = sessionId;
            due = time.GetUtcNow() + GracePeriod;
            timer = time.CreateTimer(_ => { _ = StopAsync(sessionId); }, null, GracePeriod, Timeout.InfiniteTimeSpan);
            return due;
        }
    }

    private async Task StopAsync(string sessionId)
    {
        lock (gate) { if (disposed || scheduledSession != sessionId) return; }
        try { await stop(sessionId); }
        catch (Exception error) { Log.Error(error, "[Terminal] Agent-requested completion failed for {SessionId}", sessionId); }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; timer?.Dispose(); timer = null; }
    }
}
