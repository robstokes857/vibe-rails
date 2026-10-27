namespace VibeRails.Services.Board.PostProcessing;

/// <summary>
/// The first post-processor (VIBE-185): a finished card that an agent worked on but whose code
/// never reached the default branch gets a ghost, which the lane tile animates until the user
/// clears it in Advanced or linked evidence is verified. Research Spikes never get one, a lane or
/// board named like Canceled / Not Doing never does, and a session must be attached. Commits count
/// on the local or remote-tracking default branch; PRs must merge into the owning default branch.
/// Anything that cannot be checked (no checkout, no default branch) is not a ghost.
/// </summary>
public sealed class GhostCardPostProcessor(IBoardDefaultBranchCheck defaultBranch, IBoardCodeLinkLandingCheck? codeLinks = null) : IBoardCardPostProcessor
{
    // Compared after lower-casing and dropping everything but letters, so "Won't Do", "WONT-DO"
    // and "Not doing" all match.
    private static readonly string[] NotDoingWords =
    [
        "cancel", "notdoing", "wontdo", "willnotdo", "wontfix", "willnotfix", "notplanned",
        "abandon", "reject", "decline", "dropped", "discard", "obsolete", "duplicate"
    ];

    public string Name => "ghost";

    public async Task<BoardCardPostProcessorOutcome> ProcessAsync(BoardPostProcessingCandidate card, CancellationToken cancellationToken)
    {
        if (string.Equals(card.Type, BoardCardTypes.ResearchSpike, StringComparison.Ordinal)
            || IsNotDoing(card.LaneName) || IsNotDoing(card.BoardName)
            || card.SessionCount == 0)
            return BoardCardPostProcessorOutcome.Nothing;
        if (card.CommitShas.Count == 0 && card.CodeLinkCount == 0)
            return BoardCardPostProcessorOutcome.Ghost(BoardGhostReasons.NoCode);
        var landing = card.CommitShas.Count == 0 ? BoardCommitLanding.NotLanded
            : await defaultBranch.CheckAsync(card.ProjectPath, card.CommitShas, cancellationToken);
        if (landing != BoardCommitLanding.Landed && card.CodeLinkCount > 0)
        {
            var linked = codeLinks is null ? BoardCommitLanding.Unverifiable
                : await codeLinks.CheckAsync(card.ProjectPath, card.CodeLinks, cancellationToken);
            // Landed wins; a retryable Git failure is never downgraded to a final "unverifiable".
            if (linked is BoardCommitLanding.Landed or BoardCommitLanding.Failed) landing = linked;
            else if (linked == BoardCommitLanding.Unverifiable && landing != BoardCommitLanding.Failed) landing = linked;
        }
        return landing switch
        {
            BoardCommitLanding.NotLanded => BoardCardPostProcessorOutcome.Ghost(BoardGhostReasons.NotLanded),
            BoardCommitLanding.Failed => BoardCardPostProcessorOutcome.Later("git could not check the linked commits"),
            BoardCommitLanding.Unverifiable => BoardCardPostProcessorOutcome.Unverified("code landing could not be verified"),
            _ => BoardCardPostProcessorOutcome.Nothing
        };
    }

    /// <summary>True for a lane or board name that means the work was deliberately not done.</summary>
    internal static bool IsNotDoing(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var letters = new string(name.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
        return NotDoingWords.Any(word => letters.Contains(word, StringComparison.Ordinal));
    }
}
