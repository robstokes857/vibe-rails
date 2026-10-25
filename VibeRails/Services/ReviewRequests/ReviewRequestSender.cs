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
        long textBytes = 0, carriedBytes = 0;
        var withText = 0;
        var withBytes = 0;
        foreach (var file in bundle.Files)
        {
            var included = file.Omitted is null;
            // A file listed without text may still send its exact bytes (VIBE-211); the person sees that too.
            var bytesIncluded = file.AfterBytes is not null;
            if (included) withText++;
            if (bytesIncluded)
            {
                withBytes++;
                carriedBytes += ReviewBundleCapture.DecodedLength(file.AfterBytes);
            }
            textBytes += Utf8(file.Before) + Utf8(file.After);
            files.Add(new ReviewDisclosureFile(file.Path, file.Change, file.PreviousPath, file.ChangedLines, included, file.Omitted, bytesIncluded));
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
            attached, SessionDisclosure, bundle.Reviewers, bundle.CardKey, bundle.Note, expires, remote is not null, remote,
            withBytes, carriedBytes);
    }

    /// <summary>
    /// Sends an open request once its disclosure was confirmed. Refuses without confirmation (400
    /// <c>confirmation_required</c>), for a request that is not open (409 <c>not_open</c>) or without an
    /// account key (409 <c>no_api_key</c>); transport failures answer 502 with the transport's code and
    /// wording. A request that was already sent is answered without another transfer, but an
    /// interrupted first send has its sessions queued now, under the key that sent it. The attempt
    /// and the sending key's fingerprint are recorded before the POST (VIBE-212): a refusal the host
    /// confirmed (a 4xx answer) clears them, while a lost or unreadable answer keeps them, so the
    /// request is not deletable until the poller has recovered or refuted the hosted copy; a retry
    /// with the same key posts again (the host answers an exact retry with the stored request) and
    /// one with another key is refused (409 <c>send_pending</c>). On such a retry only a refusal that
    /// also refutes the first POST (<see cref="RefutesEarlierDelivery"/>) clears the attempt. A request
    /// cancelled while its bundle was in flight keeps the reference only to revoke the hosted copy,
    /// and nothing is queued for upload.
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
        if (record.RemoteId is not null) return await AlreadySentAsync(record, bundle, ct);
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new ReviewRequestException("no_api_key", "Sign in to your VibeRails account in Settings before sending a review request. Nothing was sent.", 409);
        // The key is captured once: polling, uploads and cancellation stay bound to the account that
        // actually received the request, even if Settings change while this call is in flight.
        var fingerprint = SessionSharingService.KeyFingerprint(key);

        // The person confirmed: from here the transfer, its persistence and any recovery run to
        // completion on the transport's own deadlines, never the caller's token. A browser that
        // disconnects after the host accepted the bundle must not leave a hosted copy nothing local
        // points at (Codex slice E review, F2: the window between the hosted success and MarkSent).
        var none = CancellationToken.None;
        var now = _clock.GetUtcNow().UtcDateTime;
        // A row that already carries an attempt is a retry of a send whose answer was lost: that first
        // POST may have landed, so a refusal of this one proves nothing about it unless the content or
        // the route decided it, the poller's rule (VIBE-213, item 1).
        var earlierAttempt = record.SendAttemptedUTC;
        // The attempt is durable before the bundle leaves (VIBE-212, Codex VIBE-209 fourth pass F2): a
        // POST the host commits but whose answer is lost would otherwise look unsent, be deletable without
        // the host, and stay readable by its reviewers until expiry. The claim also pins the account: a
        // retry under the same key posts again (the host answers an exact retry with the stored request),
        // while a send under another key is refused so it cannot create a second hosted copy.
        if (!await store.MarkSendAttemptAsync(record.Id, fingerprint, now, none))
        {
            var current = await store.GetAsync(record.Id, none)
                ?? throw new ReviewRequestException("not_found", "Review request not found.", 404);
            if (current.RemoteId is not null) return await AlreadySentAsync(current, bundle, none);
            throw new ReviewRequestException("send_pending",
                "A send of this review request with a different account key has not been confirmed or refused by viberails.ai yet. "
                + "Sign in with that account to retry it, or wait for the automatic check. Nothing was sent.", 409);
        }
        ReviewRequestHostedStatus remote;
        try { remote = await transport.SendAsync(key, record.Id, record.TreeId, Encoding.UTF8.GetBytes(record.BundleJson), none); }
        catch (ReviewRequestTransportException ex) when (earlierAttempt is null ? IsConfirmedRefusal(ex) : RefutesEarlierDelivery(ex))
        {
            // The host answered and did not take the bundle: the request is an unsent one again. Only
            // the attempt this call answered for is cleared; one another send claimed first stays.
            if (await store.ClearSendAttemptAsync(record.Id, fingerprint, earlierAttempt ?? now, _clock.GetUtcNow().UtcDateTime, none))
                throw new ReviewRequestException(ex.Code, ex.Message, 502);
            throw Unconfirmed(record, fingerprint, ex);
        }
        catch (ReviewRequestTransportException ex)
        {
            // No answer, an unreadable one, a redirect or a server error, or a retry the host refused for
            // the call rather than the content: the host may hold the request. The attempt stays, Delete
            // refuses until it is settled, and the poller posts the stored bundle again under this
            // fingerprint to recover the reference (or learns it was refused).
            throw Unconfirmed(record, fingerprint, ex);
        }

        now = _clock.GetUtcNow().UtcDateTime;
        var status = await store.MarkSentAsync(record.Id, remote.Id!, fingerprint, now, remote.ExpiresUtc, none);
        if (status is null)
        {
            if (await store.GetAsync(record.Id, none) is null)
            {
                // Deleted while the send was in flight: the hosted copy exists and nothing local points
                // at it. The reference is made durable first, with its own token, so a caller that
                // disconnects now cannot take the only revocation record with it (Codex slice E review,
                // F2); only then is the hosted copy cancelled, on the transport's own bounded deadline.
                await RestoreForRevocationAsync(record, remote, fingerprint, now);
                try
                {
                    await transport.CancelAsync(key, remote.Id!, CancellationToken.None);
                }
                catch (Exception ex) when (ex is ReviewRequestTransportException or OperationCanceledException)
                {
                    // The poller retries the cancellation and Delete refuses until the host confirms.
                    Log.Warning("[ReviewRequests] Request {Id} was deleted during its send and its hosted copy could not be cancelled yet ({Code}); keeping it cancelled for the retry",
                        record.Id, (ex as ReviewRequestTransportException)?.Code ?? "timeout");
                    polls.Prompt(fingerprint);
                    Publish(record.Id, ReviewRequestStatus.Cancelled, record.CardKey, true);
                    throw new ReviewRequestException("remote_pending",
                        "This review request was deleted while it was being sent, and its copy on viberails.ai is not cancelled yet. "
                        + "The request is kept here, cancelled, and the cancellation is retried automatically; delete it again once viberails.ai confirms.", 409);
                }
                await store.MarkRemoteCancelledAsync(record.Id, _clock.GetUtcNow().UtcDateTime, CancellationToken.None);
                await store.DeleteAsync(record.Id, CancellationToken.None);
                throw new ReviewRequestException("not_found", "This review request was deleted while it was being sent. Its hosted copy was cancelled.", 404);
            }
            // Another root sent this request first and recorded a different reference. The host
            // answers an exact retry with the stored request, so this cannot happen for one account;
            // follow what is stored rather than overwrite what the poller is already following.
            Log.Warning("[ReviewRequests] Request {Id} was already sent under another reference; keeping the stored one", record.Id);
            return new ReviewRequestSent(await reviews.SummaryAsync(record.Id, none), "This review request was already sent.");
        }
        if (status == ReviewRequestStatus.Cancelled) return await RevokeCancelledInFlightAsync(record, remote, key, fingerprint, now);
        // The first decision check happens one interval from now, not after the idle backoff.
        polls.Prompt(fingerprint);

        var (queued, missing) = await QueueSessionsAsync(store, archives, record.Id, bundle, fingerprint, now, none);
        Publish(record.Id, status, record.CardKey, true);
        var message = missing > 0
            ? "Sent. Some attached sessions are no longer on this computer and cannot upload; reviewers see them as not uploaded. Decisions are checked every 15 minutes."
            : queued > 0
                ? "Sent. Attached sessions upload after they end; keep VibeRails open until they do. Decisions are checked every 15 minutes."
                : "Sent. Decisions are checked every 15 minutes.";
        return new ReviewRequestSent(await reviews.SummaryAsync(record.Id, none), message);
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
    /// to finish. A request whose send was never answered is refused the same way (409
    /// <c>send_pending</c>) until the poller has recovered its reference and revoked the copy, or
    /// learned that the host refused it. Reviewers never keep access to a request the developer
    /// thinks is gone.
    /// </summary>
    public async Task DeleteAsync(string projectPath, string id, CancellationToken ct)
    {
        var record = await reviews.RequireAsync(projectPath, id, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (record.SendUncertain)
        {
            // A send began and its answer never arrived: the host may hold a copy that only this row
            // can still find (VIBE-212). The request is cancelled here so it takes no decisions and kept
            // so the poller can recover the reference under the pinned key and revoke the copy; the
            // delete is refused until that has happened.
            if (record.Status != ReviewRequestStatus.Cancelled) await reviews.CancelAsync(projectPath, id, ct);
            var pinned = record.SentKeyFingerprint!;
            var currentKey = apiKey();
            var sameAccount = !string.IsNullOrWhiteSpace(currentKey) && SessionSharingService.KeyFingerprint(currentKey) == pinned;
            if (sameAccount) polls.Prompt(pinned);
            Publish(record.Id, ReviewRequestStatus.Cancelled, record.CardKey, false);
            throw new ReviewRequestException("send_pending",
                "This review request was sent but the answer from viberails.ai was lost, so it may hold a copy. The request is kept here, "
                + "cancelled, until viberails.ai confirms whether it does; a copy is then cancelled automatically. Delete it again after that. "
                + CancelledApprovalNote
                + (sameAccount ? "" : " The send was made with a different account key; sign in with that account so the check can run."), 409);
        }
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
                } + " " + CancelledApprovalNote, 409);
            }
        }
        await reviews.DeleteAsync(projectPath, id, ct);
        Publish(record.Id, DeletedStatus, record.CardKey, record.RemoteId is not null);
    }

    /// <summary>
    /// Appended to a refused Delete (409 <c>remote_pending</c> or <c>send_pending</c>): the request is
    /// cancelled locally before the host is asked, and the commit hook never applies an approval from
    /// a cancelled request (Rob's decision, 2026-10-25, VIBE-213 item 5).
    /// </summary>
    internal const string CancelledApprovalNote =
        "It is cancelled here, so any approval it received no longer applies to commits.";

    /// <summary>Appended to the transport's wording when a send's answer was lost, so the person knows the request is neither sent nor unsent yet.</summary>
    internal const string UnconfirmedSendNote =
        "viberails.ai may have received this request. VibeRails checks automatically and completes or clears the send; "
        + "until then the request cannot be deleted. Send again to retry now.";

    /// <summary>
    /// The host answered this very POST with a 4xx: it did not take the bundle, so nothing hosted can
    /// exist for it. Anything else (no answer, an unreadable or truncated 2xx body, a redirect, a 5xx
    /// from the host or a gateway in front of it) leaves delivery unknown.
    /// </summary>
    internal static bool IsConfirmedRefusal(ReviewRequestTransportException ex) => ex.HttpStatus is >= 400 and < 500;

    /// <summary>
    /// Whether a refused retry of the same bundle also proves the earlier POST never landed. Only
    /// refusals decided by the content or the route qualify: a malformed or oversized bundle, a host
    /// without the route, or another bundle under this id. A key the host now rejects, a missing
    /// permission or a rate limit refuse the call, not the content, so the earlier POST may have landed
    /// and the attempt is kept.
    /// </summary>
    internal static bool RefutesEarlierDelivery(ReviewRequestTransportException ex) =>
        ex.HttpStatus is 400 or 404 or 405 or 409 or 413 or 422 or 501;

    /// <summary>The send may have landed: the attempt stays for the poller, which is prompted, and the person is told so.</summary>
    private ReviewRequestException Unconfirmed(ReviewRequestRecord record, string fingerprint, ReviewRequestTransportException ex)
    {
        Log.Information("[ReviewRequests] The send of request {Id} was not confirmed ({Code}); keeping the attempt for reconciliation", record.Id, ex.Code);
        polls.Prompt(fingerprint);
        Publish(record.Id, record.Status, record.CardKey, false);
        return new ReviewRequestException(ex.Code, ex.Message + " " + UnconfirmedSendNote, 502);
    }

    /// <summary>
    /// The request was cancelled while its bundle was in flight (VIBE-213, item 3). The reference is
    /// already stored, so the hosted copy is revoked now, and no session is ever queued: the person
    /// cancelled before anything was confirmed. A cancellation the host does not confirm is retried
    /// by the poller, and Delete waits for it.
    /// </summary>
    private async Task<ReviewRequestSent> RevokeCancelledInFlightAsync(ReviewRequestRecord record, ReviewRequestHostedStatus remote,
        string key, string fingerprint, DateTime now)
    {
        var none = CancellationToken.None;
        await store.MarkSessionsQueuedAsync(record.Id, now, none);
        string message;
        try
        {
            await transport.CancelAsync(key, remote.Id!, none);
            await store.MarkRemoteCancelledAsync(record.Id, _clock.GetUtcNow().UtcDateTime, none);
            message = "This review request was cancelled while it was being sent. Its copy on viberails.ai was cancelled, and no session uploads.";
        }
        catch (Exception ex) when (ex is ReviewRequestTransportException or OperationCanceledException)
        {
            Log.Warning("[ReviewRequests] Request {Id} was cancelled during its send and its hosted copy could not be cancelled yet ({Code}); the poller retries",
                record.Id, (ex as ReviewRequestTransportException)?.Code ?? "timeout");
            polls.Prompt(fingerprint);
            message = "This review request was cancelled while it was being sent. Its copy on viberails.ai is not cancelled yet; "
                + "the cancellation is retried automatically. No session uploads.";
        }
        Publish(record.Id, ReviewRequestStatus.Cancelled, record.CardKey, true);
        return new ReviewRequestSent(await reviews.SummaryAsync(record.Id, none), message);
    }

    /// <summary>
    /// Already sent: never post the bundle again. A send interrupted between the hosted confirmation and
    /// the session queue is finished here, under the fingerprint it was sent with.
    /// </summary>
    private async Task<ReviewRequestSent> AlreadySentAsync(ReviewRequestRecord record, ReviewBundle bundle, CancellationToken ct)
    {
        var resumed = record.SessionsQueuedUTC is null;
        if (resumed)
            await QueueSessionsAsync(store, archives, record.Id, bundle, record.SentKeyFingerprint!, _clock.GetUtcNow().UtcDateTime, ct);
        return new ReviewRequestSent(await reviews.SummaryAsync(record.Id, ct),
            resumed ? "This review request was already sent. Its attached sessions are queued to upload now." : "This review request was already sent.");
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

    /// <summary>
    /// Puts a deleted request's row back so its hosted copy can still be revoked: open first, then
    /// marked sent with the reference, the sending key's fingerprint and the expiry, then cancelled.
    /// Each step leaves a recoverable state if the process dies between them: an open unsent request
    /// the host answers with the same stored reference on its next send, or a sent one the poller
    /// follows. Nothing is queued for upload. Never cancelled by the caller.
    /// </summary>
    private async Task RestoreForRevocationAsync(ReviewRequestRecord record, ReviewRequestHostedStatus remote, string fingerprint, DateTime now)
    {
        var none = CancellationToken.None;
        await store.AddAsync(record with { Status = ReviewRequestStatus.Open, UpdatedUTC = now }, none);
        await store.MarkSentAsync(record.Id, remote.Id!, fingerprint, now, remote.ExpiresUtc, none);
        await store.MarkSessionsQueuedAsync(record.Id, now, none);
        await store.UpdateStatusAsync(record.Id, ReviewRequestStatus.Cancelled, now, none);
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
