using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using VibeRails.Utils;

namespace VibeRails.Services.Board.Sync;

/// <summary>The three calls the desktop makes to viberails.ai. Faked in tests; one HTTP implementation.</summary>
public interface IBoardSyncClient
{
    /// <summary>True when an endpoint and an API key are both configured; the sync is off otherwise.</summary>
    bool IsConfigured { get; }
    string? DestinationKey { get; }

    /// <summary>Advertises this open project root and consumes at most one owner launch request.</summary>
    Task<BoardLaunchPollResult> PollLaunchAsync(BoardLaunchPoll body, CancellationToken ct, string destination) => throw new NotSupportedException();
    /// <summary>Reports a launch outcome; retries never repeat the launch.</summary>
    Task<BoardLaunchResultAck> CompleteLaunchAsync(Guid id, BoardLaunchResult body, CancellationToken ct, string destination) => throw new NotSupportedException();

    Task<BoardSyncPublishResponse> PublishAsync(BoardSyncPublishRequest request, CancellationToken cancellationToken, string? expectedDestination = null);
    Task<BoardSyncPushResponse> PushAsync(string remoteBoardId, BoardSyncPushRequest request, CancellationToken cancellationToken, string? expectedDestination = null);
    Task<BoardSyncPullResponse> PullAsync(string remoteBoardId, long after, int limit, CancellationToken cancellationToken, string? expectedDestination = null);
    Task<BoardSyncActivityAck> PutActivityAsync(string remoteBoardId, string cardId, BoardSyncActivityWire activity, CancellationToken cancellationToken, string? expectedDestination = null);
    Task<BoardRemoteDescriptor?> DescribeAsync(string remoteBoardId, CancellationToken ct, string? destination = null) => Task.FromResult<BoardRemoteDescriptor?>(null);
    Task<List<BoardRemoteDescriptor>> DiscoverAsync(CancellationToken ct, string? destination = null) => throw new NotSupportedException();
    Task<BoardSharingOverview> GetSharingAsync(string remoteBoardId, CancellationToken ct, string? destination = null) => throw new NotSupportedException();
    Task<BoardSharingResult> SaveInviteAsync(string remoteBoardId, string? invitationId, BoardSharingEmailRequest body, CancellationToken ct, string? destination = null) => throw new NotSupportedException();
    Task<BoardSharingResult> RemoveInviteAsync(string remoteBoardId, string invitationId, CancellationToken ct, string? destination = null) => throw new NotSupportedException();
}

/// <summary>
/// HTTPS client for the desktop Board sync API. The endpoint and the API key are both resolved on
/// every call (a changed destination interrupts this exchange and is republished next tick), the key is sent in
/// <c>X-Api-Key</c>, and the named client follows no redirects: a redirect would replay the key and
/// body to wherever it pointed.
/// </summary>
public sealed class BoardSyncHttpClient(IHttpClientFactory httpClientFactory, Func<Uri?> endpoint, Func<string?> apiKey) : IBoardSyncClient
{
    public const string HttpClientName = "board-sync";

    /// <summary>A fixed endpoint, for tests and callers that never change it.</summary>
    public BoardSyncHttpClient(IHttpClientFactory httpClientFactory, Uri? endpoint, Func<string?> apiKey)
        : this(httpClientFactory, () => endpoint, apiKey) { }

