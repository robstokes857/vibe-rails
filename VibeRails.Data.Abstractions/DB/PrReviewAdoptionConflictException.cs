namespace VibeRails.DB;

/// <summary>Legacy and dedicated PR history both changed since the last automatic adoption.</summary>
public sealed class PrReviewAdoptionConflictException(int number) : Exception(
    $"PR #{number} changed in both the legacy and dedicated review databases. " +
    "Both copies are preserved; automatic adoption stopped to avoid overwriting review history. " +
    "Reconcile this PR's edits before continuing with this repository.")
{
    /// <summary>The PR whose histories diverged.</summary>
    public int Number { get; } = number;
}
