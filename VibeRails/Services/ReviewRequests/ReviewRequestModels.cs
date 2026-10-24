using System.Text.Json.Serialization;

namespace VibeRails.Services.ReviewRequests;

/// <summary>Request lifecycle. A request is approved by any verified approval and rejected by any rejection without one.</summary>
public static class ReviewRequestStatus
{
    public const string Open = "open";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
}

/// <summary>What a request covers. Only the staged index is captured in this version.</summary>
public static class ReviewScope
{
    public const string Staged = "staged";
}

/// <summary>A reviewer's answer kinds.</summary>
public static class ReviewDecisionKind
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>Why a file's content is not in the bundle: it is binary, over the per-file limit, or the bundle limit was reached.</summary>
public static class ReviewBundleOmission
{
    public const string Binary = "binary";
    public const string Oversized = "oversized";
    public const string BundleLimit = "bundle-limit";
}

/// <summary>The app event the card UI listens for: a request was sent, cancelled or received a decision.</summary>
public static class ReviewRequestEvents
{
    public const string Updated = "review_request_updated";
}

/// <summary>
/// One changed file as the reviewer sees it. <see cref="Before"/> and <see cref="After"/> are the
/// base and staged text; both are null when <see cref="Omitted"/> says why.
/// </summary>
public sealed record ReviewBundleFile(
    string Path,
    string Change,
    bool Binary,
    string? PreviousPath,
    int? ChangedLines,
    string? Before,
    string? After,
    string? Omitted);

/// <summary>A terminal session the developer attached so the reviewer can see how the change was made.</summary>
public sealed record ReviewBundleSession(string Id, string Name, string Cli, DateTime StartedUtc, DateTime? EndedUtc);

/// <summary>
/// Everything a reviewer reads. The tree id is what an approval signs; the files are the content
/// of that tree's difference from its base, bounded, so the bundle is evidence, not the statement.
/// </summary>
public sealed record ReviewBundle(
    int Schema,
    string Repository,
    string Scope,
    string TreeId,
    string BaseTree,
    string? BaseCommit,
    IReadOnlyList<ReviewBundleFile> Files,
    IReadOnlyList<ReviewBundleSession> Sessions,
    IReadOnlyList<string> Reviewers,
    string? CardKey,
    string? Note,
    DateTime CreatedUtc);

/// <summary>A recorded decision. <see cref="Trailer"/> is the approval's commit-message line, ready to paste.</summary>
public sealed record ReviewDecisionDto(
    long Id,
    string Reviewer,
    string Decision,
    string? Comment,
    string? Trailer,
    DateTime DecidedUtc,
    DateTime? VerifiedUtc);

/// <summary>
/// The hosted copy of a sent request: its viberails.ai reference (a selector, never a credential),
/// when it was sent, when reviewers lose access, and when the host confirmed a local cancellation.
/// </summary>
public sealed record ReviewRequestRemote(string Id, DateTime SentUtc, DateTime? ExpiresUtc, DateTime? CancelledUtc);

public sealed record ReviewRequestSummary(
    string Id,
    string Scope,
    string TreeId,
    string? BaseCommit,
    string Status,
    IReadOnlyList<string> Reviewers,
    string? CardKey,
    int FileCount,
    int SessionCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<ReviewDecisionDto> Decisions,
    ReviewRequestRemote? Remote = null);

public sealed record ReviewRequestDetail(ReviewRequestSummary Request, ReviewBundle Bundle);

public sealed record ReviewRequestList(IReadOnlyList<ReviewRequestSummary> Requests);

/// <summary>Body of <c>POST /api/v1/review-requests</c>.</summary>
public sealed record CreateReviewRequest(
    string? Scope = null,
    IReadOnlyList<string>? Reviewers = null,
    IReadOnlyList<string>? Sessions = null,
    string? CardKey = null,
    string? Note = null);

/// <summary>
/// Body of <c>POST /api/v1/review-requests/{id}/decisions</c>. An approval is the reviewer's
/// <c>vibe-signature:</c> line (the reviewer is whoever viberails.ai says signed it); a rejection
/// names the reviewer and carries no signature.
/// </summary>
public sealed record RecordReviewDecisionRequest(
    string? Decision = null,
    string? Trailer = null,
    string? Comment = null,
    string? Reviewer = null);

