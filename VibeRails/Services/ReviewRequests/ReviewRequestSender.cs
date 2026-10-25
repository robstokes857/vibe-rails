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
/// Cancelling and deleting keep the hosted copy's revocation durable: a request whose hosted copy is
/// not yet cancelled is never deleted locally, because its row is the only thing that can still cancel it.
/// </summary>
public sealed class ReviewRequestSender(
    IReviewRequestStore store,
    ReviewRequestService reviews,
    ReviewRequestTransport transport,
    ISessionArchiveReader archives,
    IAppEventBus events,
    ReviewRequestPollState polls,
    Func<string?> apiKey,
    TimeProvider? clock = null)
{
    /// <summary>What a reviewer gets from an attached session; shown beside each session name before the send.</summary>
    public const string SessionDisclosure =
        "Each attached session uploads to your viberails.ai account after it ends: its terminal output, your prompts, "
        + "and the code changes saved during it. The listed reviewers can replay it while the request is open.";

    /// <summary>Reviewers are always listed people; a request is never a public link.</summary>
    public const string Audience = "email";

    /// <summary>The event status for a request that no longer exists locally.</summary>
    public const string DeletedStatus = "deleted";

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
    /// wording. A request that was already sent is answered without another transfer, but an
    /// interrupted first send has its sessions queued now, under the key that sent it.
    /// </summary>
    public async Task<ReviewRequestSent> SendAsync(string projectPath, string id, SendReviewRequest? body, CancellationToken ct)
    {
        var record = await reviews.RequireAsync(projectPath, id, ct);
        if (body?.Confirmed != true)
            throw new ReviewRequestException("confirmation_required",
                "Read what will leave this computer (the request's disclosure) and send again with \"confirmed\": true. Nothing was sent.", 400);
        if (record.Status != ReviewRequestStatus.Open)
            throw new ReviewRequestException("not_open", $"This review request is {record.Status}; only an open request can be sent.", 409);
        var bundle = ReviewRequestService.Bundle(record);
        if (record.RemoteId is not null)
        {
            // Already sent: never post the bundle again. A send interrupted between the hosted
            // confirmation and the session queue is finished here, under the fingerprint it was sent with.
            var resumed = record.SessionsQueuedUTC is null;
            if (resumed)
                await QueueSessionsAsync(store, archives, record.Id, bundle, record.SentKeyFingerprint!, _clock.GetUtcNow().UtcDateTime, ct);
            return new ReviewRequestSent(await reviews.SummaryAsync(record.Id, ct),
                resumed ? "This review request was already sent. Its attached sessions are queued to upload now." : "This review request was already sent.");
        }
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new ReviewRequestException("no_api_key", "Sign in to your VibeRails account in Settings before sending a review request. Nothing was sent.", 409);
        // The key is captured once: polling, uploads and cancellation stay bound to the account that
        // actually received the request, even if Settings change while this call is in flight.
        var fingerprint = SessionSharingService.KeyFingerprint(key);

        ReviewRequestHostedStatus remote;
        try { remote = await transport.SendAsync(key, record.Id, record.TreeId, Encoding.UTF8.GetBytes(record.BundleJson), ct); }
        catch (ReviewRequestTransportException ex) { throw new ReviewRequestException(ex.Code, ex.Message, 502); }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (!await store.MarkSentAsync(record.Id, remote.Id!, fingerprint, now, remote.ExpiresUtc, ct))
        {
            if (await store.GetAsync(record.Id, ct) is null)
            {
                // Deleted while the send was in flight. No local row can revoke the hosted copy later,
                // so revoke it now, before anyone is told about it.
                try { await transport.CancelAsync(key, remote.Id!, ct); }
                catch (ReviewRequestTransportException ex)
                {
                    // The hosted copy is alive and nothing local points at it. Put the row back,
                    // cancelled, carrying the reference, the sending key's fingerprint and the expiry:
                    // the poller retries the cancellation and Delete refuses until the host confirms
                    // (Codex slice E review, F2). Nothing is queued for upload for it.
                    Log.Warning("[ReviewRequests] Request {Id} was deleted during its send and its hosted copy could not be cancelled yet ({Code}); keeping it cancelled for the retry",
                        record.Id, ex.Code);
                    // AddAsync writes the request columns; the hosted columns go through the same
                    // marks a normal send uses, so the row reads exactly like a sent-then-cancelled one.
                    await store.AddAsync(record with { Status = ReviewRequestStatus.Cancelled, UpdatedUTC = now }, ct);
                    await store.MarkSentAsync(record.Id, remote.Id!, fingerprint, now, remote.ExpiresUtc, ct);
                    await store.MarkSessionsQueuedAsync(record.Id, now, ct);
                    polls.Prompt(fingerprint);
                    Publish(record.Id, ReviewRequestStatus.Cancelled, record.CardKey, true);
                    throw new ReviewRequestException("remote_pending",
                        "This review request was deleted while it was being sent, and its copy on viberails.ai is not cancelled yet. "
                        + "The request is kept here, cancelled, and the cancellation is retried automatically; delete it again once viberails.ai confirms.", 409);
                }
                throw new ReviewRequestException("not_found", "This review request was deleted while it was being sent. Its hosted copy was cancelled.", 404);
            }
            // Another root sent this request first and recorded a different reference. The host
            // answers an exact retry with the stored request, so this cannot happen for one account;
            // follow what is stored rather than overwrite what the poller is already following.
            Log.Warning("[ReviewRequests] Request {Id} was already sent under another reference; keeping the stored one", record.Id);
            return new ReviewRequestSent(await reviews.SummaryAsync(record.Id, ct), "This review request was already sent.");
        }
        // The first decision check happens one interval from now, not after the idle backoff.
        polls.Prompt(fingerprint);

        var (queued, missing) = await QueueSessionsAsync(store, archives, record.Id, bundle, fingerprint, now, ct);
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
        if (await TryCancelRemoteAsync(id, ct) is { } failure)
            Log.Information("[ReviewRequests] The hosted copy of request {Id} is not cancelled yet ({Code}); it will be retried", id, failure);
        else
            summary = await reviews.SummaryAsync(id, ct);
        events.Publish(ReviewRequestEvents.Updated, new ReviewRequestChanged(summary.Id, summary.Status, summary.CardKey, summary.Remote is not null),
            ReviewRequestJsonContext.Default.ReviewRequestChanged);
        return summary;
    }

    /// <summary>
    /// Deletes a request. A sent request's row is the only thing that can still cancel its hosted
    /// copy, so one whose hosted copy is not cancelled (and not expired) is cancelled first, here and
    /// now; when the host cannot be reached, or the request was sent with another account's key, the
    /// delete is refused (409 <c>remote_pending</c>) and the request stays, cancelled, for the poller
    /// to finish. Reviewers never keep access to a request the developer thinks is gone.
    /// </summary>
    public async Task DeleteAsync(string projectPath, string id, CancellationToken ct)
    {
        var record = await reviews.RequireAsync(projectPath, id, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (record is { RemoteId: not null, RemoteCancelledUTC: null } && !(record.ExpiresUTC is { } expires && expires <= now))
        {
            if (record.Status != ReviewRequestStatus.Cancelled) await reviews.CancelAsync(projectPath, id, ct);
            if (await TryCancelRemoteAsync(id, ct) is { } failure)
            {
                Publish(record.Id, ReviewRequestStatus.Cancelled, record.CardKey, true);
                throw new ReviewRequestException("remote_pending", failure switch
                {
                    "other_account" => "The copy on viberails.ai was sent with a different account key and is not cancelled yet. "
                        + "Sign in with that account to cancel it; the request is kept here, cancelled, until then.",
                    _ => "The copy on viberails.ai is not cancelled yet, so the request is kept here, cancelled, and the cancellation "
                        + "is retried automatically. Delete it again once viberails.ai confirms."
                }, 409);
            }
        }
        await reviews.DeleteAsync(projectPath, id, ct);
        Publish(record.Id, DeletedStatus, record.CardKey, record.RemoteId is not null);
    }

    /// <summary>
    /// Queues every attached session under the key that sent the request and records completion, so
    /// a send interrupted after the hosted confirmation is finished by a retry or by the poller.
    /// </summary>
    internal static async Task<(int Queued, int Missing)> QueueSessionsAsync(IReviewRequestStore store, ISessionArchiveReader archives,
        string requestId, ReviewBundle bundle, string fingerprint, DateTime now, CancellationToken ct)
    {
        var queued = 0;
        var missing = 0;
        foreach (var session in bundle.Sessions)
        {
            if (await archives.EnsureSessionShareUploadAsync(session.Id, fingerprint, now, ct)) queued++;
            else missing++;
        }
        await store.MarkSessionsQueuedAsync(requestId, now, ct);
        return (queued, missing);
    }

    /// <summary>Null when the hosted copy is cancelled (now or before); otherwise why not: a transport code or <c>other_account</c>.</summary>
    private async Task<string?> TryCancelRemoteAsync(string id, CancellationToken ct)
    {
        var record = await store.GetAsync(id, ct);
        if (record is not { RemoteId: { } remoteId, RemoteCancelledUTC: null }) return null;
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key) || SessionSharingService.KeyFingerprint(key) != record.SentKeyFingerprint) return "other_account";
        try
        {
            await transport.CancelAsync(key, remoteId, ct);
            await store.MarkRemoteCancelledAsync(id, _clock.GetUtcNow().UtcDateTime, ct);
            return null;
        }
        catch (ReviewRequestTransportException ex)
        {
            return ex.Code; // Fixed local wording only; the poller retries on its cadence.
        }
    }

    private void Publish(string id, string status, string? cardKey, bool sent) =>
        events.Publish(ReviewRequestEvents.Updated, new ReviewRequestChanged(id, status, cardKey, sent), ReviewRequestJsonContext.Default.ReviewRequestChanged);

    private static int Utf8(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);
}
