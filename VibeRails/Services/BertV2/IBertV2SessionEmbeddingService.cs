namespace VibeRails.Services.BertV2;

public interface IBertV2SessionEmbeddingService
{
    /// <summary>
    /// Concatenates the ordered input texts, slides a fixed-size window over the result,
    /// and stores one embedding per chunk keyed (sessionId, chunkIndex). Returns the
    /// number of chunks written; zero means the session had no embeddable content.
    /// </summary>
    int CaptureSession(string sessionId, IReadOnlyList<string> orderedInputTexts);
}
