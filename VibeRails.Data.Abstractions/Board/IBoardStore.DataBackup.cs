namespace VibeRails.Services.Board;

/// <summary>One complete, content-addressed row or binary part in a coherent Board backup.</summary>
public sealed record BoardDataBackupItem(string Sha256, string? Json, byte[]? Content);

public partial interface IBoardStore
{
    /// <summary>
    /// Reads every retained Board table, including unpublished boards and deleted cards, in one
    /// deferred snapshot. Large values are emitted as bounded content parts before their rows.
    /// No hosted-UI limits or sync state determine coverage. The callback must not mutate this store.
    /// </summary>
    Task ReadDataBackupAsync(Func<BoardDataBackupItem, CancellationToken, Task> emit,
        CancellationToken cancellationToken = default);
}
