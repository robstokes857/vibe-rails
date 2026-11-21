using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>Which hosted feature families the account may use. The JSON keys are the hosted wire names.</summary>
public sealed record ProFeatureFlags(
    [property: JsonPropertyName(ProFeatures.RemoteBoards)] bool RemoteBoards,
    [property: JsonPropertyName(ProFeatures.RemoteTerminals)] bool RemoteTerminals,
    [property: JsonPropertyName(ProFeatures.SessionSharing)] bool SessionSharing,
    [property: JsonPropertyName(ProFeatures.CardSharing)] bool CardSharing)
{
    public static readonly ProFeatureFlags All = new(true, true, true, true);
    public static readonly ProFeatureFlags None = new(false, false, false, false);

    /// <summary>A name this version does not know is never blocked here; the server decides.</summary>
    public bool Allows(string feature) => feature switch
    {
        ProFeatures.RemoteBoards => RemoteBoards,
        ProFeatures.RemoteTerminals => RemoteTerminals,
        ProFeatures.SessionSharing => SessionSharing,
        ProFeatures.CardSharing => CardSharing,
        _ => true
    };

    public ProFeatureFlags Without(string feature) => feature switch
    {
        ProFeatures.RemoteBoards => this with { RemoteBoards = false },
        ProFeatures.RemoteTerminals => this with { RemoteTerminals = false },
        ProFeatures.SessionSharing => this with { SessionSharing = false },
        ProFeatures.CardSharing => this with { CardSharing = false },
        _ => this
    };
}

/// <summary>
/// The account's plan as the dashboard reads it from <c>GET /api/v1/account/entitlements</c>.
/// <see cref="Unknown"/> (offline, an older server, a failed read) allows everything: it is never
/// treated as a known Free account, and the hosted service remains the authority.
/// </summary>
public sealed record AccountEntitlements(string State, bool Enforced, ProFeatureFlags Features, string BillingUrl, DateTime? CheckedUtc)
{
    public const string SignedOut = "signed_out";
    public const string Free = "free";
    public const string Pro = "pro";
    public const string Inactive = "inactive";
    public const string Unknown = "unknown";

    public bool Allows(string feature) => Features.Allows(feature);
}

public interface IAccountEntitlements
{
    /// <summary>The cached answer, read from the server when stale or when <paramref name="refresh"/> is set. Never throws for a failed read.</summary>
    Task<AccountEntitlements> GetAsync(CancellationToken ct, bool refresh = false);

    /// <summary>False only when no account key is configured or the account is known not to have the feature.</summary>
    Task<bool> AllowsAsync(string feature, CancellationToken ct);

    /// <summary>
    /// Records a <c>pro_required</c> answer for this account's own plan, so background loops and the
    /// dashboard stop at once instead of waiting for the next read.
    /// </summary>
    void NoteDenied(string feature);

    void Invalidate();
}

internal sealed record RemoteEntitlementFeatures(
    [property: JsonPropertyName(ProFeatures.RemoteBoards)] bool? RemoteBoards,
    [property: JsonPropertyName(ProFeatures.RemoteTerminals)] bool? RemoteTerminals,
    [property: JsonPropertyName(ProFeatures.SessionSharing)] bool? SessionSharing,
    [property: JsonPropertyName(ProFeatures.CardSharing)] bool? CardSharing);
internal sealed record RemoteEntitlements(int Version, string? Access, bool Enforced, RemoteEntitlementFeatures? Features, int? RefreshAfterSeconds);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteEntitlements))]
internal partial class AccountEntitlementsJson : JsonSerializerContext;

