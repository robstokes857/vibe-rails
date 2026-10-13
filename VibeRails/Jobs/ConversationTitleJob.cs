using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.DB;
using VibeRails.Interfaces;

namespace VibeRails.Jobs;

/// <summary>Drains title milestones while the root backend is open.</summary>
public sealed class ConversationTitleJob(ILogger<ConversationTitleJob> logger,
    ISystemResourceService resources, IServiceScopeFactory scopes, IConversationTitleStore store,
    IAppEventBus events) : JobBase(logger, resources)
{
    private string? changeStamp;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(5);

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ConversationTitleService>().ProcessOneAsync(cancellationToken);
        // Another root can own the request for this window's session. Observe the shared stamp
        // even when this root's remote settings are off; refreshing local labels sends no data.
        var stamp = await store.GetChangeStampAsync(cancellationToken);
        if (stamp == changeStamp) return;
        changeStamp = stamp;
        events.Publish("session_title_updated", new ConversationTitleChanged(null),
            ConversationTitleJsonContext.Default.ConversationTitleChanged);
    }
}
