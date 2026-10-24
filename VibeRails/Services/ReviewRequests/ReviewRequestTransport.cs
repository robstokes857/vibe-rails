using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.VCA;

namespace VibeRails.Services.ReviewRequests;

/// <summary>
/// The desktop's bounded transport to viberails.ai for review requests (VB-50, slice C): send a
/// stored request's bundle, poll its decisions, cancel its hosted copy. Fixed HTTPS origin, header
/// <c>X-Api-Key</c> only, no redirects or cookies, bounded time and response bytes, and every answer
/// validated before use. Failures carry fixed local wording and a code; a remote body, header or
/// exception message never crosses this boundary (the <see cref="SessionSharingService"/> discipline).
/// </summary>
/// <remarks>
/// There is no separate capability probe: a host without the review-request update answers the
/// create POST with 404/405/501, which is reported as <c>server_update_required</c>, and nothing is
/// recorded as sent or queued for upload until the host has confirmed the request by reference. An
/// absent route creates nothing, so the first POST is the probe and the client fails closed.
/// </remarks>
public sealed class ReviewRequestTransport(HttpClient client)
{
    public static readonly Uri Endpoint = new(DataExportEndpointConfiguration.Host + "/api/v1/review-requests");

    /// <summary>The origin shown in the disclosure; nothing is sent anywhere else.</summary>
    public const string Destination = DataExportEndpointConfiguration.Host;

    /// <summary>A bundle holds up to 4 MiB of text (24 MiB on the wire once escaped), so a send gets longer than a poll.</summary>
    internal static readonly TimeSpan SendDeadline = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>Fifty decisions with 4,000-character comments and trailers fit comfortably; anything larger is not an answer.</summary>
    internal const int MaxResponseBytes = 1024 * 1024;
    private const int MaxErrorBodyBytes = 16 * 1024;
    public const int MaxDecisions = 50;
    private const string ReferencePrefix = "rvw_";

