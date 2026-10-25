namespace VibeRails.DB;

/// <summary>
/// A request for a person to review a set of changes before they are committed (VB-50). The row
/// keeps the identity that is approved (the tree id) beside the bundle a reviewer reads, which is
/// stored as JSON because its shape belongs to the application, not the database.
/// </summary>
/// <param name="Status">One of <c>open</c>, <c>approved</c>, <c>rejected</c>, <c>cancelled</c>.</param>
/// <param name="SentUTC">When viberails.ai confirmed the request; null while it exists only on this computer.</param>
/// <param name="RemoteId">The hosted reference (<c>rvw_</c> plus 32 hex characters) once sent.</param>
/// <param name="SentKeyFingerprint">SHA-256 of the account key the request was sent with; polling, uploads and cancellation stay bound to it.</param>
/// <param name="ExpiresUTC">The hosted expiry (one month) once sent.</param>
/// <param name="RemoteCancelledUTC">When the hosted copy confirmed a local cancellation; null until then.</param>
/// <param name="SessionsQueuedUTC">When every attached session was queued for upload under the sending key; null while a send is still to be finished.</param>
/// <param name="SendAttemptedUTC">
/// When the first hosted POST of this request began, written with <paramref name="SentKeyFingerprint"/> before the
/// transfer (VIBE-212). Set without a <paramref name="RemoteId"/>, it means the host may hold a copy whose
/// reference was never received: the request is not deleted, and the poller re-posts the stored bundle
/// (an exact retry the host answers with the stored request) under that fingerprint to recover it. Null
/// again after the host confirmed it refused the bundle.
/// </param>
public sealed record ReviewRequestRecord(
    string Id,
    string ProjectPath,
    string Scope,
    string TreeId,
    string? BaseCommit,
    string? CardKey,
    string Status,
    string BundleJson,
    DateTime CreatedUTC,
    DateTime UpdatedUTC,
    DateTime? SentUTC = null,
    string? RemoteId = null,
    string? SentKeyFingerprint = null,
    DateTime? ExpiresUTC = null,
    DateTime? RemoteCancelledUTC = null,
    DateTime? SessionsQueuedUTC = null,
    DateTime? SendAttemptedUTC = null)
{
    /// <summary>A send began and its answer never arrived: the host may hold a copy nothing here references yet.</summary>
    public bool SendUncertain => RemoteId is null && SendAttemptedUTC is not null;
}

/// <summary>
/// One reviewer's answer. An approval carries the signature trailer fields it was verified from;
/// <see cref="VerifiedUTC"/> is set only after viberails.ai confirmed the signature and the signer's
/// verified email, so the commit hook can trust a row it reads back without a second round trip
/// before the recheck.
/// </summary>
/// <param name="Decision"><c>approved</c> or <c>rejected</c>.</param>
/// <param name="RemoteId">The hosted decision id when the decision arrived through polling, so a later poll does not record it twice.</param>
/// <param name="LineCommentsJson">The reviewer's per-line comments as JSON (<c>[{ path, line, side, text }]</c>), or null when there are none (VB-50 slice F).</param>
public sealed record ReviewDecisionRecord(
    long Id,
    string RequestId,
    string Reviewer,
    string Decision,
    string? Comment,
    Guid? KeyId,
    string? Fingerprint,
    string? TreeId,
    string? SignatureBase64,
    DateTime DecidedUTC,
    DateTime? VerifiedUTC,
    long? RemoteId = null,
    string? LineCommentsJson = null);

/// <summary>A stored decision and the request status it produced.</summary>
public sealed record ReviewDecisionOutcome(ReviewDecisionRecord Decision, string Status);

/// <summary>Durable review requests and decisions in state.db (component <c>review-requests</c>).</summary>
public interface IReviewRequestStore
{
    /// <summary>Adds a new request. The id must be unique.</summary>
    Task AddAsync(ReviewRequestRecord request, CancellationToken ct = default);

    /// <summary>The request with this id, or null.</summary>
    Task<ReviewRequestRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Requests for one project, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ReviewRequestRecord>> ListAsync(string projectPath, int limit, CancellationToken ct = default);

    /// <summary>Changes the status and stamps the update time. False when the request does not exist.</summary>
    Task<bool> UpdateStatusAsync(string id, string status, DateTime updatedUtc, CancellationToken ct = default);

