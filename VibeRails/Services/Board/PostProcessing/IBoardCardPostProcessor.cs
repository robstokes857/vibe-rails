namespace VibeRails.Services.Board.PostProcessing;

/// <summary>
/// One rule that runs once on a card after it settles in its board's last lane (VIBE-185). Every
/// registered processor sees each card in the same pass; their outcomes are applied together with
/// the card's processed mark, so a card is never processed twice. Register a new processor with
/// <c>AddSingleton&lt;IBoardCardPostProcessor, …&gt;</c>; <see cref="BoardCardPostProcessingService"/>
/// picks up every registration. A processor reads more through <see cref="IBoardStore"/> if the
/// candidate does not carry what it needs, and never writes the card itself.
/// </summary>
public interface IBoardCardPostProcessor
{
    /// <summary>Short name for logs.</summary>
    string Name { get; }

    Task<BoardCardPostProcessorOutcome> ProcessAsync(BoardPostProcessingCandidate card, CancellationToken cancellationToken);
}

/// <summary>
/// What a processor decided. <see cref="Defer"/> leaves the whole card for a later pass, until
/// <see cref="BoardCardPostProcessingService.GiveUpAfter"/>, when it is processed without that
/// processor's outcome: a transient failure must neither block the card forever nor mark it.
/// </summary>
public sealed record BoardCardPostProcessorOutcome(bool Defer = false, string? GhostReason = null, string? Note = null)
{
    public static BoardCardPostProcessorOutcome Nothing { get; } = new();
    public static BoardCardPostProcessorOutcome Ghost(string reason) => new(GhostReason: reason);
    public static BoardCardPostProcessorOutcome Later(string why) => new(Defer: true, Note: why);
}
