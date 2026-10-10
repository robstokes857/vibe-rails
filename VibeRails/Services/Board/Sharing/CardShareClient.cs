using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Services.Board.Sharing;

/// <summary>Bounded transport to the approved public-card host. The caller pins the account for a whole operation.</summary>
public sealed class CardShareClient(HttpClient client)
{
    internal static readonly Uri Endpoint = new("https://viberails.ai/api/v1/card-sharing-links");
    internal static HttpMessageHandler CreateHandler() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
    internal static bool IsHex(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool IsSourceKey(string? value) => value is { Length: >= 2 and <= 160 }
        && char.IsAsciiLetter(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public async Task<CardSharePublished> PublishAsync(string key, string name, string localCardId, CardShareSnapshot snapshot, CancellationToken ct)
    {
        var result = await SendAsync(key, HttpMethod.Post, "", JsonContent.Create(new CardSharePublishRequest(name, snapshot, localCardId),
            CardSharingJsonContext.Default.CardSharePublishRequest), HttpStatusCode.Created, CardSharingJsonContext.Default.CardSharePublished, ct);
        ValidateLink(result.Link, snapshot.SourceKey, localCardId);
        if (result.Link.Status != "active" || result.Link.ExpiresUtc <= DateTime.UtcNow || result.Link.ExpiresUtc > DateTime.UtcNow.AddDays(32)) throw Invalid();
        ValidateSessions(result.UploadSessions, snapshot);
        return result;
    }

    public async Task<List<CardShareLinkDto>> ListAsync(string key, string sourceKey, string localCardId, int? before, CancellationToken ct)
    {
        var result = await SendAsync(key, HttpMethod.Get, "?sourceKey=" + Uri.EscapeDataString(sourceKey) + "&localCardId=" + Uri.EscapeDataString(localCardId)
            + (before is null ? "" : "&before=" + before.Value), null, HttpStatusCode.OK, CardSharingJsonContext.Default.ListCardShareLinkDto, ct);
        if (result.Count > 100) throw Invalid();
        var previous = before ?? int.MaxValue;
        foreach (var link in result)
        {
            ValidateLink(link, sourceKey, localCardId);
            if (link.Id >= previous) throw Invalid();
            previous = link.Id;
        }
        return result;
    }

    public async Task<List<CardShareSource>> SourcesAsync(string key, int after, CancellationToken ct)
    {
        var result = await SendAsync(key, HttpMethod.Get, "/sources?after=" + after, null, HttpStatusCode.OK,
            CardSharingJsonContext.Default.ListCardShareSource, ct);
        if (result.Count > 100) throw Invalid();
        foreach (var source in result)
        {
            if (source is null || source.Id <= after || !IsSourceKey(source.SourceKey) || !IsHex(source.Revision)
                || source.LocalCardId is { } id && (id.Length is < 1 or > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))) throw Invalid();
            after = source.Id;
        }
        return result;
    }

    public async Task<CardShareRefreshResult> RefreshAsync(string key, CardShareSource source, CardShareSnapshot snapshot, CancellationToken ct)
    {
        var result = await SendAsync(key, HttpMethod.Put, "/sources/" + source.Id,
            JsonContent.Create(new CardShareRefreshRequest(source.Revision, snapshot), CardSharingJsonContext.Default.CardShareRefreshRequest),
            HttpStatusCode.OK, CardSharingJsonContext.Default.CardShareRefreshResult, ct);
        if (!result.Updated) throw Invalid();
        ValidateSessions(result.UploadSessions, snapshot);
        return result;
    }

    public async Task RenameAsync(string key, int id, string name, CancellationToken ct) =>
        await SendEmptyAsync(key, HttpMethod.Patch, "/" + id, JsonContent.Create(new CardShareNameRequest(name),
            CardSharingJsonContext.Default.CardShareNameRequest), ct);
    public async Task RevokeAsync(string key, int id, CancellationToken ct) => await SendEmptyAsync(key, HttpMethod.Delete, "/" + id, null, ct);

    private async Task<T> SendAsync<T>(string key, HttpMethod method, string path, HttpContent? body,
        HttpStatusCode expected, JsonTypeInfo<T> json, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var request = Request(key, method, path, body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != expected) throw Failure(response.StatusCode);
        var bytes = await SessionSharingService.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(deadline.Token), 1024 * 1024, deadline.Token);
        return JsonSerializer.Deserialize(bytes, json) ?? throw Invalid();
    }
    private async Task SendEmptyAsync(string key, HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(key, method, path, body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != HttpStatusCode.NoContent) throw Failure(response.StatusCode);
    }
    private static HttpRequestMessage Request(string key, HttpMethod method, string path, HttpContent? body)
    {
        var request = new HttpRequestMessage(method, Endpoint.AbsoluteUri + path) { Content = body };
        request.Headers.Add("X-Api-Key", key);
        return request;
    }
    private static void ValidateLink(CardShareLinkDto? link, string sourceKey, string localCardId)
    {
        const string prefix = "/shared/card#key=";
        if (link is null || link.Id <= 0 || link.SourceKey != sourceKey || link.LocalCardId != localCardId || link.DisplayName is not { Length: >= 1 and <= 160 }
            || link.SharePath is null || !link.SharePath.StartsWith(prefix, StringComparison.Ordinal) || !IsHex(link.SharePath[prefix.Length..])
            || link.CreatedUtc == default || link.ExpiresUtc <= link.CreatedUtc || link.ExpiresUtc > link.CreatedUtc.AddDays(32)
            || link.UpdatedUtc == default || link.Status is not ("active" or "expired" or "revoked" or "key_unavailable")) throw Invalid();
    }
    private static void ValidateSessions(IReadOnlyList<Guid>? requested, CardShareSnapshot snapshot)
    {
        var allowed = snapshot.Sessions.Select(s => s.SourceId).ToHashSet();
        if (requested is null || requested.Count > allowed.Count || requested.Distinct().Count() != requested.Count
            || requested.Any(id => !allowed.Contains(id))) throw Invalid();
    }
    private static CardShareTransportException Invalid() => new("The sharing server returned an invalid response.");
    private static CardShareTransportException Failure(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized => "Sign in again. viberails.ai rejected the account key.",
        HttpStatusCode.Forbidden => "The account key needs Boards and Sessions access to share complete cards.",
        HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "The share was not found, or the server needs the card-sharing update.",
        HttpStatusCode.Conflict => "The shared card changed. Refresh the links and try again.",
        HttpStatusCode.RequestEntityTooLarge => "The complete card exceeds the sharing transfer limit. No partial card was published.",
        HttpStatusCode.BadRequest => "The server rejected the card or link name. Check account permissions and update both applications.",
        HttpStatusCode.TooManyRequests => "Too many sharing requests. Try again in a minute.",
        _ => "viberails.ai could not complete the request. Check the connection and server deployment."
    });
}

/// <summary>Only fixed local wording may cross this boundary; never remote bodies, headers or credentials.</summary>
public sealed class CardShareTransportException(string message) : Exception(message);
