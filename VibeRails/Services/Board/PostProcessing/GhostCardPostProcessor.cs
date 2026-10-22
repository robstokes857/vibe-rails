namespace VibeRails.Services.Board.PostProcessing;

/// <summary>
/// The first post-processor (VIBE-185): a finished card that an agent worked on but whose code
/// never reached the default branch gets a ghost, which the lane tile animates until the user
/// clears it in Advanced. Owner rules: Research Spikes never get one, a lane or board named like
/// Canceled / Not Doing never does, a session must be attached, and a GitHub PR or commit link
/// counts as code. Linked commits count only once one is on the project's default branch.
/// Anything that cannot be checked (no checkout, no default branch) is not a ghost.
/// </summary>
public sealed class GhostCardPostProcessor(IBoardDefaultBranchCheck defaultBranch) : IBoardCardPostProcessor
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
            || card.SessionCount == 0 || card.CodeLinkCount > 0)
            return BoardCardPostProcessorOutcome.Nothing;
        if (card.CommitShas.Count == 0)
            return BoardCardPostProcessorOutcome.Ghost(BoardGhostReasons.NoCode);

        return await defaultBranch.CheckAsync(card.ProjectPath, card.CommitShas, cancellationToken) switch
        {
            BoardCommitLanding.NotLanded => BoardCardPostProcessorOutcome.Ghost(BoardGhostReasons.NotLanded),
            BoardCommitLanding.Failed => BoardCardPostProcessorOutcome.Later("git could not check the linked commits"),
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
