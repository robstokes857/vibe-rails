using System.Text;
using Serilog;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Services.ReviewRequests;

/// <summary>
/// Sends a stored review request to viberails.ai (VB-50, slice C). "Send" is explicit: the
/// disclosure lists exactly what leaves this computer, and nothing is transferred until the caller
/// confirms. After the host confirms the request, the attached sessions are queued through the
/// existing <c>SessionShareUploads</c> path under the fingerprint of the key that sent the request,
/// so a later account change cannot retarget them, and the drain job uploads each one after it ends.
/// </summary>
public sealed class ReviewRequestSender(
    IReviewRequestStore store,
    ReviewRequestService reviews,
    ReviewRequestTransport transport,
    ISessionArchiveReader archives,
    IAppEventBus events,
    Func<string?> apiKey,
    TimeProvider? clock = null)
{
    /// <summary>What a reviewer gets from an attached session; shown beside each session name before the send.</summary>
    public const string SessionDisclosure =
        "Each attached session uploads to your viberails.ai account after it ends: its terminal output, your prompts, "
        + "and the code changes saved during it. The listed reviewers can replay it while the request is open.";

    /// <summary>Reviewers are always listed people; a request is never a public link.</summary>
    public const string Audience = "email";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The manifest of a send. Live sessions are re-read so the disclosure says whether each one still waits to end.</summary>
    public async Task<ReviewRequestDisclosure> DisclosureAsync(string projectPath, string id, ISessionStore? sessions, CancellationToken ct)
    {
        var detail = await reviews.GetAsync(projectPath, id, ct);
        var bundle = detail.Bundle;
        var files = new List<ReviewDisclosureFile>(bundle.Files.Count);
        long textBytes = 0;
        var withText = 0;
        foreach (var file in bundle.Files)
        {
            var included = file.Omitted is null;
            if (included) withText++;
            textBytes += Utf8(file.Before) + Utf8(file.After);
            files.Add(new ReviewDisclosureFile(file.Path, file.Change, file.PreviousPath, file.ChangedLines, included, file.Omitted));
        }
        var attached = new List<ReviewDisclosureSession>(bundle.Sessions.Count);
        foreach (var session in bundle.Sessions)
        {
            var ended = session.EndedUtc;
            if (ended is null && sessions is not null && await sessions.GetSessionByIdAsync(session.Id, ct) is { } current)
                ended = current.EndedUTC;
            attached.Add(new ReviewDisclosureSession(session.Id, session.Name, session.Cli, session.StartedUtc, ended, ended is null));
        }
        var remote = detail.Request.Remote;
        // The host keeps a request for one calendar month from the send; before the send this is the estimate.
        var expires = remote?.ExpiresUtc ?? _clock.GetUtcNow().UtcDateTime.AddMonths(1);
        return new ReviewRequestDisclosure(detail.Request.Id, ReviewRequestTransport.Destination, Audience, bundle.Repository,
            bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, files, withText, files.Count - withText, textBytes,
            attached, SessionDisclosure, bundle.Reviewers, bundle.CardKey, bundle.Note, expires, remote is not null, remote);
    }

    /// <summary>
    /// Sends an open request once its disclosure was confirmed. Refuses without confirmation (400
    /// <c>confirmation_required</c>), for a request that is not open (409 <c>not_open</c>) or without an
    /// account key (409 <c>no_api_key</c>); transport failures answer 502 with the transport's code and
    /// wording. A request that was already sent is answered without another transfer.
    /// </summary>
    public async Task<ReviewRequestSent> SendAsync(string projectPath, string id, SendReviewRequest? body, CancellationToken ct)
    {
        var record = await reviews.RequireAsync(projectPath, id, ct);
        if (body?.Confirmed != true)
            throw new ReviewRequestException("confirmation_required",
                "Read what will leave this computer (the request's disclosure) and send again with \"confirmed\": true. Nothing was sent.", 400);
        if (record.Status != ReviewRequestStatus.Open)
            throw new ReviewRequestException("not_open", $"This review request is {record.Status}; only an open request can be sent.", 409);
        if (record.RemoteId is not null)
            return new ReviewRequestSent(await reviews.SummaryAsync(record.Id, ct), "This review request was already sent.");
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new ReviewRequestException("no_api_key", "Sign in to your VibeRails account in Settings before sending a review request. Nothing was sent.", 409);
        // The key is captured once: polling, uploads and cancellation stay bound to the account that
        // actually received the request, even if Settings change while this call is in flight.
        var fingerprint = SessionSharingService.KeyFingerprint(key);
        var bundle = ReviewRequestService.Bundle(record);

        ReviewRequestHostedStatus remote;
        try { remote = await transport.SendAsync(key, record.Id, record.TreeId, Encoding.UTF8.GetBytes(record.BundleJson), ct); }
        catch (ReviewRequestTransportException ex) { throw new ReviewRequestException(ex.Code, ex.Message, 502); }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (!await store.MarkSentAsync(record.Id, remote.Id!, fingerprint, now, remote.ExpiresUtc, ct))
        {
            // Another root sent this request first and recorded a different reference. The host
            // answers an exact retry with the stored request, so this cannot happen for one account;
            // follow what is stored rather than overwrite what the poller is already following.
            Log.Warning("[ReviewRequests] Request {Id} was already sent under another reference; keeping the stored one", record.Id);
            return new ReviewRequestSent(await reviews.SummaryAsync(record.Id, ct), "This review request was already sent.");
        }

        var queued = 0;
        var missing = 0;
        foreach (var session in bundle.Sessions)
        {
            if (await archives.EnsureSessionShareUploadAsync(session.Id, fingerprint, now, ct)) queued++;
            else missing++;
        }
        events.Publish(ReviewRequestEvents.Updated, new ReviewRequestChanged(record.Id, record.Status, record.CardKey, true),
            ReviewRequestJsonContext.Default.ReviewRequestChanged);
        var message = missing > 0
            ? "Sent. Some attached sessions are no longer on this computer and cannot upload; reviewers see them as not uploaded. Decisions are checked every 15 minutes."
            : queued > 0
                ? "Sent. Attached sessions upload after they end; keep VibeRails open until they do. Decisions are checked every 15 minutes."
                : "Sent. Decisions are checked every 15 minutes.";
        return new ReviewRequestSent(await reviews.SummaryAsync(record.Id, ct), message);
    }

    /// <summary>
    /// Cancels locally first (the ledger refuses decisions from this moment), then asks the host to
    /// cancel its copy so reviewers lose access. A hosted cancellation that fails is retried by the
    /// poller; one sent with another account's key waits for that key.
    /// </summary>
    public async Task<ReviewRequestSummary> CancelAsync(string projectPath, string id, CancellationToken ct)
    {
        var summary = await reviews.CancelAsync(projectPath, id, ct);
        var record = await store.GetAsync(id, ct);
        if (record is { RemoteId: { } remoteId, RemoteCancelledUTC: null })
        {
            var key = apiKey();
            if (!string.IsNullOrWhiteSpace(key) && SessionSharingService.KeyFingerprint(key) == record.SentKeyFingerprint)
            {
                try
                {
                    await transport.CancelAsync(key, remoteId, ct);
                    await store.MarkRemoteCancelledAsync(id, _clock.GetUtcNow().UtcDateTime, ct);
                    summary = await reviews.SummaryAsync(id, ct);
                }
                catch (ReviewRequestTransportException ex)
                {
                    // Fixed local wording only; the poller retries on its cadence.
                    Log.Information("[ReviewRequests] The hosted copy of request {Id} is not cancelled yet ({Code}); it will be retried", id, ex.Code);
                }
            }
        }
        events.Publish(ReviewRequestEvents.Updated, new ReviewRequestChanged(summary.Id, summary.Status, summary.CardKey, summary.Remote is not null),
            ReviewRequestJsonContext.Default.ReviewRequestChanged);
        return summary;
    }

    private static int Utf8(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);
}
