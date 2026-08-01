using System.Globalization;
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
    public const string TokenSaverPauseEventType = "token_saver_pause";

    /// <summary>
    /// Announces that the token saver was paused or resumed for this process's tab. Separate from
    /// <see cref="ProxyActivityEventType"/> on purpose: a pause is not proxy traffic, and folding it
    /// into that event would inflate the meter's request count and pulse it as if a relay had run.
    /// </summary>
    /// <param name="pausedUntilUtc">Absolute expiry, or null for "not paused".</param>
    public static void PublishTokenSaverPause(
        this IAppEventBus bus,
        DateTimeOffset? pausedUntilUtc,
        bool saverEnabled)
    {
        bus.Publish(
            TokenSaverPauseEventType,
            new TokenSaverPausePayload(
                pausedUntilUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                saverEnabled),
            AppJsonSerializerContext.Default.TokenSaverPausePayload);
    }

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
