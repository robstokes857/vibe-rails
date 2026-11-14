namespace VibeRails.Services.Board;

public partial interface IBoardStore
{
    /// <summary>
    /// An opaque value that moves whenever any writer (this process, another root, an older binary,
    /// tracker or hosted sync) changes what the Board poll or the card editor reads (VIBE-322).
    /// An equal value means none of that has changed since it was read. Null means the store cannot
    /// tell, and the caller rereads everything.
    /// </summary>
    Task<string?> GetChangeStampAsync(CancellationToken cancellationToken = default);
}