/// <summary>Body of <c>POST /api/v1/review-requests/{id}/send</c>. Nothing leaves the computer unless <see cref="Confirmed"/> is true.</summary>
public sealed record SendReviewRequest(bool? Confirmed = null);

/// <summary>Answer to a send: the request as stored, and what happens next in fixed local wording.</summary>
public sealed record ReviewRequestSent(ReviewRequestSummary Request, string Message);

/// <summary>One file as the disclosure lists it: what the reviewer gets, or why its text is not included.</summary>
public sealed record ReviewDisclosureFile(string Path, string Change, string? PreviousPath, int? ChangedLines, bool TextIncluded, string? Omitted);

/// <summary>One attached session as the disclosure lists it. A live session uploads only after it ends.</summary>
public sealed record ReviewDisclosureSession(string Id, string Name, string Cli, DateTime StartedUtc, DateTime? EndedUtc, bool Live);

/// <summary>
/// Exactly what leaves this computer when a request is sent, so the developer confirms it knowingly:
/// the destination, every file with its counts and omissions, the tree id, each session by name
/// with the sharing disclosure, the reviewer addresses (the only people who can open it) and the
/// expiry. Nothing in it is a secret; it is the manifest of the send.
/// </summary>
public sealed record ReviewRequestDisclosure(
    string Id,
    string Destination,
    string Audience,
    string Repository,
    string TreeId,
    string BaseTree,
    string? BaseCommit,
    IReadOnlyList<ReviewDisclosureFile> Files,
    int FilesWithText,
    int FilesWithoutText,
    long TextBytes,
    IReadOnlyList<ReviewDisclosureSession> Sessions,
    string SessionDisclosure,
    IReadOnlyList<string> Reviewers,
    string? CardKey,
    string? Note,
    DateTime ExpiresUtc,
    bool Sent,
    ReviewRequestRemote? Remote);

/// <summary>Payload of <see cref="ReviewRequestEvents.Updated"/>.</summary>
public sealed record ReviewRequestChanged(string Id, string Status, string? CardKey, bool Sent);

/// <summary>A decision as viberails.ai reports it to the requester's poll.</summary>
public sealed record ReviewRequestHostedDecision(
    long Id,
    string? Reviewer,
    string? Decision,
    string? Comment,
    string? Trailer,
    DateTime DecidedUtc,
    DateTime? VerifiedUtc);

/// <summary>
/// viberails.ai's answer to create, poll and cancel. <see cref="Status"/> adds <c>expired</c> to the
/// local states. Every field is validated by <see cref="ReviewRequestTransport.ParseStatus"/> before use.
/// </summary>
public sealed record ReviewRequestHostedStatus(
    string? Id,
    Guid? RequestId,
    string? TreeId,
    string? Status,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<ReviewRequestHostedDecision>? Decisions);

/// <summary>The only part of a hosted error body that is read: a short code that selects fixed local wording.</summary>
public sealed record ReviewRequestHostedError(string? Code);

/// <summary>On-disk shape of <see cref="ReviewRequestPollState"/>: an account fingerprint and two numbers, never a key.</summary>
public sealed class ReviewRequestPollFile
{
    public string Account { get; set; } = "";
    public DateTime NextDueUtc { get; set; }
    public long DelayTicks { get; set; }
}

public sealed record ReviewRequestError(string Error, string Code);

/// <summary>A refused operation, with the HTTP status the route answers.</summary>
public sealed class ReviewRequestException(string code, string message, int status) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReviewBundle))]
[JsonSerializable(typeof(ReviewRequestSummary))]
[JsonSerializable(typeof(ReviewRequestDetail))]
[JsonSerializable(typeof(ReviewRequestList))]
[JsonSerializable(typeof(CreateReviewRequest))]
[JsonSerializable(typeof(RecordReviewDecisionRequest))]
[JsonSerializable(typeof(SendReviewRequest))]
[JsonSerializable(typeof(ReviewRequestSent))]
[JsonSerializable(typeof(ReviewRequestDisclosure))]
[JsonSerializable(typeof(ReviewRequestChanged))]
[JsonSerializable(typeof(ReviewRequestHostedStatus))]
[JsonSerializable(typeof(ReviewRequestHostedError))]
[JsonSerializable(typeof(ReviewRequestPollFile))]
[JsonSerializable(typeof(ReviewRequestError))]
public sealed partial class ReviewRequestJsonContext : JsonSerializerContext;
