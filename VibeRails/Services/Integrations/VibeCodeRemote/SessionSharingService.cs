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
    string? Url = null, string? DisplayName = null, DateTimeOffset? ExpiresUtc = null,
    int? HttpStatus = null);
internal sealed record RemoteSessionShareRequest(Guid SessionId, string DisplayName);
internal sealed record RemoteSessionShareResponse(Guid SessionId, string Key, string SharePath,
    DateTimeOffset ExpiresUtc, bool? UploadRequired);
internal sealed record RemoteSessionShareError(string? Code);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateSessionShareRequest))]
[JsonSerializable(typeof(SessionShareResponse))]
[JsonSerializable(typeof(RemoteSessionShareRequest))]
[JsonSerializable(typeof(RemoteSessionShareResponse))]
[JsonSerializable(typeof(RemoteSessionShareError))]
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
            if (response.StatusCode != HttpStatusCode.Created)
                return await ServerFailureAsync(response, deadline.Token);
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
        { return Failure("timeout", "viberails.ai did not respond in time. Check Sharing links before trying again; the link may already have been created."); }
        catch (HttpRequestException ex)
        {
            return Failure("network_error", ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "Could not find viberails.ai. Check your internet connection and DNS settings.",
                HttpRequestError.SecureConnectionError => "Could not establish a secure connection to viberails.ai. Check your device clock and network, or contact the server administrator.",
                _ => "Could not connect to viberails.ai. Check your internet connection and try again."
            });
        }
        catch (InvalidDataException)
        { return Failure("invalid_response", "The sharing server returned a response larger than expected. Contact the server administrator."); }
        catch (JsonException)
        { return Failure("invalid_response", "The sharing server returned an unreadable response. Check that the server and client are up to date."); }
        catch (IOException)
        { return Failure("network_error", "The connection to viberails.ai was interrupted. Check Sharing links before trying again; the link may already have been created."); }
        finally { CreationGate.Release(); }
    }

    private static async Task<SessionShareResponse> ServerFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? code = null;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), 16 * 1024, ct);
                code = JsonSerializer.Deserialize(bytes, SessionSharingJsonContext.Default.RemoteSessionShareError)?.Code;
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                // Keep the HTTP status useful even when an error page is malformed or too large.
            }
        }

        // Only known codes select our own messages. Never display remote error prose, SQL,
        // redirect locations, response headers, or exception messages that might contain keys.
        if (status >= 500 && code == "schema_update_required")
            return Failure("schema_update_required", "The sharing server needs a database update. Ask the server administrator to apply the pending migrations.", status);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Failure("invalid_api_key", code switch
            {
                "api_key_expired" => "Your VibeRails account key has expired. Sign in again before sharing.",
                "api_key_revoked" => "Your VibeRails account key has been revoked. Sign in again before sharing.",
                _ => "viberails.ai rejected your account key. Sign in again before sharing."
            }, status),
            HttpStatusCode.Forbidden => Failure("permission_denied", "Your account key does not allow session sharing. Sign in with a key that includes Sessions access.", status),
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
                Failure("server_update_required", $"Session sharing is not available on the deployed server (HTTP {status}). Ask the server administrator to deploy the sharing update.", status),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                Failure("invalid_request", $"The sharing server rejected the session or link name (HTTP {status}). Refresh the terminal and try again.", status),
            HttpStatusCode.TooManyRequests => Failure("rate_limited", "Too many sharing requests. Try again in a minute.", status),
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                Failure("server_unavailable", $"viberails.ai is temporarily unavailable (HTTP {status}). Try again shortly, or contact the server administrator if it continues.", status),
            _ when status is >= 300 and < 400 => Failure("unexpected_redirect", $"The sharing server returned an unexpected redirect (HTTP {status}). Ask the server administrator to check the deployment.", status),
            _ => Failure("server_error", $"viberails.ai could not create the sharing link (HTTP {status}). Ask the server administrator to check the server logs and pending database migrations.", status)
        };
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

    private static SessionShareResponse Failure(string status, string message, int? httpStatus = null)
        => new(false, status, message, HttpStatus: httpStatus);
}
