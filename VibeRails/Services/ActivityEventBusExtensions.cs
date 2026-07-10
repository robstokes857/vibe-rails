using VibeRails.DTOs;

namespace VibeRails.Interfaces;

/// <summary>
/// One-liner for feeding the shared "activity light" in the UI. Any subsystem that wants to show
/// it's doing something (an LLM proxy request, a background sync, a webhook, …) publishes a ping;
/// the browser blinks a single shared LED and aggregates recent pings by <c>source</c> (see
/// <c>wwwroot/js/modules/activity-blinker.js</c>). Keep <c>source</c> stable per subsystem so the
/// UI groups its pings together. Never pass secrets or payloads — this is display-only.
/// </summary>
public static class ActivityEventBusExtensions
{
    public const string ActivityEventType = "activity";

    public static void PublishActivity(
        this IAppEventBus bus,
        string source,
        string? label = null,
        string? target = null,
        string? status = null)
    {
        bus.Publish(
            ActivityEventType,
            new ActivityPingPayload(source, label, target, status),
            AppJsonSerializerContext.Default.ActivityPingPayload);
    }
}