    /// <summary>Removes the request and its decisions. False when it did not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Records a decision and recomputes the request status in one transaction: approved by any
    /// verified approval, otherwise rejected by any rejection, otherwise open. Null when the request
    /// is missing or cancelled, in which case nothing is stored: a decision that was still being
    /// verified when the request was cancelled must not revive it.
    /// </summary>
    Task<ReviewDecisionOutcome?> RecordDecisionAsync(ReviewDecisionRecord decision, DateTime updatedUtc, CancellationToken ct = default);

    /// <summary>Decisions for one request, oldest first.</summary>
    Task<IReadOnlyList<ReviewDecisionRecord>> GetDecisionsAsync(string requestId, CancellationToken ct = default);

    /// <summary>
    /// Verified approvals whose signature covers <paramref name="treeId"/>, newest first, across every
    /// request that is not cancelled. The commit hook filters these by the rule's approvers.
    /// </summary>
    Task<IReadOnlyList<ReviewDecisionRecord>> FindVerifiedApprovalsAsync(string treeId, CancellationToken ct = default);

    /// <summary>
    /// Records that viberails.ai accepted the request: the hosted reference, the fingerprint of the
    /// key it was sent with, the send time and the hosted expiry. The reference is stored whatever the
    /// status, so a request cancelled while its send was in flight can still revoke the hosted copy.
    /// Returns the request's status as of that write, or null when the request does not exist or was
    /// already sent with a different reference, in which case nothing changes.
    /// </summary>
    Task<string?> MarkSentAsync(string id, string remoteId, string keyFingerprint, DateTime sentUtc, DateTime expiresUtc, CancellationToken ct = default);

    /// <summary>Records that the hosted copy confirmed a local cancellation. False when the request does not exist.</summary>
    Task<bool> MarkRemoteCancelledAsync(string id, DateTime cancelledUtc, CancellationToken ct = default);

    /// <summary>Records that every attached session was queued for upload. False when the request does not exist.</summary>
    Task<bool> MarkSessionsQueuedAsync(string id, DateTime queuedUtc, CancellationToken ct = default);

    /// <summary>
    /// Claims a send before the hosted POST (VIBE-212): records when the first attempt began (an
    /// earlier time is kept) and pins the fingerprint of the key it is made with. False, changing
    /// nothing, when the request does not exist, already has a hosted reference, or is pinned to a
    /// different fingerprint: only the account that began a send may retry or clear it, so a second
    /// send under another key cannot create a second hosted copy.
    /// </summary>
    Task<bool> MarkSendAttemptAsync(string id, string keyFingerprint, DateTime attemptedUtc, CancellationToken ct = default);

    /// <summary>
    /// Forgets a send attempt after the host confirmed it refused the bundle, so the request is an
    /// unsent one again. Only the attempt the refusal answered is cleared: the stored attempt time and
    /// pinned fingerprint must still be <paramref name="attemptedUtc"/> and <paramref name="keyFingerprint"/>.
    /// False, changing nothing, when the request does not exist, already has a hosted reference, or
    /// carries another attempt.
    /// </summary>
    Task<bool> ClearSendAttemptAsync(string id, string keyFingerprint, DateTime attemptedUtc, DateTime updatedUtc, CancellationToken ct = default);

    /// <summary>
    /// Requests whose send began but never received a hosted reference, across every project and
    /// whatever their status, oldest attempt first. The poller re-posts each one under its pinned
    /// fingerprint; <see cref="ListSentPendingAsync"/> cannot see them because they have no reference.
    /// </summary>
    Task<IReadOnlyList<ReviewRequestRecord>> ListSendAttemptsPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Sent requests that still need the host, across every project: every one that is not cancelled
    /// (decisions are collected until the hosted expiry, whatever the current status, because the host
    /// keeps taking them) and cancelled ones whose hosted copy has not confirmed the cancellation yet.
    /// A request whose hosted expiry is before <paramref name="expiresAfterUtc"/> is left out: nothing
    /// there can still take a decision or be read. Oldest first.
    /// </summary>
    Task<IReadOnlyList<ReviewRequestRecord>> ListSentPendingAsync(DateTime expiresAfterUtc, CancellationToken ct = default);

    /// <summary>Hosted decision ids already recorded for one request, so polling records each decision once.</summary>
    Task<IReadOnlySet<long>> GetRemoteDecisionIdsAsync(string requestId, CancellationToken ct = default);
}