    internal static HttpMessageHandler CreateHandler() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };

    /// <summary>The hosted reference: <c>rvw_</c> and 32 lowercase hex characters.</summary>
    public static bool IsReference(string? value) =>
        value is { Length: 36 } && value.StartsWith(ReferencePrefix, StringComparison.Ordinal)
        && value.Skip(ReferencePrefix.Length).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Sends the bundle as <c>{ requestId, bundle }</c>. 201 is a new hosted request, 200 the stored one
    /// for an exact retry; the answer must name this request id and tree, or it is invalid.
    /// </summary>
    public async Task<ReviewRequestHostedStatus> SendAsync(string apiKey, string requestId, string treeId, byte[] bundleJson, CancellationToken ct)
    {
        var answer = await ExchangeAsync(apiKey, HttpMethod.Post, "", Envelope(requestId, bundleJson), SendDeadline, ct);
        if (answer.Status is not (HttpStatusCode.Created or HttpStatusCode.OK)) throw Failure(answer.Status, answer.Code);
        return ParseStatus(answer.Body, requestId, treeId, null);
    }

    /// <summary>The requester's poll: status and every decision of one hosted request.</summary>
    public async Task<ReviewRequestHostedStatus> PollAsync(string apiKey, string remoteId, CancellationToken ct)
    {
        RequireReference(remoteId);
        var answer = await ExchangeAsync(apiKey, HttpMethod.Get, "/" + remoteId + "/decisions", null, Deadline, ct);
        if (answer.Status == HttpStatusCode.NotFound)
            throw new ReviewRequestTransportException("not_found",
                "viberails.ai no longer has this review request for the signed-in account. It may have been removed there, or the account key changed.", 404);
        if (answer.Status != HttpStatusCode.OK) throw Failure(answer.Status, answer.Code);
        return ParseStatus(answer.Body, null, null, remoteId);
    }

    /// <summary>Cancels the hosted copy. Idempotent; a request the host no longer has counts as cancelled.</summary>
    public async Task CancelAsync(string apiKey, string remoteId, CancellationToken ct)
    {
        RequireReference(remoteId);
        var answer = await ExchangeAsync(apiKey, HttpMethod.Delete, "/" + remoteId, null, Deadline, ct);
        if (answer.Status is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound) return;
        throw Failure(answer.Status, answer.Code);
    }

    // ---- The reviewer's side (VB-50 slice D): the key's account as a listed reviewer. ----

    /// <summary>A bundle holds up to 4 MiB of text, which the host's default JSON encoder can escape to six times that.</summary>
    internal const int MaxBundleResponseBytes = 25 * 1024 * 1024;
    internal static readonly TimeSpan BundleDeadline = TimeSpan.FromSeconds(90);
    /// <summary>The host lists at most 100 and says whether there are more; anything beyond this is not an answer.</summary>
    public const int MaxInboxItems = 200;
    private const int MaxDecisionBodyBytes = 16 * 1024;

    /// <summary>Requests addressed to the key's account (its verified email), newest first, without bundle text or other reviewers.</summary>
    public async Task<ReviewInboxHosted> InboxAsync(string apiKey, CancellationToken ct)
    {
        var answer = await ExchangeAsync(apiKey, HttpMethod.Get, "/inbox", null, Deadline, ct);
        if (answer.Status != HttpStatusCode.OK) throw Failure(answer.Status, answer.Code);
        return ParseInbox(answer.Body);
    }

    /// <summary>One request as a listed reviewer reads it: files with text, sessions, the statement and this account's decisions.</summary>
    public async Task<ReviewHostedBundle> BundleAsync(string apiKey, string reference, CancellationToken ct)
    {
        RequireReference(reference);
        var answer = await ExchangeAsync(apiKey, HttpMethod.Get, "/" + reference + "/bundle", null, BundleDeadline, ct, MaxBundleResponseBytes);
        if (answer.Status == HttpStatusCode.NotFound) throw NotAddressed();
        if (answer.Status != HttpStatusCode.OK) throw Failure(answer.Status, answer.Code);
        return ParseBundle(answer.Body, reference);
    }

    /// <summary>
    /// Records the key's account's decision: a rejection with a comment, or an approval whose trailer the
    /// host verifies itself (tree, signature, signer = this account). 201 is a new decision, 200 the
    /// stored one for a repeated trailer. The host's refusals keep their code and status.
    /// </summary>
    public async Task<ReviewRequestHostedDecision> DecideAsync(string apiKey, string reference, ReviewInboxDecisionBody decision, CancellationToken ct)
    {
        RequireReference(reference);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(decision, ReviewInboxJsonContext.Default.ReviewInboxDecisionBody);
        if (bytes.Length > MaxDecisionBodyBytes)
            throw new ReviewRequestTransportException("invalid_request", "The decision is larger than viberails.ai accepts.", null);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var answer = await ExchangeAsync(apiKey, HttpMethod.Post, "/" + reference + "/decisions", content, Deadline, ct);
        if (answer.Status == HttpStatusCode.NotFound) throw NotAddressed();
        if (answer.Status is not (HttpStatusCode.Created or HttpStatusCode.OK)) throw DecisionFailure(answer.Status, answer.Code);
        return ParseDecision(answer.Body);
    }

    /// <summary>Validates the hosted inbox: at most <see cref="MaxInboxItems"/> distinct references, each with bounded text, a known status and a plausible expiry.</summary>
    public static ReviewInboxHosted ParseInbox(ReadOnlySpan<byte> json)
    {
        ReviewInboxHosted? inbox;
        try { inbox = JsonSerializer.Deserialize(json, ReviewInboxJsonContext.Default.ReviewInboxHosted); }
        catch (JsonException) { throw Unreadable(); }
        if (inbox is null) throw Invalid();
        var items = inbox.Requests ?? [];
        if (items.Count > MaxInboxItems) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null || !IsReference(item.Id) || !ids.Add(item.Id)) throw Invalid();
            if (item.Repository is not { Length: > 0 and <= 256 } || item.RequestedBy is not { Length: > 0 and <= 320 }) throw Invalid();
            if (!IsKnownStatus(item.Status) || item.FileCount < 0 || item.SessionCount < 0 || item.CardKey is { Length: > 64 }) throw Invalid();
            if (item.CreatedUtc == default || item.ExpiresUtc <= item.CreatedUtc) throw Invalid();
        }
        return inbox with { Requests = items };
    }

    /// <summary>
    /// Validates a hosted bundle the way the host validated the desktop's: this reference, well-formed
    /// ids, 1 to 5,000 files with exact paths, known change kinds and omission reasons, the desktop's own
    /// text bounds, at most ten sessions and fifty of this account's decisions, and a statement that is
    /// exactly the staged-tree statement for the tree id.
    /// </summary>
    public static ReviewHostedBundle ParseBundle(ReadOnlySpan<byte> json, string reference)
    {
        ReviewHostedBundle? bundle;
        try { bundle = JsonSerializer.Deserialize(json, ReviewInboxJsonContext.Default.ReviewHostedBundle); }
        catch (JsonException) { throw Unreadable(); }
        if (bundle is null || bundle.Id != reference) throw Invalid();
        if (bundle.Repository is not { Length: > 0 and <= 256 } || bundle.RequestedBy is not { Length: > 0 and <= 320 }) throw Invalid();
        if (!IsKnownStatus(bundle.Status)) throw Invalid();
        if (!CommitSignatureFormat.IsObjectId(bundle.TreeId) || !CommitSignatureFormat.IsObjectId(bundle.BaseTree)
            || (bundle.BaseCommit is not null && !CommitSignatureFormat.IsObjectId(bundle.BaseCommit))) throw Invalid();
        if (bundle.CardKey is { Length: > 64 } || bundle.Note is { Length: > 4000 } || bundle.Note?.Contains('\0') == true) throw Invalid();
        if (bundle.CreatedUtc == default || bundle.ExpiresUtc <= bundle.CreatedUtc) throw Invalid();
        if (bundle.Statement is not null && bundle.Statement != Encoding.UTF8.GetString(CommitSignatureFormat.BuildPayload(bundle.TreeId))) throw Invalid();

        var files = bundle.Files ?? [];
        if (files.Count is 0 or > ReviewTreeRebuilder.MaxFiles) throw Invalid();
        long total = 0;
        foreach (var file in files)
        {
            if (file is null || !IsPath(file.Path) || (file.PreviousPath is not null && !IsPath(file.PreviousPath))) throw Invalid();
            if (file.Change is not ("added" or "modified" or "deleted" or "renamed" or "copied" or "unmerged" or "unknown")) throw Invalid();
            if (file.ChangedLines is < 0) throw Invalid();
            if (file.Omitted is not null and not (ReviewBundleOmission.Binary or ReviewBundleOmission.Oversized or ReviewBundleOmission.BundleLimit)) throw Invalid();
            if (file.Binary && file.Omitted != ReviewBundleOmission.Binary) throw Invalid();
            if (file.Omitted is not null && (file.Before is not null || file.After is not null)) throw Invalid();
            var size = Utf8(file.Before) + Utf8(file.After);
            if (size > ReviewBundleCapture.MaxFileContentBytes) throw Invalid();
            total += size;
            if (total > ReviewBundleCapture.MaxBundleContentBytes) throw Invalid();
        }
        var sessions = bundle.Sessions ?? [];
        if (sessions.Count > ReviewRequestService.MaxSessions) throw Invalid();
        foreach (var session in sessions)
        {
            if (session is null || session.Number is < 1 or > ReviewRequestService.MaxSessions) throw Invalid();
            if (session.Name is { Length: > 256 } || session.Cli is { Length: > 64 } || session.Status is { Length: > 32 }) throw Invalid();
        }
        var decisions = bundle.YourDecisions ?? [];
        if (decisions.Count > MaxDecisions) throw Invalid();
        var ids = new HashSet<long>();
        foreach (var decision in decisions)
        {
            ValidateDecision(decision);
            if (!ids.Add(decision.Id)) throw Invalid();
        }
        return bundle with { Files = files, Sessions = sessions, YourDecisions = decisions };
    }

    /// <summary>One decision as the host answers a reviewer's post.</summary>
    public static ReviewRequestHostedDecision ParseDecision(ReadOnlySpan<byte> json)
    {
        ReviewRequestHostedDecision? decision;
        try { decision = JsonSerializer.Deserialize(json, ReviewInboxJsonContext.Default.ReviewRequestHostedDecision); }
        catch (JsonException) { throw Unreadable(); }
        ValidateDecision(decision);
        return decision!;
    }

    private static bool IsKnownStatus(string? status) => status is ReviewRequestStatus.Open or ReviewRequestStatus.Approved
        or ReviewRequestStatus.Rejected or ReviewRequestStatus.Cancelled or "expired";

    /// <summary>Git paths are exact: 1 to 4,096 characters, anything but NUL.</summary>
    private static bool IsPath(string? path) => path is { Length: > 0 and <= 4096 } && !path.Contains('\0');

    private static int Utf8(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);

    private static ReviewRequestTransportException NotAddressed() => new("not_found",
        "viberails.ai has no open review request with this reference for your account. It may have been cancelled or expired, or it is not addressed to your verified email.", 404);

    /// <summary>The host's refusals of a decision keep their code; the wording is local.</summary>
    private static ReviewRequestTransportException DecisionFailure(HttpStatusCode status, string? code)
    {
        var number = (int)status;
        return number switch
        {
            400 => new("invalid_request", "viberails.ai rejected the decision as malformed (HTTP 400). Update VibeRails and try again.", number),
            409 => code switch
            {
                "tree_mismatch" => new("tree_mismatch", "The signature covers a different tree than the review request.", number),
                "signature_rejected" => new("signature_rejected", "viberails.ai did not accept the signature for this request's tree.", number),
                "signer_mismatch" => new("signer_mismatch", "The signing key belongs to another viberails.ai account. Sign with a key registered to the account the request is addressed to.", number),
                "not_a_reviewer" => new("not_a_reviewer", "Your account is not a listed reviewer of this request, or its email is not verified.", number),
                "not_open" => new("not_open", "This review request was cancelled or has expired, so no decision can be recorded.", number),
                "decision_limit" => new("decision_limit", "This review request has reached its limit of recorded decisions.", number),
                _ => new("request_conflict", "viberails.ai refused the decision because the request changed meanwhile. Open it again.", number)
            },
            503 => new("verification_unavailable", "viberails.ai could not confirm the signer's identity right now. Try again shortly.", number),
            _ => Failure(status, code)
        };
    }

    /// <summary>
    /// Validates a hosted status answer field by field: a well-formed reference (equal to
    /// <paramref name="expectedReference"/> when given), this request's id and tree when given, a known
    /// status, a plausible expiry, and at most <see cref="MaxDecisions"/> distinct decisions whose
    /// reviewer, kind, comment and trailer are within the desktop's own bounds. Public so the
    /// cross-repository round trip can parse the hosted service's JSON the way the desktop does.
    /// </summary>
    public static ReviewRequestHostedStatus ParseStatus(ReadOnlySpan<byte> json, string? expectedRequestId, string? expectedTreeId, string? expectedReference)
    {
        ReviewRequestHostedStatus? remote;
        try { remote = JsonSerializer.Deserialize(json, ReviewRequestJsonContext.Default.ReviewRequestHostedStatus); }
        catch (JsonException) { throw Unreadable(); }
        if (remote is null || !IsReference(remote.Id)) throw Invalid();
        if (expectedReference is not null && remote.Id != expectedReference) throw Invalid();
        if (expectedRequestId is not null && (remote.RequestId is not { } requestId || requestId.ToString("D") != expectedRequestId)) throw Invalid();
        if (!CommitSignatureFormat.IsObjectId(remote.TreeId) || (expectedTreeId is not null && remote.TreeId != expectedTreeId)) throw Invalid();
        if (remote.Status is not (ReviewRequestStatus.Open or ReviewRequestStatus.Approved or ReviewRequestStatus.Rejected
            or ReviewRequestStatus.Cancelled or "expired")) throw Invalid();
        if (remote.CreatedUtc == default || remote.ExpiresUtc <= remote.CreatedUtc || remote.ExpiresUtc > remote.CreatedUtc.AddDays(32)) throw Invalid();
        var decisions = remote.Decisions ?? [];
        if (decisions.Count > MaxDecisions) throw Invalid();
        var ids = new HashSet<long>();
        foreach (var decision in decisions)
        {
            ValidateDecision(decision);
            if (!ids.Add(decision.Id)) throw Invalid();
        }
        return remote with { Decisions = decisions };
    }

    /// <summary>A hosted decision within the desktop's own bounds: a listed kind, an email, a parsable trailer on an approval, a bounded comment.</summary>
    private static void ValidateDecision(ReviewRequestHostedDecision? decision)
    {
        if (decision is null || decision.Id <= 0) throw Invalid();
        if (decision.Reviewer is not { Length: <= 320 } || !HumanSignatureRule.IsEmail(decision.Reviewer)) throw Invalid();
        if (decision.Decision is not (ReviewDecisionKind.Approved or ReviewDecisionKind.Rejected)) throw Invalid();
        if (decision.Comment is { Length: > ReviewRequestService.MaxCommentChars }) throw Invalid();
        if (decision.Trailer is { } trailer && (trailer.Length > 1024 || !CommitSignatureFormat.TryParseTrailer(trailer, out _))) throw Invalid();
        if (decision.Decision == ReviewDecisionKind.Approved && decision.Trailer is null) throw Invalid();
        if (decision.DecidedUtc == default) throw Invalid();
    }

    private sealed record Answer(HttpStatusCode Status, byte[] Body, string? Code);

    private async Task<Answer> ExchangeAsync(string apiKey, HttpMethod method, string path, HttpContent? body, TimeSpan deadline, CancellationToken ct,
        int maxResponseBytes = MaxResponseBytes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(deadline);
        try
        {
            using var request = new HttpRequestMessage(method, Endpoint.AbsoluteUri + path) { Content = body };
            request.Headers.Add("X-Api-Key", apiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                var bytes = await SessionSharingService.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), maxResponseBytes, timeout.Token);
                return new(response.StatusCode, bytes, null);
            }
            return new(response.StatusCode, [], await ErrorCodeAsync(response, timeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ReviewRequestTransportException("timeout", "viberails.ai did not respond in time. Check your connection and try again.", null); }
        catch (HttpRequestException ex)
        {
            throw new ReviewRequestTransportException("network_error", ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "Could not find viberails.ai. Check your internet connection and DNS settings.",
                HttpRequestError.SecureConnectionError => "Could not establish a secure connection to viberails.ai. Check your device clock and network, or contact the server administrator.",
                _ => "Could not connect to viberails.ai. Check your internet connection and try again."
            }, null);
        }
        catch (InvalidDataException)
        { throw new ReviewRequestTransportException("invalid_response", "viberails.ai returned a response larger than expected. Contact the server administrator.", null); }
        catch (IOException)
        { throw new ReviewRequestTransportException("network_error", "The connection to viberails.ai was interrupted. Check your connection and try again.", null); }
    }

    /// <summary>Only a short JSON <c>code</c> is read from an error body; prose, SQL and headers are never read or echoed.</summary>
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            && mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) != true) return null;
        try
        {
            var bytes = await SessionSharingService.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), MaxErrorBodyBytes, ct);
            var code = JsonSerializer.Deserialize(bytes, ReviewRequestJsonContext.Default.ReviewRequestHostedError)?.Code;
            return code is { Length: <= 64 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or OperationCanceledException)
        {
            return null; // The HTTP status still selects a fixed message.
        }
    }

    /// <summary>Wraps the stored bundle bytes without serializing them again; the host re-validates every field.</summary>
    private static HttpContent Envelope(string requestId, byte[] bundleJson)
    {
        var buffer = new ArrayBufferWriter<byte>(bundleJson.Length + 128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", requestId);
            writer.WritePropertyName("bundle");
            writer.WriteRawValue(bundleJson, skipInputValidation: true);
            writer.WriteEndObject();
        }
        var content = new ReadOnlyMemoryContent(buffer.WrittenMemory);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static void RequireReference(string remoteId)
    {
        if (!IsReference(remoteId)) throw new ReviewRequestTransportException("invalid_request", "The stored hosted reference is not valid.", null);
    }

    private static ReviewRequestTransportException Invalid() =>
        new("invalid_response", "viberails.ai returned an invalid review-request response. Check that the server and VibeRails are up to date.", null);

    private static ReviewRequestTransportException Unreadable() =>
        new("invalid_response", "viberails.ai returned an unreadable review-request response. Check that the server and VibeRails are up to date.", null);

    /// <summary>Known codes select fixed wording; unknown codes, remote prose and redirect targets never appear.</summary>
    private static ReviewRequestTransportException Failure(HttpStatusCode status, string? code)
    {
        var number = (int)status;
        return number switch
        {
            401 => new("invalid_api_key", code switch
            {
                "api_key_expired" => "Your VibeRails account key has expired. Sign in again before sending review requests.",
                "api_key_revoked" => "Your VibeRails account key has been revoked. Sign in again before sending review requests.",
                _ => "viberails.ai rejected your account key. Sign in again before sending review requests."
            }, number),
            403 => new("permission_denied", "Your account key does not allow review requests. Sign in with a key that includes Sessions access.", number),
            404 or 405 or 501 => new("server_update_required",
                $"Review requests are not available on viberails.ai yet (HTTP {number}). Nothing was sent. Ask the server administrator to deploy the review-request update.", number),
            400 or 422 => new("invalid_request", $"viberails.ai rejected the review bundle (HTTP {number}). Update VibeRails and try again; nothing was sent.", number),
            409 => new("request_conflict", "viberails.ai already holds a different bundle under this request id. Create a new review request.", number),
            413 => new("too_large", "The review bundle exceeds the viberails.ai transfer limit. Nothing was sent.", number),
            429 => new("rate_limited", "Too many review requests. Try again in a minute.", number),
            502 or 503 or 504 => new("server_unavailable", $"viberails.ai is temporarily unavailable (HTTP {number}). Try again shortly, or contact the server administrator if it continues.", number),
            >= 300 and < 400 => new("unexpected_redirect", $"viberails.ai returned an unexpected redirect (HTTP {number}). Ask the server administrator to check the deployment.", number),
            _ => new("server_error", $"viberails.ai could not complete the review request (HTTP {number}). Ask the server administrator to check the server logs.", number)
        };
    }
}

/// <summary>A transport failure in fixed local wording. <see cref="Code"/> is stable for callers and tests; <see cref="HttpStatus"/> is the upstream status when there was one.</summary>
public sealed class ReviewRequestTransportException(string code, string message, int? httpStatus) : Exception(message)
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;

    /// <summary>The host could not be reached or answered that it is down; the caller backs off rather than retrying at once.</summary>
    public bool IsUnreachable => Code is "network_error" or "timeout" or "server_unavailable";
}
