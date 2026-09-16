namespace VibeRails.Services.BertV2;

public interface IBertV2SessionVectorStore : IDisposable
{
    /// <summary>Checks that the complete ordered chunk set and every embedding are stored.</summary>
    bool ContainsCurrentSession(string sessionId, IReadOnlyList<string> chunks);

    void AddOrUpdate(string sessionId, int chunkIndex, string text, float[] embedding);

    /// <summary>
    /// Replace every chunk for <paramref name="sessionId"/> atomically. Either the
    /// session ends with exactly the chunks supplied or it ends with the chunks it
    /// had before — never a partial mix.
    /// </summary>
    void ReplaceSession(string sessionId, IReadOnlyList<BertSessionChunkWrite> chunks);
}
