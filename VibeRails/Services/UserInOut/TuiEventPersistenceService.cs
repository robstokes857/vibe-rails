using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using VibeRails.DB;

namespace VibeRails.Services.UserInOut;

/// <summary>
/// Persists parsed TUI watcher events to SQLite without blocking the terminal input path.
/// </summary>
public sealed class TuiEventPersistenceService : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private IDisposable? _subscription;

    public TuiEventPersistenceService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = TUI_Event_Watcher.Subscribe(PersistAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
    }

    private async Task PersistAsync(TUI_Event_Watcher.TuiEvent tuiEvent)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
            await repository.InsertTuiEventAsync(
                tuiEvent.SessionId,
                tuiEvent.TimestampUtc,
                tuiEvent.Sequence,
                tuiEvent.EventType);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[TuiEventPersistence] Failed to persist {EventType} for session {SessionId}",
                tuiEvent.EventType,
                tuiEvent.SessionId);
        }
    }
}