    public bool IsConfigured => endpoint() is not null && !string.IsNullOrWhiteSpace(apiKey());
    public string? DestinationKey => Identity(endpoint(), apiKey());
    private static string? Identity(Uri? target, string? key) => target is null || string.IsNullOrWhiteSpace(key) ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.AbsoluteUri.TrimEnd('/') + "\n" + key)));
    // The hosted board fills a pull page only up to an 8 MiB estimate that assumes every character
    // is escaped (BoardSyncLimits.MaxReadBytes, 6 bytes a character), and acknowledgements are tiny.
    // Twice that contract leaves headroom without buffering more than a page could hold. The body is
    // read incrementally, never trusted from Content-Length alone.
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    public const int MaxErrorResponseBytes = 4096;
    public static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);

    public Uri? Endpoint => endpoint();

    /// <inheritdoc />
    public Task<BoardLaunchPollResult> PollLaunchAsync(BoardLaunchPoll body, CancellationToken ct, string destination) =>
        SendAsync(HttpMethod.Post, "launches/poll", body, BoardSyncJsonContext.Default.BoardLaunchPoll,
            BoardSyncJsonContext.Default.BoardLaunchPollResult, ct, destination);

    /// <inheritdoc />
    public async Task<BoardLaunchResultAck> CompleteLaunchAsync(Guid id, BoardLaunchResult body, CancellationToken ct, string destination)
    {
        var ack = await SendAsync(HttpMethod.Post, "launches/" + id.ToString("D") + "/result", body, BoardSyncJsonContext.Default.BoardLaunchResult,
            BoardSyncJsonContext.Default.BoardLaunchResultAck, ct, destination);
        return ack.Saved ? ack : throw new BoardSyncClientException("The server did not acknowledge the launch outcome.", "invalid_response");
    }

    public async Task<BoardRemoteDescriptor?> DescribeAsync(string remoteBoardId, CancellationToken ct, string? destination = null) =>
        await SendAsync<object, BoardRemoteDescriptor>(HttpMethod.Get, Uri.EscapeDataString(remoteBoardId), null, null,
            BoardSyncJsonContext.Default.BoardRemoteDescriptor, ct, destination);
    public Task<List<BoardRemoteDescriptor>> DiscoverAsync(CancellationToken ct, string? destination = null) =>
        SendAsync<object, List<BoardRemoteDescriptor>>(HttpMethod.Get, "", null, null, BoardSyncJsonContext.Default.ListBoardRemoteDescriptor, ct, destination);
    public Task<BoardSharingOverview> GetSharingAsync(string remoteBoardId, CancellationToken ct, string? destination = null) =>
        SendAsync<object, BoardSharingOverview>(HttpMethod.Get, Uri.EscapeDataString(remoteBoardId) + "/sharing", null, null,
            BoardSyncJsonContext.Default.BoardSharingOverview, ct, destination);
    public Task<BoardSharingResult> SaveInviteAsync(string remoteBoardId, string? invitationId, BoardSharingEmailRequest body, CancellationToken ct, string? destination = null) =>
        SendAsync(invitationId is null ? HttpMethod.Post : HttpMethod.Put,
            Uri.EscapeDataString(remoteBoardId) + "/sharing" + (invitationId is null ? "" : "/" + Uri.EscapeDataString(invitationId)),
            body, BoardSyncJsonContext.Default.BoardSharingEmailRequest, BoardSyncJsonContext.Default.BoardSharingResult, ct, destination);
    public Task<BoardSharingResult> RemoveInviteAsync(string remoteBoardId, string invitationId, CancellationToken ct, string? destination = null) =>
        SendAsync<object, BoardSharingResult>(HttpMethod.Delete, Uri.EscapeDataString(remoteBoardId) + "/sharing/" + Uri.EscapeDataString(invitationId),
            null, null, BoardSyncJsonContext.Default.BoardSharingResult, ct, destination);

    public async Task<BoardSyncActivityAck> PutActivityAsync(string remoteBoardId, string cardId, BoardSyncActivityWire activity,
        CancellationToken cancellationToken, string? expectedDestination = null)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(activity, BoardSyncJsonContext.Default.BoardSyncActivityWire).Length > BoardSyncActivity.MaxSnapshotBytes)
            throw new BoardSyncClientException("The card activity exceeds the upload limit; its data remains local.", "activity_too_large");
        var ack = await SendAsync(HttpMethod.Put,
            Uri.EscapeDataString(remoteBoardId) + "/cards/" + Uri.EscapeDataString(cardId) + "/activity",
            activity, BoardSyncJsonContext.Default.BoardSyncActivityWire, BoardSyncJsonContext.Default.BoardSyncActivityAck,
            cancellationToken, expectedDestination);
        if (ack.Schema != 1 || !string.Equals(ack.CardId, cardId, StringComparison.Ordinal))
            throw new BoardSyncClientException("The server did not acknowledge this card's activity; the upload will retry.", "invalid_response");
        return ack;
    }

    public Task<BoardSyncPublishResponse> PublishAsync(BoardSyncPublishRequest request, CancellationToken cancellationToken, string? expectedDestination = null) =>
        SendAsync(HttpMethod.Post, "publish", request, BoardSyncJsonContext.Default.BoardSyncPublishRequest,
            BoardSyncJsonContext.Default.BoardSyncPublishResponse, cancellationToken, expectedDestination);

    public Task<BoardSyncPushResponse> PushAsync(string remoteBoardId, BoardSyncPushRequest request, CancellationToken cancellationToken, string? expectedDestination = null) =>
        SendAsync(HttpMethod.Post, Uri.EscapeDataString(remoteBoardId) + "/push", request, BoardSyncJsonContext.Default.BoardSyncPushRequest,
            BoardSyncJsonContext.Default.BoardSyncPushResponse, cancellationToken, expectedDestination);

    public Task<BoardSyncPullResponse> PullAsync(string remoteBoardId, long after, int limit, CancellationToken cancellationToken, string? expectedDestination = null) =>
        SendAsync<object, BoardSyncPullResponse>(HttpMethod.Get,
            $"{Uri.EscapeDataString(remoteBoardId)}/entries?after={after.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
            null, null, BoardSyncJsonContext.Default.BoardSyncPullResponse, cancellationToken, expectedDestination);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method, string relativePath, TRequest? body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest>? requestInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseInfo,
        CancellationToken cancellationToken, string? expectedDestination)
        where TRequest : class
        where TResponse : class
    {
        var target = endpoint();
        if (target is null)
            throw new BoardSyncClientException("Board sync has no HTTPS endpoint configured.", "no_endpoint");
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new BoardSyncClientException("Add your viberails.ai API key in Settings before publishing a board.", "no_api_key");
        if (expectedDestination is not null && expectedDestination != Identity(target, key))
            throw new BoardSyncClientException("The server or API key changed during sync. Retrying with the configured account next minute.", "destination_changed");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExchangeTimeout);

        using var request = new HttpRequestMessage(method, new Uri(target.AbsoluteUri.TrimEnd('/') + "/" + relativePath));
        request.Headers.Add("X-Api-Key", key);
        if (body is not null && requestInfo is not null)
            request.Content = JsonContent.Create(body, requestInfo);

        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new BoardSyncClientException("viberails.ai rejected the API key. Check it in Settings.", "unauthorized", status);
                // Only a bounded, recognised code changes what the desktop does; the message is
                // always the desktop's own wording, so remote prose never reaches logs or the UI.
                var (code, entryId) = await ReadErrorAsync(response, timeout.Token);
                if (response.StatusCode == HttpStatusCode.BadRequest && code == BoardSyncWire.CodeInvalidEntry && entryId is not null)
                    throw new BoardSyncClientException("The server rejected a queued entry. Its data has been kept locally.",
                        BoardSyncWire.CodeInvalidEntry, status, entryId);
                if (response.StatusCode == HttpStatusCode.BadRequest && code == BoardSyncWire.CodeInvalidRequest)
                    throw new BoardSyncClientException(relativePath.Contains("/sharing", StringComparison.Ordinal)
                        ? "Check the email address and the limit of three collaborators, including pending invitations."
                        : "viberails.ai rejected the request as invalid; entries remain queued. Check the board and lane names, then retry.",
                        BoardSyncWire.CodeInvalidRequest, status);
                if (response.StatusCode == HttpStatusCode.NotFound && code == BoardSyncWire.CodeBoardNotFound)
                    throw new BoardSyncClientException("This board is unavailable on viberails.ai, or this account no longer has access.",
                        BoardSyncWire.CodeBoardNotFound, status);
                if (response.StatusCode == HttpStatusCode.Conflict && code == BoardSyncWire.CodeWriteConflict)
                    throw new BoardSyncClientException("viberails.ai was updating this board at the same time; the sync will retry.",
                        BoardSyncWire.CodeWriteConflict, status);
                throw new BoardSyncClientException($"Board sync returned HTTP {status}; entries remain queued.", "http_" + status, status);
            }
            var parsed = await ReadJsonAsync(response, responseInfo, timeout.Token);
            return parsed ?? throw new BoardSyncClientException("The server returned an invalid or empty response.", "invalid_response", status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // An HttpClient timeout is a TaskCanceledException whose token is not the caller's.
            throw new BoardSyncClientException("viberails.ai did not answer in time.", "network");
        }
        catch (HttpRequestException)
        {
            throw new BoardSyncClientException("viberails.ai could not be reached. Check the connection and retry.", "network");
        }
    }

    // Only these bounded, known machine-readable fields affect what the desktop does: a recognised
    // `code` and, for invalid_entry, a well-formed `entryId`. Remote prose is never trusted,
    // retained, or shown; an oversized, malformed or unknown error reads as (null, null) and
    // falls back to the ordinary retryable HTTP error.
    private static async Task<(string? Code, string? EntryId)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxErrorResponseBytes)
            return (null, null);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxErrorResponseBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > MaxErrorResponseBytes) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            string? code = null, entryId = null;
            var hasCode = false;
            var hasId = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("code"))
                {
                    if (hasCode || property.Value.ValueKind != JsonValueKind.String) return (null, null);
                    hasCode = true;
                    code = property.Value.GetString();
                }
                else if (property.NameEquals("entryId"))
                {
                    if (hasId || property.Value.ValueKind != JsonValueKind.String) return (null, null);
                    hasId = true;
                    entryId = property.Value.GetString();
                }
            }
            if (code is not (BoardSyncWire.CodeInvalidEntry or BoardSyncWire.CodeInvalidRequest
                or BoardSyncWire.CodeBoardNotFound or BoardSyncWire.CodeWriteConflict))
                return (null, null);
            var wellFormedId = entryId is { Length: > 0 and <= BoardSyncWire.MaxIdLength }
                && entryId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
            return (code, wellFormedId ? entryId : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new BoardSyncClientException("The server response exceeds the sync size limit.", "response_too_large");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            // Sized from a trustworthy Content-Length up front, so the buffer does not double its way there.
            using var buffer = new MemoryStream(response.Content.Headers.ContentLength is long length and > 0
                ? (int)length : 64 * 1024);
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                    throw new BoardSyncClientException("The server response exceeds the sync size limit.", "response_too_large");
                buffer.Write(chunk, 0, read);
            }
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The production instance: named client, endpoint and key both re-read per call.</summary>
    public static BoardSyncHttpClient FromConfiguration(IHttpClientFactory factory, Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        new(factory, () => BoardSyncEndpoint.FromConfiguration(configuration), static () => ParserConfigs.GetApiKey());
}
