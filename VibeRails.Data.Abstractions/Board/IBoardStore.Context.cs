namespace VibeRails.Services.Board;

/// <summary>
/// Agent-context samples (VB-63, board/19): how many tokens a card put in front of an agent at
/// each launch, kept so context growth can be tracked and later trimmed. Recording a sample also
/// appends a Card Log <c>change</c> entry carrying the numbers under a <c>context</c> field, so
/// the measurement travels with Board sync and shows in History like any other change.
/// </summary>
public partial interface IBoardStore
{
    /// <summary>Records a launch measurement for the card; null when the card is not in the project.</summary>
    Task<BoardContextSampleRecord?> RecordContextSampleAsync(string projectPath, string cardId, NewBoardContextSample sample, CancellationToken cancellationToken = default);

    /// <summary>The most recent recorded sample for the card, if any.</summary>
    Task<BoardContextSampleRecord?> GetLatestContextSampleAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
}
