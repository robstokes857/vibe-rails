using Serilog;
using VibeRails.DB;
using VibeRails.Services.ReviewRequests;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>A verified approval the commit-msg hook can place in the message instead of opening the popup.</summary>
public sealed record StoredCommitApproval(CommitSignature Signature, string SignerEmail, string RequestId);

/// <summary>Where the hook looks for approvals a reviewer already gave over the staged tree (VB-50).</summary>
public interface IStoredCommitApprovalSource
{
    /// <summary>
    /// The newest verified approval over <see cref="HumanSignatureRequest.TreeId"/> by one of the
    /// request's approvers (any verified signer when the list is empty), or null.
    /// </summary>
    Task<StoredCommitApproval?> FindAsync(HumanSignatureRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the review-request ledger in state.db. Bounded to a few seconds and quiet on any failure:
/// the ledger is a convenience, so a missing or busy database must never stand between a person and
/// the popup, and the hook re-verifies every signature it applies from here with viberails.ai.
/// </summary>
public sealed class ReviewRequestApprovalSource(IReviewRequestStore store) : IStoredCommitApprovalSource
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public async Task<StoredCommitApproval?> FindAsync(HumanSignatureRequest request, CancellationToken cancellationToken)
    {
        if (!CommitSignatureFormat.IsObjectId(request.TreeId)) return null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            var approvals = await store.FindVerifiedApprovalsAsync(request.TreeId, budget.Token);
            var row = ReviewRequestService.SelectApproval(approvals, request.Approvers);
            return row is null
                ? null
                : new StoredCommitApproval(
                    new CommitSignature(row.KeyId!.Value, row.Fingerprint!, row.TreeId!, row.SignatureBase64!),
                    row.Reviewer,
                    row.RequestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Git Guard could not read stored review approvals for tree {Tree}", request.TreeId[..12]);
            return null;
        }
    }
}
