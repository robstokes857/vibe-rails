using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.DB;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

public sealed record CreateSessionShareRequest(string DisplayName);
public sealed record SessionShareResponse(bool Success, string Status, string Message,
    string? Url = null, string? DisplayName = null, DateTimeOffset? ExpiresUtc = null);
internal sealed record RemoteSessionShareRequest(Guid SessionId, string DisplayName);
internal sealed record RemoteSessionShareResponse(Guid SessionId, string Key, string SharePath,
    DateTimeOffset ExpiresUtc, bool? UploadRequired);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateSessionShareRequest))]
[JsonSerializable(typeof(SessionShareResponse))]
[JsonSerializable(typeof(RemoteSessionShareRequest))]
[JsonSerializable(typeof(RemoteSessionShareResponse))]
internal partial class SessionSharingJsonContext : JsonSerializerContext;

/// <summary>Creates public capabilities at the pinned export origin and persists their upload intent.</summary>
public sealed class SessionSharingService(HttpClient client, ISessionStore sessions, ISessionArchiveReader archives)
{
    private static readonly SemaphoreSlim CreationGate = new(1, 1);
    internal static readonly Uri Endpoint = new(DataExportEndpointConfiguration.ExportUri, "/api/v1/session-sharing-links");
    internal static HttpMessageHandler CreateHandler() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };

    internal static string KeyFingerprint(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task<SessionShareResponse> CreateAsync(Guid sessionId, string? displayName, CancellationToken ct)
    {
        var name = displayName?.Trim();
        if (sessionId == Guid.Empty || string.IsNullOrEmpty(name) || name.Length > 160)
            return Failure("invalid_request", "Enter a link name of 1 to 160 characters.");
        var session = await sessions.GetSessionByIdAsync(sessionId.ToString("D"), ct);
        if (session is null) return Failure("not_found", "This session recording was not found.");
        var apiKey = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return Failure("no_api_key", "Sign in to your VibeRails account before sharing.");
        if (!await CreationGate.WaitAsync(0, ct)) return Failure("busy", "Another sharing link is being created. Try again shortly.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Add("X-Api-Key", apiKey);
            request.Content = JsonContent.Create(new RemoteSessionShareRequest(sessionId, name),
                SessionSharingJsonContext.Default.RemoteSessionShareRequest);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failure("invalid_api_key", "Sign in with an account key that allows session uploads and sharing.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Failure("rate_limited", "Too many sharing requests. Try again in a minute.");
            if (response.StatusCode != HttpStatusCode.Created)
                return Failure("unavailable", "Could not create the link. Try again shortly.");
            var bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(deadline.Token), 16 * 1024, deadline.Token);
            var remote = JsonSerializer.Deserialize(bytes, SessionSharingJsonContext.Default.RemoteSessionShareResponse);
            if (remote is null || remote.SessionId != sessionId || remote.Key is not { Length: 64 }
                || remote.Key.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || remote.SharePath != "/shared/session?key=" + remote.Key || remote.UploadRequired is null
                || remote.ExpiresUtc <= DateTimeOffset.UtcNow || remote.ExpiresUtc > DateTimeOffset.UtcNow.AddDays(32))
                return Failure("invalid_response", "The sharing server returned an invalid response.");

            // Capture the key actually used for creation. A later settings/account change must
            // not retarget this request, including while this HTTP response is in flight.
            if (remote.UploadRequired.Value && !await archives.QueueSessionShareUploadAsync(
                session.Id, KeyFingerprint(apiKey), DateTime.UtcNow, ct))
                return Failure("not_found", "This session recording was removed before its upload could be queued.");
            var changed = !string.Equals(apiKey, ParserConfigs.GetApiKey(), StringComparison.Ordinal);
            var status = !remote.UploadRequired.Value ? "ready" : changed ? "account_changed" : "pending_upload";
            var message = status switch
            {
                "ready" => "Your replay is ready to share.",
                "account_changed" => "Link created. Switch back to the account key used to create it so the session can upload.",
                _ when session.EndedUTC is not null => "Your session is at the front of the upload queue. Keep VibeRails open until it uploads.",
                _ => "Available after the session ends and uploads. Keep VibeRails open to finish the upload."
            };
            return new(true, status, message, Endpoint.GetLeftPart(UriPartial.Authority) + remote.SharePath, name, remote.ExpiresUtc);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Failure("timeout", "The sharing server took too long to respond. Try again shortly."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException)
        { return Failure("unavailable", "Could not create the link. Try again shortly."); }
        finally { CreationGate.Release(); }
    }

    // Bounds decoded bytes even for a chunked response; never read or echo remote error prose.
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[Math.Min(limit + 1, 4096)];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)result.Length)), ct);
            if (count == 0) return result.ToArray();
            result.Write(buffer, 0, count);
            if (result.Length > limit) throw new InvalidDataException("Sharing payload exceeds its limit.");
        }
    }

    private static SessionShareResponse Failure(string status, string message) => new(false, status, message);
}
