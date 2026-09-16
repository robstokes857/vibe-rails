using VibeRails.Services.UserInOut;

namespace VibeRails.Services.BertV2;

public class BertV2SessionEmbeddingService : IBertV2SessionEmbeddingService
{
    // Tokenizer truncates at 512 tokens (~2000 chars for English). Sized below that
    // ceiling so we don't silently lose the tail of every window to truncation.
    private const int WindowChars = 1600;
    // 50% overlap — concepts spanning a window boundary get embedded by both windows.
    private const int StrideChars = 800;
    private const string MessageSeparator = "\n\n";

    private readonly IBertV2BgeEmbedder _embedder;
    private readonly IBertV2SessionVectorStore _store;
    private readonly Lock _captureLock = new();

    public BertV2SessionEmbeddingService(IBertV2BgeEmbedder embedder, IBertV2SessionVectorStore store)
    {
        _embedder = embedder;
        _store = store;
    }

    public int CaptureSession(string sessionId, IReadOnlyList<string> orderedInputTexts)
    {
        if (orderedInputTexts.Count == 0)
            return 0;

        // Apply the same secret/noise filter the per-message embedding path uses, on
        // a per-input basis: a single rogue line (e.g. an env var assignment with a
        // token) is dropped before it can land in the concatenated session chunk.
        var filtered = new List<string>(orderedInputTexts.Count);
        foreach (var raw in orderedInputTexts)
        {
            var safe = InputEtlFilter.Process(raw);
            if (!string.IsNullOrWhiteSpace(safe))
                filtered.Add(safe);
        }
        if (filtered.Count == 0)
            return 0;

        var concatenated = string.Join(MessageSeparator, filtered).Trim();
        if (string.IsNullOrWhiteSpace(concatenated))
            return 0;

        var chunks = ComputeChunks(concatenated);
        if (chunks.Count == 0)
            return 0;

        lock (_captureLock)
        {
            // Recover from state-marker contention without repeating inference.
            // Matching includes chunk order, exact text and every vector's presence.
            if (_store.ContainsCurrentSession(sessionId, chunks))
                return chunks.Count;

            // Prepare every vector first; ReplaceSession commits all chunks atomically.
            var prepared = new List<BertSessionChunkWrite>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
                prepared.Add(new BertSessionChunkWrite(i, chunks[i], _embedder.GenerateEmbedding(chunks[i])));
            _store.ReplaceSession(sessionId, prepared);
        }
        return chunks.Count;
    }

    internal static List<string> ComputeChunks(string text)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text))
            return chunks;

        if (text.Length <= WindowChars)
        {
            chunks.Add(text);
            return chunks;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(WindowChars, text.Length - start);
            chunks.Add(text.Substring(start, length));
            if (start + length >= text.Length)
                break;
            start += StrideChars;
        }
        return chunks;
    }
}