/// <summary>
/// One bounded read of the account's plan from the hosted origin, cached per (origin, account key).
/// A different key or origin is a different entry, so linking, signing out or replacing the key
/// needs no hook. Only a hash of the pair is kept; the key is never stored or logged here.
/// </summary>
public sealed class AccountEntitlementsClient(IHttpClientFactory clients, Func<Uri?> origin, Func<string?> apiKey, TimeProvider? time = null)
    : IAccountEntitlements
{
    public const string HttpClientName = "account-entitlements";
    public const string Path = "/api/v1/account/entitlements";
    public const string BillingPath = "/Billing";
    public const int MaxResponseBytes = 16 * 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(300);
    /// <summary>An unknown answer is asked again sooner, so a recovered connection shows the real plan.</summary>
    public static readonly TimeSpan UnknownLifetime = TimeSpan.FromSeconds(60);
    private const int MinLifetimeSeconds = 30, MaxLifetimeSeconds = 3600, MaxEntries = 8;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (AccountEntitlements Snapshot, DateTimeOffset Expires)> _cache = [];

    /// <summary>The hosted origin: HTTPS, or HTTP for a loopback fixture, without credentials. Null leaves the answer unknown.</summary>
    public static Uri? Origin(string? frontendUrl) =>
        Uri.TryCreate(frontendUrl, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            ? new Uri(uri.GetLeftPart(UriPartial.Authority))
            : null;

    public async Task<AccountEntitlements> GetAsync(CancellationToken ct, bool refresh = false)
    {
        var target = origin();
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            return new(AccountEntitlements.SignedOut, false, ProFeatureFlags.None, BillingUrl(target), null);
        if (target is null) return Unknown(target);
        var identity = Identity(target, key);
        lock (_gate)
        {
            if (!refresh && _cache.TryGetValue(identity, out var cached) && cached.Expires > _time.GetUtcNow())
                return cached.Snapshot;
        }
        var (snapshot, lifetime) = await ReadAsync(target, key, ct);
        lock (_gate)
        {
            if (_cache.Count >= MaxEntries) _cache.Clear();
            _cache[identity] = (snapshot, _time.GetUtcNow() + lifetime);
        }
        return snapshot;
    }

    public async Task<bool> AllowsAsync(string feature, CancellationToken ct) => (await GetAsync(ct)).Allows(feature);

    public void NoteDenied(string feature)
    {
        var target = origin();
        var key = apiKey();
        if (target is null || string.IsNullOrWhiteSpace(key)) return;
        var identity = Identity(target, key);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var known = _cache.TryGetValue(identity, out var cached);
            var current = known ? cached.Snapshot : Unknown(target);
            // The server just refused this account, so it is no longer unknown or Pro for this feature.
            var state = current.State is AccountEntitlements.Free or AccountEntitlements.Inactive ? current.State : AccountEntitlements.Free;
            var expires = known && cached.Expires > now + DefaultLifetime ? cached.Expires : now + DefaultLifetime;
            _cache[identity] = (current with { State = state, Enforced = true, Features = current.Features.Without(feature), CheckedUtc = now.UtcDateTime }, expires);
        }
    }

    public void Invalidate()
    {
        lock (_gate) _cache.Clear();
    }

    private async Task<(AccountEntitlements Snapshot, TimeSpan Lifetime)> ReadAsync(Uri target, string key, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(target, Path));
            // Not Add: a hand-edited key with a line break must read as unknown, never throw into a
            // terminal start or a sync tick. Such a value is dropped, and the server answers 401.
            request.Headers.TryAddWithoutValidation("X-Api-Key", key);
            // The named client carries the 10 s timeout, the response cap and no redirects.
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.OK
                && JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(ct), AccountEntitlementsJson.Default.RemoteEntitlements) is
                {
                    Version: 1,
                    Access: AccountEntitlements.Free or AccountEntitlements.Pro or AccountEntitlements.Inactive,
                    Features: { RemoteBoards: { } boards, RemoteTerminals: { } terminals, SessionSharing: { } sessions, CardSharing: { } cards }
                } remote)
                return (new(remote.Access, remote.Enforced, new(boards, terminals, sessions, cards), BillingUrl(target), _time.GetUtcNow().UtcDateTime),
                    TimeSpan.FromSeconds(Math.Clamp(remote.RefreshAfterSeconds ?? (int)DefaultLifetime.TotalSeconds, MinLifetimeSeconds, MaxLifetimeSeconds)));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Offline, timed out, oversized or unreadable: unknown, which blocks nothing.
        }
        // 401 (the feature's own call reports the key), 404 from an older server, 429, 5xx, another
        // contract version and malformed bodies land here too.
        return (Unknown(target), UnknownLifetime);
    }

    private static AccountEntitlements Unknown(Uri? target) =>
        new(AccountEntitlements.Unknown, false, ProFeatureFlags.All, BillingUrl(target), null);

    private static string BillingUrl(Uri? target) =>
        (target?.GetLeftPart(UriPartial.Authority) ?? DataExportEndpointConfiguration.Host) + BillingPath;

    private static string Identity(Uri target, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.GetLeftPart(UriPartial.Authority) + "\n" + key)));
}
