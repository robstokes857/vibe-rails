using VibeRails.DTOs;

namespace VibeRails.Interfaces;

/// <summary>
/// Feeds the proxy/token-saver light in the UI. This event is deliberately proxy-specific: only
/// an authenticated, enabled LLM relay that actually reached its upstream should publish it.
/// Keep <c>source</c> stable per provider and never pass secrets, request bodies, or query strings.
/// </summary>
public static class ActivityEventBusExtensions
{
    public const string ProxyActivityEventType = "proxy_activity";

    public static void PublishProxyActivity(
        this IAppEventBus bus,
        string source,
        string? label = null,
        string? target = null,
        string? status = null,
        long? bytesSaved = null,
        long? tokensSavedTotal = null,
        long? tokensSavedSession = null,
        long? tokensSavedMonth = null)
    {
        bus.Publish(
            ProxyActivityEventType,
            new ProxyActivityPingPayload(
                source, label, target, status, bytesSaved,
                tokensSavedTotal, tokensSavedSession, tokensSavedMonth),
            AppJsonSerializerContext.Default.ProxyActivityPingPayload);
    }
}
