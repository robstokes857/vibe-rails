namespace VibeRails.Services.BertV2;

public class BertV2InputService : IBertV2InputService
{
    private readonly IBertV2BgeEmbedder _embedder;
    private readonly IBertV2VectorStore _store;
    private readonly Lock _writeLock = new();

    public BertV2InputService(IBertV2BgeEmbedder embedder, IBertV2VectorStore store)
    {
        _embedder = embedder;
        _store = store;
    }

    public void Capture(string sessionId, long userInputId, string inputText)
    {
        if (string.IsNullOrWhiteSpace(inputText))
            return;

        var captureText = SanitizeText(inputText);
        if (string.IsNullOrWhiteSpace(captureText))
            return;

        var embedding = _embedder.GenerateEmbedding(captureText);

        lock (_writeLock)
        {
            _store.AddOrUpdate(BertDocumentId.Create(sessionId, userInputId), captureText, embedding);
        }
    }

    private static string SanitizeText(string value)
    {
        return value.Replace("\0", string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();
    }
}
