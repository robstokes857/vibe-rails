using System.Text.Json.Serialization;

namespace VibeRails.Services.ReviewRequests;

/// <summary>The app event the inbox page listens for: the list was refreshed, or a decision or signing changed one entry.</summary>
public static class ReviewInboxEvents
{
    public const string Updated = "review_inbox_updated";
}

/// <summary>
/// One request addressed to this account, as viberails.ai lists it (<c>GET /api/v1/review-requests/inbox</c>):
/// no bundle text, no other reviewers. <see cref="Decided"/> is whether this account already answered.
/// </summary>
public sealed record ReviewInboxHostedItem(
    string Id,
    string Repository,
    string RequestedBy,
    string Status,
    int FileCount,
    int SessionCount,
    string? CardKey,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    bool Decided);

/// <summary>The hosted inbox answer. <see cref="EmailVerified"/> is false when the account has no verified email, so nothing can be addressed to it.</summary>
public sealed record ReviewInboxHosted(bool EmailVerified, IReadOnlyList<ReviewInboxHostedItem>? Requests, bool More);

/// <summary>An attached session as viberails.ai shows it to a reviewer: a position, a label and whether its recording uploaded.</summary>
public sealed record ReviewHostedSession(int Number, string? Name, string? Cli, DateTime StartedUtc, DateTime? EndedUtc, string? Status);

/// <summary>
/// One request as a listed reviewer reads it (<c>GET /api/v1/review-requests/{reference}/bundle</c>): the
/// files with their base and staged text or omission reason, the sessions by position, the statement an
/// approval signs, and this account's own decisions. Session ids and the reviewer list are not in it.
/// </summary>
public sealed record ReviewHostedBundle(
    string Id,
    string Repository,
    string RequestedBy,
    string Status,
    string TreeId,
    string BaseTree,
    string? BaseCommit,
    string? CardKey,
    string? Note,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    string? Statement,
    bool CanDecide,
    IReadOnlyList<ReviewBundleFile>? Files,
    IReadOnlyList<ReviewHostedSession>? Sessions,
    IReadOnlyList<ReviewRequestHostedDecision>? YourDecisions);

/// <summary>Body of the reviewer's <c>POST /{reference}/decisions</c>: a rejection with a comment, or an approval with the signed trailer.</summary>
public sealed record ReviewInboxDecisionBody(string Decision, string? Trailer, string? Comment);

/// <summary>
/// On-disk shape of <see cref="ReviewInboxState"/>: the account fingerprint, the pacing numbers and the
/// last list as viberails.ai gave it (repository names, requester addresses, references; never a key,
/// file text or a note). Lives beside state.db so every root backend serves the same list.
/// </summary>
public sealed class ReviewInboxFile
{
    public string Account { get; set; } = "";
    public DateTime? FetchedUtc { get; set; }
    public DateTime NextDueUtc { get; set; }
    public long DelayTicks { get; set; }
    public bool EmailVerified { get; set; }
    public List<ReviewInboxHostedItem> Requests { get; set; } = [];
    public bool More { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastError { get; set; }
}

/// <summary>A signing popup this root opened for one request, and how it ended. <see cref="Outcome"/>: <c>signing</c>, <c>approved</c>, <c>not_approved</c>, <c>popup_unavailable</c>.</summary>
public sealed record ReviewInboxSigning(string Reference, DateTime StartedUtc, DateTime? FinishedUtc, int? ExitCode, string Outcome);

/// <summary>One inbox row as the page shows it: the hosted item plus this root's signing state for it.</summary>
public sealed record ReviewInboxItem(
    string Id,
    string Repository,
    string RequestedBy,
    string Status,
    int FileCount,
    int SessionCount,
    string? CardKey,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    bool Decided,
    string ReadUrl,
    ReviewInboxSigning? Signing);

/// <summary>
/// What the "Reviews requested of you" page reads. <see cref="Email"/> is the account's verified email
/// (null when no key is saved or the key's identity is unknown); <see cref="PopupAvailable"/> says
/// whether this machine can open the signing popup (Windows, an interactive desktop, not CI).
/// </summary>
public sealed record ReviewInboxView(
    bool HasApiKey,
    string? Email,
    bool EmailVerified,
    DateTime? FetchedUtc,
    DateTime? NextDueUtc,
    string? LastErrorCode,
    string? LastError,
    IReadOnlyList<ReviewInboxItem> Requests,
    bool More,
    string Project,
    bool PopupAvailable);

/// <summary>Body of <c>POST /api/v1/review-inbox/{reference}/reject</c>.</summary>
public sealed record ReviewInboxRejectRequest(string? Comment = null);

/// <summary>Answer to reject and approve: the entry's status afterwards and, for approve, the popup's state.</summary>
public sealed record ReviewInboxActionResult(string Reference, string Status, string Message, ReviewInboxSigning? Signing = null);

/// <summary>Payload of <see cref="ReviewInboxEvents.Updated"/>: when the list or a signing state changed. The page re-reads the list.</summary>
public sealed record ReviewInboxChanged(DateTime AtUtc);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReviewInboxHosted))]
[JsonSerializable(typeof(ReviewHostedBundle))]
[JsonSerializable(typeof(ReviewInboxDecisionBody))]
[JsonSerializable(typeof(ReviewRequestHostedDecision))]
[JsonSerializable(typeof(ReviewInboxFile))]
[JsonSerializable(typeof(ReviewInboxView))]
[JsonSerializable(typeof(ReviewInboxRejectRequest))]
[JsonSerializable(typeof(ReviewInboxActionResult))]
[JsonSerializable(typeof(ReviewInboxChanged))]
[JsonSerializable(typeof(ReviewRequestError))]
public sealed partial class ReviewInboxJsonContext : JsonSerializerContext;
